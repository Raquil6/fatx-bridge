using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FatxBridge.Core;

public enum FatxFormatMode
{
    Quick,
    Full,
}

public enum FatxFormatStage
{
    Preparing,
    InvalidatingHeader,
    ZeroingPartition,
    ZeroingAllocationTable,
    ZeroingRootDirectory,
    WritingAllocationMarkers,
    WritingHeader,
    Validating,
    Completed,
}

/// <summary>
/// Immutable options for formatting one caller-selected FATX partition range.
/// The formatter never discovers or chooses a physical-disk partition.
/// </summary>
public sealed record FatxFormatOptions(
    long PartitionOffset,
    long PartitionLength,
    FatxByteOrder ByteOrder = FatxByteOrder.LittleEndian,
    int SectorSize = 512,
    uint SectorsPerCluster = 2,
    FatxFormatMode Mode = FatxFormatMode.Quick,
    uint? SerialNumber = null);

/// <summary>Progress for a formatting operation. BytesProcessed is bounded by TotalBytes.</summary>
public sealed record FatxFormatProgress(
    FatxFormatStage Stage,
    long BytesProcessed,
    long TotalBytes)
{
    public double Fraction => TotalBytes <= 0
        ? 0
        : Math.Clamp((double)BytesProcessed / TotalBytes, 0, 1);
}

/// <summary>Validated result returned after the formatted volume is reopened successfully.</summary>
public sealed record FatxFormatResult(
    FatxVolumeMetadata Metadata,
    FatxFormatMode Mode,
    long BytesWritten)
{
    public long FatLength => checked(Metadata.DataOffset - Metadata.FatOffset);
    public long RootDirectoryOffset => Metadata.DataOffset;
    public long RootDirectoryLength => checked((long)Metadata.SectorsPerCluster * Metadata.SectorSize);
}

/// <summary>
/// Image-first FATX formatter. Only the explicitly supplied partition range is
/// modified; this class does not know how to format a whole physical device.
/// </summary>
public static class FatxFormatter
{
    private const int HeaderSize = 0x1000;
    private const int RawAlignment = 0x1000;
    private const int ZeroChunkSize = 1024 * 1024;
    private const long MaximumFatEntries = 0x0FFFFFFF;
    private const uint Fat16MediaMarker = 0xFFF8;
    private const uint Fat16EndOfChain = 0xFFFF;
    private const uint Fat32MediaMarker = 0xFFFFFFF8;
    private const uint Fat32EndOfChain = 0xFFFFFFFF;

    private static readonly uint[] ValidSectorsPerCluster = [2, 4, 8, 16, 32, 64, 128];

