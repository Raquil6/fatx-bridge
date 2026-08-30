using System.Globalization;
using System.IO;
using System.Text;

namespace FatxBridge.Core;

/// <summary>The context used to describe a bounded, read-only storage view.</summary>
public enum RawStorageViewKind
{
    RawRange,
    Sector,
    FatxHeader,
    FatxAllocationTable,
    FatxCluster
}

/// <summary>
/// An immutable description of bytes read by <see cref="RawStorageInspector"/>.
/// The byte array returned by <see cref="Bytes"/> is a copy, so callers cannot
/// mutate the inspector's result or the source stream through this model.
/// </summary>
public sealed class RawStorageView
{
    private readonly byte[] bytes;

    internal RawStorageView(
        RawStorageViewKind kind,
        long absoluteOffset,
        long requestedLength,
        long sourceLength,
        int logicalSectorSize,
        long? partitionRelativeOffset,
        long? sectorNumber,
        uint? clusterNumber,
        bool isTruncated,
        bool isAtEnd,
        byte[] bytes)
    {
        Kind = kind;
        AbsoluteOffset = absoluteOffset;
        RequestedLength = requestedLength;
        SourceLength = sourceLength;
        LogicalSectorSize = logicalSectorSize;
        PartitionRelativeOffset = partitionRelativeOffset;
        SectorNumber = sectorNumber;
        ClusterNumber = clusterNumber;
        IsTruncated = isTruncated;
        IsAtEnd = isAtEnd;
        this.bytes = bytes;
        HexDump = RawStorageInspector.FormatHexDump(bytes, absoluteOffset);
        AsciiColumn = RawStorageInspector.FormatAsciiColumn(bytes);
    }

    public RawStorageViewKind Kind { get; }
    public long AbsoluteOffset { get; }
    public long Offset => AbsoluteOffset;
    public int Length => bytes.Length;
    public int ByteCount => bytes.Length;
    public long RequestedLength { get; }
    public long SourceLength { get; }
    public long EndOffsetExclusive => checked(AbsoluteOffset + bytes.Length);
    public int LogicalSectorSize { get; }
    public long? PartitionRelativeOffset { get; }
    public long? SectorNumber { get; }
    public uint? ClusterNumber { get; }
    public bool IsTruncated { get; }
    public bool Truncated => IsTruncated;
    public bool IsAtEnd { get; }

    /// <summary>A defensive copy of the bytes represented by this view.</summary>
    public byte[] Bytes => bytes.ToArray();

    /// <summary>The complete fixed-address hex dump, including its ASCII column.</summary>
    public string HexDump { get; }

    /// <summary>Alias for <see cref="HexDump"/> useful to text-oriented callers.</summary>
    public string FormattedDump => HexDump;

    /// <summary>The printable ASCII column without addresses or hex bytes.</summary>
    public string AsciiColumn { get; }
}

/// <summary>
/// Performs bounded, read-only inspection over a caller-authorized readable and
/// seekable stream. This type never opens a path, changes stream length, or writes
/// to the supplied stream. The caller remains responsible for stream lifetime and
/// for obtaining authorization to inspect its source.
/// </summary>
public sealed class RawStorageInspector
{
    public const int MaximumViewBytes = 64 * 1024;
    public const int DefaultSectorSize = 512;
    public const int AlternateSectorSize = 4096;
    private const int HeaderViewBytes = 4096;
    private const int BytesPerDumpLine = 16;
    private const int MinimumSafeSectorSize = 512;
    private const int MaximumSafeSectorSize = MaximumViewBytes;

    private readonly Stream source;
    private readonly long sourceLength;
    private readonly int logicalSectorSize;

    /// <summary>
    /// Creates an inspector over an already-authorized stream. <paramref name="sourceLength"/>
    /// is authoritative and may intentionally be shorter than the stream's physical length.
    /// </summary>
    public RawStorageInspector(Stream source, long sourceLength, int logicalSectorSize = DefaultSectorSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("The inspection source must be readable and seekable.", nameof(source));
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);
        ValidateLogicalSectorSize(logicalSectorSize);