    /// <summary>
    /// Formats one bounded partition range and returns only after
    /// <see cref="FatxVolume.Open(Stream, long, long, long?, int)"/> accepts
    /// the result and its root directory can be read.
    /// </summary>
    public static FatxFormatResult Format(
        Stream source,
        FatxFormatOptions options,
        IProgress<FatxFormatProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (!source.CanRead || !source.CanSeek || !source.CanWrite)
            throw new ArgumentException("The FATX formatter requires a readable, writable, seekable stream.", nameof(source));

        long originalPosition = 0;
        bool restorePosition = false;
        try
        {
            try
            {
                originalPosition = source.Position;
                restorePosition = true;
            }
            catch (Exception exception) when (IsPositionFailure(exception))
            {
                // A seekable stream should expose Position, but preserving it is
                // best-effort and must not turn a valid formatting request into
                // a failure before any bounded validation occurs.
            }

            long sourceLength = source.Length;
            FormatPlan plan = BuildPlan(options, sourceLength);
            var reporter = new ProgressReporter(progress, plan.TotalWork);
            reporter.Report(FatxFormatStage.Preparing);
            cancellationToken.ThrowIfCancellationRequested();

            long bytesWritten = 0;
            if (options.Mode == FatxFormatMode.Full)
            {
                bytesWritten = checked(bytesWritten + ZeroRange(source, plan.PartitionOffset,
                    plan.PartitionLength, plan, FatxFormatStage.ZeroingPartition,
                    reporter, cancellationToken));
            }
            else
            {
                // Invalidate the old signature/header before destroying the
                // old FAT and root. A crash after this flush leaves the range
                // unrecognized instead of advertising stale, damaged FATX.
                reporter.Report(FatxFormatStage.InvalidatingHeader);
                cancellationToken.ThrowIfCancellationRequested();
                WriteBounded(source, plan.PartitionOffset, new byte[HeaderSize], plan);
                bytesWritten = checked(bytesWritten + HeaderSize);
                reporter.Advance(HeaderSize);
                source.Flush();
                cancellationToken.ThrowIfCancellationRequested();

                bytesWritten = checked(bytesWritten + ZeroRange(source, plan.FatOffset,
                    plan.FatLength, plan, FatxFormatStage.ZeroingAllocationTable,
                    reporter, cancellationToken));
                bytesWritten = checked(bytesWritten + ZeroRange(source, plan.DataOffset,
                    plan.ClusterSize, plan, FatxFormatStage.ZeroingRootDirectory,
                    reporter, cancellationToken));
            }

            // Flush the erased regions before publishing any FATX metadata. The
            // header is deliberately written last, so a crash cannot advertise a
            // newly formatted volume before its FAT markers exist.
            source.Flush();
            cancellationToken.ThrowIfCancellationRequested();

            reporter.Report(FatxFormatStage.WritingAllocationMarkers);
            byte[] markers = BuildAllocationMarkers(plan);
            WriteBounded(source, plan.FatOffset, markers, plan);
            bytesWritten = checked(bytesWritten + markers.Length);
            reporter.Advance(markers.Length);
            source.Flush();

            cancellationToken.ThrowIfCancellationRequested();
            reporter.Report(FatxFormatStage.WritingHeader);
            byte[] header = BuildHeader(plan);
            WriteBounded(source, plan.PartitionOffset, header, plan);
            bytesWritten = checked(bytesWritten + header.Length);
            reporter.Advance(header.Length);
            source.Flush();

            cancellationToken.ThrowIfCancellationRequested();
            reporter.Report(FatxFormatStage.Validating);
            FatxVolume reopened = FatxVolume.Open(source, plan.PartitionOffset,
                plan.PartitionLength, sourceLength, plan.SectorSize);
            ValidateReopenedVolume(reopened, plan);
            if (reopened.ListRootDirectory().Count != 0)
                throw new FatxFormatException("The formatted FATX root directory was not empty after validation.");

            reporter.Report(FatxFormatStage.Completed);
            return new FatxFormatResult(reopened.Metadata, options.Mode, bytesWritten);
        }
        finally
        {
            if (restorePosition)
            {
                try { source.Position = originalPosition; }
                catch (Exception exception) when (IsPositionFailure(exception)) { }
            }
        }
    }

    /// <summary>Convenience overload for callers that only need cancellation.</summary>
    public static FatxFormatResult Format(
        Stream source,
        FatxFormatOptions options,
        CancellationToken cancellationToken) =>
        Format(source, options, progress: null, cancellationToken: cancellationToken);

    private static FormatPlan BuildPlan(FatxFormatOptions options, long sourceLength)
    {
        if (options.Mode is not (FatxFormatMode.Quick or FatxFormatMode.Full))
            throw new FatxFormatException("The FATX format mode is invalid.");
        if (options.PartitionOffset < 0)
            throw new FatxFormatException("The FATX partition offset cannot be negative.");
        if (options.PartitionLength < HeaderSize)
            throw new FatxFormatException($"The FATX partition must be at least 0x{HeaderSize:X} bytes long.");
        if (options.SectorSize is not (512 or 4096))
            throw new FatxFormatException("FATX logical sectors must be 512 or 4096 bytes.");
        if (!ValidSectorsPerCluster.Contains(options.SectorsPerCluster))
            throw new FatxFormatException("FATX sectors-per-cluster must be one of 2, 4, 8, 16, 32, 64, or 128.");
        if (options.PartitionOffset % options.SectorSize != 0)
            throw new FatxFormatException("The FATX partition offset must be aligned to its logical sector size.");
        if (options.PartitionOffset % RawAlignment != 0)
            throw new FatxFormatException("The FATX partition offset must be aligned to 0x1000 bytes so FatxVolume can reopen its allocation table.");
        if (options.PartitionLength % options.SectorSize != 0)
            throw new FatxFormatException("The FATX partition length must be aligned to its logical sector size.");
        if (!Within(options.PartitionOffset, options.PartitionLength, sourceLength))
            throw new FatxFormatException("The FATX partition range is outside the source stream.");

        long clusterSize;
        long entries;
        long fatBytes;
        long fatLength;
        long data;
        long clusterCount;
        try
        {
            clusterSize = checked((long)options.SectorsPerCluster * options.SectorSize);
            // This intentionally mirrors FatxVolume.Layout exactly.
            entries = options.PartitionLength / clusterSize + 1;
            if (entries is < 2 or > MaximumFatEntries)
                throw new FatxFormatException("The FATX allocation-table size is impossible.");
            FatxAllocationTable allocationTable = entries < 0xFFF0
                ? FatxAllocationTable.Fat16
                : FatxAllocationTable.Fat32;
            int entrySize = allocationTable == FatxAllocationTable.Fat16 ? 2 : 4;
            fatBytes = checked(entries * entrySize);
            fatLength = Align(fatBytes, RawAlignment);
            data = checked(HeaderSize + fatLength);
            clusterCount = (options.PartitionLength - data) / clusterSize;
            if (data >= options.PartitionLength || clusterCount < 1 || clusterCount > entries - 1)
                throw new FatxFormatException("The FATX data-cluster range is impossible.");

            long partitionEnd = checked(options.PartitionOffset + options.PartitionLength);
            long fatOffset = checked(options.PartitionOffset + HeaderSize);
            long dataOffset = checked(options.PartitionOffset + data);
            long dataEnd = checked(dataOffset + checked(clusterCount * clusterSize));
            if (!Within(options.PartitionOffset, HeaderSize, partitionEnd) ||
                !Within(fatOffset, fatLength, partitionEnd) ||
                !Within(dataOffset, clusterSize, partitionEnd) ||
                !Within(dataOffset, checked(clusterCount * clusterSize), partitionEnd) ||
                dataEnd > partitionEnd)
                throw new FatxFormatException("The FATX layout would exceed the partition boundary.");

            uint serial = options.SerialNumber ?? GenerateSerialNumber();
            long metadataWork = checked((long)HeaderSize + 2L * entrySize);
            long zeroWork = options.Mode == FatxFormatMode.Full
                ? options.PartitionLength
                : checked(fatLength + clusterSize);
            long invalidationWork = options.Mode == FatxFormatMode.Quick ? HeaderSize : 0;
            long totalWork = checked(zeroWork + metadataWork + invalidationWork);
            return new FormatPlan(options.PartitionOffset, options.PartitionLength,
                options.ByteOrder, options.SectorSize, options.SectorsPerCluster,
                allocationTable, entrySize, serial, clusterSize, fatOffset, fatLength,
                dataOffset, clusterCount, totalWork);
        }
        catch (OverflowException exception)
        {
            throw new FatxFormatException($"The FATX format geometry overflowed the supported range: {exception.Message}");
        }
    }

    private static long ZeroRange(
        Stream source,
        long offset,
        long length,
        FormatPlan plan,
        FatxFormatStage stage,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        if (!Within(offset, length, checked(plan.PartitionOffset + plan.PartitionLength)))
            throw new FatxFormatException("A FATX zeroing range would exceed the partition boundary.");

        byte[] zeros = new byte[ZeroChunkSize];
        long done = 0;
        while (done < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int take = checked((int)Math.Min((long)zeros.Length, length - done));
            WriteBounded(source, checked(offset + done), zeros.AsSpan(0, take), plan);
            done = checked(done + take);
            reporter.Advance(take);
            if (done < length) reporter.Report(stage);
        }
        reporter.Report(stage);
        return done;
    }

    private static byte[] BuildHeader(FormatPlan plan)
    {
        var header = new byte[HeaderSize];
        (plan.ByteOrder == FatxByteOrder.LittleEndian ? "FATX"u8 : "XTAF"u8).CopyTo(header);
        WriteUInt32(header.AsSpan(4, 4), plan.SerialNumber, plan.ByteOrder);
        WriteUInt32(header.AsSpan(8, 4), plan.SectorsPerCluster, plan.ByteOrder);
        WriteUInt32(header.AsSpan(12, 4), 1, plan.ByteOrder);
        return header;
    }