        this.source = source;
        this.sourceLength = sourceLength;
        this.logicalSectorSize = logicalSectorSize;
    }

    public long SourceLength => sourceLength;
    public int LogicalSectorSize => logicalSectorSize;

    /// <summary>Reads a raw absolute byte range, capped to one view.</summary>
    public RawStorageView ReadRange(long absoluteOffset, long count) =>
        ReadCore(absoluteOffset, count, RawStorageViewKind.RawRange, null, null, null, null);

    /// <summary>Short alias for <see cref="ReadRange"/>.</summary>
    public RawStorageView Read(long absoluteOffset, long count) => ReadRange(absoluteOffset, count);

    /// <summary>Explicit absolute-offset alias for <see cref="ReadRange"/>.</summary>
    public RawStorageView ReadAt(long absoluteOffset, long count) => ReadRange(absoluteOffset, count);

    /// <summary>Explicitly named alias for callers that want to emphasize raw access.</summary>
    public RawStorageView ReadRawRange(long absoluteOffset, long count) => ReadRange(absoluteOffset, count);

    /// <summary>
    /// Reads one or more logical sectors. A request larger than 64 KiB is valid
    /// when it is within the source and is returned as a truncated view.
    /// </summary>
    public RawStorageView ReadSector(long sectorNumber, long sectorCount = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sectorNumber);
        ArgumentOutOfRangeException.ThrowIfNegative(sectorCount);
        long absoluteOffset = SectorNumberToByteOffset(sectorNumber, logicalSectorSize);
        long count = checked(sectorCount * logicalSectorSize);
        return ReadCore(absoluteOffset, count, RawStorageViewKind.Sector, null, sectorNumber, null, null);
    }

    /// <summary>Converts a non-negative logical sector number into an absolute byte offset.</summary>
    public static long SectorNumberToByteOffset(long sectorNumber, int logicalSectorSize = DefaultSectorSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sectorNumber);
        ValidateLogicalSectorSize(logicalSectorSize);
        return checked(sectorNumber * (long)logicalSectorSize);
    }

    /// <summary>Compatibility-friendly alias for <see cref="SectorNumberToByteOffset"/>.</summary>
    public static long SectorToOffset(long sectorNumber, int logicalSectorSize = DefaultSectorSize) =>
        SectorNumberToByteOffset(sectorNumber, logicalSectorSize);

    /// <summary>
    /// Reads a FATX partition header prefix. The view is limited to the first
    /// 4096 bytes or the end of the partition, whichever comes first.
    /// </summary>
    public RawStorageView ReadFatxHeader(FatxVolumeMetadata metadata)
    {
        FatxLayout layout = ValidateMetadata(metadata);
        long count = Math.Min(HeaderViewBytes, layout.PartitionLength);
        return ReadCore(
            layout.PartitionOffset,
            count,
            RawStorageViewKind.FatxHeader,
            0,
            null,
            null,
            metadata);
    }

    /// <summary>
    /// Reads a bounded range from the FATX allocation-table region. The
    /// <paramref name="relativeOffset"/> is relative to <see cref="FatxVolumeMetadata.FatOffset"/>.
    /// </summary>
    public RawStorageView ReadFatxAllocationTable(
        FatxVolumeMetadata metadata,
        long relativeOffset = 0,
        long count = MaximumViewBytes)
    {
        FatxLayout layout = ValidateMetadata(metadata);
        ArgumentOutOfRangeException.ThrowIfNegative(relativeOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        long fatLength = checked(metadata.DataOffset - metadata.FatOffset);
        if (relativeOffset > fatLength || count > fatLength - relativeOffset)
            throw new ArgumentOutOfRangeException(nameof(count), "The FAT view exceeds the validated allocation-table region.");

        long absoluteOffset = checked(metadata.FatOffset + relativeOffset);
        long partitionRelative = checked(absoluteOffset - layout.PartitionOffset);
        return ReadCore(
            absoluteOffset,
            count,
            RawStorageViewKind.FatxAllocationTable,
            partitionRelative,
            null,
            null,
            metadata);
    }

    /// <summary>Short alias for <see cref="ReadFatxAllocationTable"/>.</summary>
    public RawStorageView ReadFatxFat(FatxVolumeMetadata metadata, long relativeOffset = 0, long count = MaximumViewBytes) =>
        ReadFatxAllocationTable(metadata, relativeOffset, count);

    /// <summary>
    /// Reads a FATX data cluster. The cluster number is one-based, as in FATX;
    /// all layout arithmetic and partition boundaries are checked before reading.
    /// </summary>
    public RawStorageView ReadFatxCluster(FatxVolumeMetadata metadata, uint clusterNumber)
    {
        FatxLayout layout = ValidateMetadata(metadata);
        if (clusterNumber == 0 || clusterNumber > layout.ClusterCount)
            throw new ArgumentOutOfRangeException(nameof(clusterNumber), "The FATX cluster is outside the validated data area.");

        long relativeCluster = checked((long)clusterNumber - 1);
        long absoluteOffset = checked(metadata.DataOffset + checked(relativeCluster * layout.ClusterSize));
        long partitionRelative = checked(absoluteOffset - layout.PartitionOffset);
        if (absoluteOffset < layout.PartitionOffset || layout.ClusterSize > layout.PartitionEnd - absoluteOffset)
            throw new ArgumentOutOfRangeException(nameof(clusterNumber), "The FATX cluster exceeds the validated partition.");

        return ReadCore(
            absoluteOffset,
            layout.ClusterSize,
            RawStorageViewKind.FatxCluster,
            partitionRelative,
            null,
            clusterNumber,
            metadata);
    }

    /// <summary>Calculates a validated absolute byte offset for a FATX data cluster.</summary>
    public static long FatxClusterToAbsoluteOffset(FatxVolumeMetadata metadata, uint clusterNumber)
    {
        FatxLayout layout = ValidateMetadata(metadata);
        if (clusterNumber == 0 || clusterNumber > layout.ClusterCount)
            throw new ArgumentOutOfRangeException(nameof(clusterNumber), "The FATX cluster is outside the validated data area.");
        long relativeCluster = checked((long)clusterNumber - 1);
        long absoluteOffset = checked(metadata.DataOffset + checked(relativeCluster * layout.ClusterSize));
        if (absoluteOffset < layout.PartitionOffset || layout.ClusterSize > layout.PartitionEnd - absoluteOffset)
            throw new ArgumentOutOfRangeException(nameof(clusterNumber), "The FATX cluster exceeds the validated partition.");
        return absoluteOffset;
    }

    /// <summary>Short alias for <see cref="FatxClusterToAbsoluteOffset"/>.</summary>
    public static long ClusterToOffset(FatxVolumeMetadata metadata, uint clusterNumber) =>
        FatxClusterToAbsoluteOffset(metadata, clusterNumber);

    /// <summary>
    /// Returns whether a logical sector size is a bounded power of two suitable
    /// for inspection. FATX's normal choices are 512 and 4096 bytes.
    /// </summary>
    public static bool IsSafeLogicalSectorSize(int sectorSize) =>
        sectorSize >= MinimumSafeSectorSize &&
        sectorSize <= MaximumSafeSectorSize &&
        (sectorSize & (sectorSize - 1)) == 0;

    /// <summary>
    /// Formats at most one view as fixed-width uppercase hexadecimal with an
    /// address and ASCII column on every line.
    /// </summary>
    public static string FormatHexDump(ReadOnlySpan<byte> bytes, long absoluteOffset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(absoluteOffset);
        if (bytes.Length > MaximumViewBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes), "A formatted view cannot exceed 64 KiB.");
        if ((long)bytes.Length > long.MaxValue - absoluteOffset)
            throw new ArgumentOutOfRangeException(nameof(absoluteOffset));
        if (bytes.IsEmpty) return string.Empty;

        int lineCount = checked((bytes.Length + BytesPerDumpLine - 1) / BytesPerDumpLine);
        int estimatedLineLength = 16 + 2 + (BytesPerDumpLine * 3) + 2 + BytesPerDumpLine + 2;
        var builder = new StringBuilder(checked(lineCount * (estimatedLineLength + 1)));
        for (int line = 0; line < lineCount; line++)
        {
            int start = checked(line * BytesPerDumpLine);
            int lineLength = Math.Min(BytesPerDumpLine, bytes.Length - start);
            long lineOffset = checked(absoluteOffset + start);
            builder.Append(lineOffset.ToString("X16", CultureInfo.InvariantCulture));
            builder.Append("  ");
            for (int column = 0; column < BytesPerDumpLine; column++)
            {
                if (column == 8) builder.Append(' ');
                if (column < lineLength)
                {
                    builder.Append(bytes[start + column].ToString("X2", CultureInfo.InvariantCulture));
                    builder.Append(' ');
                }
                else
                {
                    builder.Append("   ");
                }
            }

            builder.Append(" |");
            for (int column = 0; column < BytesPerDumpLine; column++)
            {
                if (column < lineLength)
                {
                    byte value = bytes[start + column];
                    builder.Append(value is >= 0x20 and <= 0x7E ? (char)value : '.');
                }
                else
                {
                    builder.Append(' ');
                }
            }
            builder.Append('|');
            if (line + 1 < lineCount) builder.Append('\n');
        }
        return builder.ToString();
    }

    /// <summary>Formats the printable ASCII column for at most one view.</summary>
    public static string FormatAsciiColumn(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumViewBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes), "A formatted view cannot exceed 64 KiB.");
        if (bytes.IsEmpty) return string.Empty;

        var builder = new StringBuilder(bytes.Length);
        foreach (byte value in bytes)
            builder.Append(value is >= 0x20 and <= 0x7E ? (char)value : '.');
        return builder.ToString();
    }

    private RawStorageView ReadCore(
        long absoluteOffset,
        long requestedLength,
        RawStorageViewKind kind,
        long? partitionRelativeOffset,
        long? sectorNumber,
        uint? clusterNumber,
        FatxVolumeMetadata? metadata)
    {
        ValidateRange(absoluteOffset, requestedLength);
        long? relative = partitionRelativeOffset;
        if (metadata is not null)
        {
            long partitionEnd = checked(metadata.PartitionOffset + metadata.PartitionLength);
            if (absoluteOffset < metadata.PartitionOffset || absoluteOffset > partitionEnd ||
                requestedLength > partitionEnd - absoluteOffset)
                throw new ArgumentOutOfRangeException(nameof(absoluteOffset), "The view exceeds the validated FATX partition.");
            relative ??= checked(absoluteOffset - metadata.PartitionOffset);
        }

        int readLength = checked((int)Math.Min(requestedLength, MaximumViewBytes));
        var bytes = new byte[readLength];
        long originalPosition = source.Position;
        try
        {
            source.Position = absoluteOffset;
            int totalRead = 0;
            while (totalRead < bytes.Length)
            {
                int read = source.Read(bytes, totalRead, bytes.Length - totalRead);
                if (read <= 0)
                    throw new EndOfStreamException("The authorized inspection source ended before the requested view was read.");
                totalRead += read;
            }
        }
        finally
        {
            RestorePosition(originalPosition);
        }

        bool isAtEnd = checked(absoluteOffset + readLength) == sourceLength;
        return new RawStorageView(
            kind,
            absoluteOffset,
            requestedLength,
            sourceLength,
            logicalSectorSize,
            relative,
            sectorNumber,
            clusterNumber,
            requestedLength > readLength,
            isAtEnd,
            bytes);
    }

    private void ValidateRange(long absoluteOffset, long requestedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(absoluteOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(requestedLength);
        if (absoluteOffset > sourceLength || requestedLength > sourceLength - absoluteOffset)
            throw new ArgumentOutOfRangeException(nameof(requestedLength), "The requested view is outside the authoritative source length.");
    }

    private static void ValidateLogicalSectorSize(int sectorSize)
    {
        if (!IsSafeLogicalSectorSize(sectorSize))
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "Logical sector size must be a power of two from 512 through 65536 bytes.");
    }

    private static FatxLayout ValidateMetadata(FatxVolumeMetadata? metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.PartitionOffset < 0 || metadata.PartitionLength <= 0)
            throw new ArgumentException("The FATX partition range is invalid.", nameof(metadata));
        if (metadata.FatOffset < metadata.PartitionOffset || metadata.DataOffset < metadata.PartitionOffset ||
            metadata.FatOffset > metadata.DataOffset)
            throw new ArgumentException("The FATX layout starts before the partition.", nameof(metadata));
        if (!IsSafeLogicalSectorSize(metadata.SectorSize))
            throw new ArgumentException("The FATX sector size is not a safe logical sector size.", nameof(metadata));
        if (metadata.SectorsPerCluster == 0 || metadata.SectorsPerCluster > 128 ||
            (metadata.SectorsPerCluster & (metadata.SectorsPerCluster - 1)) != 0)
            throw new ArgumentException("The FATX sectors-per-cluster value is invalid.", nameof(metadata));
        if (metadata.ClusterCount <= 0)
            throw new ArgumentException("The FATX cluster count is invalid.", nameof(metadata));

        long partitionEnd;
        long clusterSize;
        long dataLength;
        try
        {
            partitionEnd = checked(metadata.PartitionOffset + metadata.PartitionLength);
            clusterSize = checked((long)metadata.SectorsPerCluster * metadata.SectorSize);
            dataLength = checked(metadata.ClusterCount * clusterSize);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("The FATX layout arithmetic overflows.", nameof(metadata), exception);
        }

        if (metadata.FatOffset > partitionEnd || metadata.DataOffset > partitionEnd ||
            dataLength > partitionEnd - metadata.DataOffset)
            throw new ArgumentException("The FATX layout exceeds the partition.", nameof(metadata));

        return new FatxLayout(
            metadata.PartitionOffset,
            metadata.PartitionLength,
            partitionEnd,
            metadata.ClusterCount,
            clusterSize);
    }

    private void RestorePosition(long originalPosition)
    {
        try { source.Position = originalPosition; }
        catch (IOException) { }
        catch (NotSupportedException) { }
    }

    private readonly record struct FatxLayout(
        long PartitionOffset,
        long PartitionLength,
        long PartitionEnd,
        long ClusterCount,
        long ClusterSize);
}