    private static byte[] BuildAllocationMarkers(FormatPlan plan)
    {
        var markers = new byte[checked(plan.EntrySize * 2)];
        uint media = plan.AllocationTable == FatxAllocationTable.Fat16
            ? Fat16MediaMarker : Fat32MediaMarker;
        uint root = plan.AllocationTable == FatxAllocationTable.Fat16
            ? Fat16EndOfChain : Fat32EndOfChain;
        WriteEntry(markers.AsSpan(0, plan.EntrySize), media, plan);
        WriteEntry(markers.AsSpan(plan.EntrySize, plan.EntrySize), root, plan);
        return markers;
    }

    private static void WriteEntry(Span<byte> target, uint value, FormatPlan plan)
    {
        if (plan.EntrySize == 2)
        {
            if (value > ushort.MaxValue) throw new InvalidOperationException("A FAT16 marker exceeded its entry width.");
            if (plan.ByteOrder == FatxByteOrder.LittleEndian)
                BinaryPrimitives.WriteUInt16LittleEndian(target, (ushort)value);
            else
                BinaryPrimitives.WriteUInt16BigEndian(target, (ushort)value);
        }
        else if (plan.ByteOrder == FatxByteOrder.LittleEndian)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(target, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(target, value);
        }
    }

    private static void WriteUInt32(Span<byte> target, uint value, FatxByteOrder order)
    {
        if (order == FatxByteOrder.LittleEndian)
            BinaryPrimitives.WriteUInt32LittleEndian(target, value);
        else
            BinaryPrimitives.WriteUInt32BigEndian(target, value);
    }

    private static void WriteBounded(Stream source, long offset, ReadOnlySpan<byte> data, FormatPlan plan)
    {
        long partitionEnd = checked(plan.PartitionOffset + plan.PartitionLength);
        if (!Within(offset, data.Length, partitionEnd))
            throw new FatxFormatException("A FATX write would exceed the partition boundary.");
        source.Position = offset;
        source.Write(data);
    }

    private static void ValidateReopenedVolume(FatxVolume volume, FormatPlan plan)
    {
        FatxVolumeMetadata metadata = volume.Metadata;
        if (metadata.PartitionOffset != plan.PartitionOffset ||
            metadata.PartitionLength != plan.PartitionLength ||
            metadata.ByteOrder != plan.ByteOrder ||
            metadata.SectorSize != plan.SectorSize ||
            metadata.SectorsPerCluster != plan.SectorsPerCluster ||
            metadata.RootFirstCluster != 1 ||
            metadata.SerialNumber != plan.SerialNumber ||
            metadata.AllocationTable != plan.AllocationTable ||
            metadata.FatOffset != plan.FatOffset ||
            metadata.DataOffset != plan.DataOffset ||
            metadata.ClusterCount != plan.ClusterCount)
            throw new FatxFormatException("The reopened FATX metadata did not match the requested format geometry.");
    }

    private static uint GenerateSerialNumber()
    {
        Span<byte> random = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(random);
        return BinaryPrimitives.ReadUInt32LittleEndian(random);
    }

    private static long Align(long value, int alignment) =>
        checked((value + alignment - 1) / alignment * alignment);

    private static bool Within(long offset, long length, long total)
    {
        if (offset < 0 || length < 0 || total < 0 || offset > total) return false;
        return length <= total - offset;
    }

    private static bool IsPositionFailure(Exception exception) => exception is
        IOException or NotSupportedException or ObjectDisposedException or
        ArgumentException or InvalidOperationException;

    private sealed record FormatPlan(
        long PartitionOffset,
        long PartitionLength,
        FatxByteOrder ByteOrder,
        int SectorSize,
        uint SectorsPerCluster,
        FatxAllocationTable AllocationTable,
        int EntrySize,
        uint SerialNumber,
        long ClusterSize,
        long FatOffset,
        long FatLength,
        long DataOffset,
        long ClusterCount,
        long TotalWork);

    private sealed class ProgressReporter(IProgress<FatxFormatProgress>? progress, long totalWork)
    {
        private long bytesProcessed;

        public void Advance(long bytes)
        {
            bytesProcessed = checked(bytesProcessed + bytes);
            if (bytesProcessed > totalWork)
                throw new InvalidOperationException("FATX formatter progress exceeded its bounded work estimate.");
        }

        public void Report(FatxFormatStage stage) =>
            progress?.Report(new FatxFormatProgress(stage, bytesProcessed, totalWork));
    }
}
