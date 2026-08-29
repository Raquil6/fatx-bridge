using System.Buffers.Binary;
using System.Text;

namespace FatxBridge.Core;

public enum FatxStorageKind
{
    Xbox360HardDrive,
    OriginalXboxHardDrive,
    OriginalXboxMemoryUnit,
}

public sealed record FatxPartitionCandidate(
    string Name,
    long Offset,
    long Length,
    FatxVolumeMetadata Metadata,
    bool SupportsWrite = false);

public sealed record FatxStorageDetection(
    FatxStorageKind Kind,
    IReadOnlyList<FatxPartitionCandidate> Partitions);

public static class FatxPartitionProbe
{
    private sealed record ProbeCandidate(string Name, long Offset, long Length, bool SupportsWrite, int SectorSize = 512);

    private static readonly ProbeCandidate[] FixedXbox360RetailPartitions =
    {
        new("Cache 0", 0x80000, 0x80000000, false),
        new("Cache 1", 0x80080000, 0x80000000, false),
        new("System Auxiliary", 0x10C080000, 0xCE30000, false),
        new("System Extended", 0x118EB0000, 0x8000000, false),
        new("System / Compatibility", 0x120EB0000, 0x10000000, false),
    };

    private static readonly ProbeCandidate[] FixedOriginalXboxPartitions =
    {
        new("C (System)", 0x8CA80000, 0x1F400000, true),
        new("E (Data)", 0xABE80000, 0x1312D6000, true),
        new("X (Cache)", 0x80000, 0x2EE00000, true),
        new("Y (Cache)", 0x2EE80000, 0x2EE00000, true),
        new("Z (Cache)", 0x5DC80000, 0x2EE00000, true),
    };

    private const long Xbox360ContentOffset = 0x130EB0000;
    private const long OriginalXboxExtendedOffset = 0x1DD156000;
    private const long OriginalXboxLba28Boundary = 0x1FFFFFFE00;
    private const long MaximumOriginalXboxMemoryUnitLength = 4L * 1024 * 1024 * 1024;
    private const int XbPartitionTableSize = 512;
    private const int XbPartitionEntryOffset = 48;
    private const int XbPartitionEntrySize = 32;
    private const int XbPartitionEntryCount = 14;
    private const uint XbPartitionInUse = 0x80000000;
    private const int DeviceSectorSize = 512;
    private static ReadOnlySpan<byte> XbPartitionMagic => "****PARTINFO****"u8;

    public static FatxStorageDetection? DetectSupportedStorage(
        Stream source,
        long? sourceLength = null,
        Action<string>? diagnostic = null)
    {
        ValidateSource(source);
        long length = sourceLength ?? source.Length;
        diagnostic?.Invoke($"Source length: {length} bytes (0x{length:X}).");

        IReadOnlyList<FatxPartitionCandidate> xbox360 = DetectStandardRetailPartitions(
            source, length, message => diagnostic?.Invoke($"Xbox 360: {message}"));
        if (xbox360.Count > 0)
            return new FatxStorageDetection(FatxStorageKind.Xbox360HardDrive, xbox360);

        IReadOnlyList<FatxPartitionCandidate> original = DetectOriginalXboxPartitions(
            source, length, message => diagnostic?.Invoke($"Original Xbox HDD: {message}"));
        if (original.Count > 0)
            return new FatxStorageDetection(FatxStorageKind.OriginalXboxHardDrive, original);

        if (length is >= 0x4000 and <= MaximumOriginalXboxMemoryUnitLength)
        {
            FatxPartitionCandidate? memoryUnit = ProbeCandidateVolume(source, length,
                new ProbeCandidate("Memory Unit", 0, length, true, 4096), FatxByteOrder.LittleEndian,
                message => diagnostic?.Invoke($"Original Xbox MU: {message}"));
            if (memoryUnit is not null)
                return new FatxStorageDetection(FatxStorageKind.OriginalXboxMemoryUnit, [memoryUnit]);
        }
        else
        {
            diagnostic?.Invoke("Original Xbox MU: whole-device probing skipped because the capacity is outside the supported 16 KiB–4 GiB range.");
        }

        return null;
    }

    public static IReadOnlyList<FatxPartitionCandidate> DetectStandardRetailPartitions(
        Stream source,
        long? sourceLength = null,
        Action<string>? diagnostic = null)
    {
        ValidateSource(source);
        long length = sourceLength ?? source.Length;
        diagnostic?.Invoke($"Source length: {length} bytes (0x{length:X}).");
        var candidates = new List<ProbeCandidate>(FixedXbox360RetailPartitions);
        if (length > Xbox360ContentOffset)
            candidates.Add(new ProbeCandidate("Content", Xbox360ContentOffset,
                length - Xbox360ContentOffset, true));
        return ProbeCandidates(source, length, candidates, FatxByteOrder.BigEndian, diagnostic);
    }

    public static IReadOnlyList<FatxPartitionCandidate> DetectOriginalXboxPartitions(
        Stream source,
        long? sourceLength = null,
        Action<string>? diagnostic = null)
    {
        ValidateSource(source);
        long length = sourceLength ?? source.Length;
        diagnostic?.Invoke($"Source length: {length} bytes (0x{length:X}).");

        ProbeCandidate[]? table = ReadXbPartitionTable(source, length, diagnostic, out bool tablePresent);
        List<ProbeCandidate> candidates = table is null ? [] : new List<ProbeCandidate>(table);
        foreach (ProbeCandidate fixedPartition in FixedOriginalXboxPartitions)
        {
            if (!candidates.Any(candidate => candidate.Offset == fixedPartition.Offset))
                candidates.Add(fixedPartition);
        }

        List<FatxPartitionCandidate> found = ProbeCandidates(source, length, candidates,
            FatxByteOrder.LittleEndian, diagnostic).ToList();

        if (!tablePresent)
        {
            FatxPartitionCandidate? g = null;
            if (length > OriginalXboxLba28Boundary)
            {
                g = ProbeCandidateVolume(source, length,
                    new ProbeCandidate("G (Extended)", OriginalXboxLba28Boundary,
                        length - OriginalXboxLba28Boundary, true), FatxByteOrder.LittleEndian, diagnostic);
            }

            long fEnd = g?.Offset ?? length;
            if (fEnd > OriginalXboxExtendedOffset)
            {
                FatxPartitionCandidate? f = ProbeCandidateVolume(source, length,
                    new ProbeCandidate("F (Extended)", OriginalXboxExtendedOffset,
                        fEnd - OriginalXboxExtendedOffset, true), FatxByteOrder.LittleEndian, diagnostic);
                if (f is not null && found.All(candidate => candidate.Offset != f.Offset)) found.Add(f);
            }
            if (g is not null && found.All(candidate => candidate.Offset != g.Offset)) found.Add(g);
        }

        return found.OrderBy(candidate => candidate.Offset).ToArray();
    }

    private static ProbeCandidate[]? ReadXbPartitionTable(
        Stream source,
        long sourceLength,
        Action<string>? diagnostic,
        out bool tablePresent)
    {
        tablePresent = false;
        if (sourceLength < XbPartitionTableSize) return null;
        byte[] prefix = new byte[0x1000];
        try
        {
            source.Position = 0;
            int total = 0;
            while (total < prefix.Length)
            {
                int read = source.Read(prefix.AsSpan(total));
                if (read == 0) break;
                total += read;
            }
            if (total < XbPartitionTableSize || !prefix.AsSpan(0, 16).SequenceEqual(XbPartitionMagic))
            {
                diagnostic?.Invoke("No XBPartitioner table signature was present.");
                return null;
            }
            tablePresent = true;
        }
        catch (IOException exception)
        {
            diagnostic?.Invoke($"XBPartitioner table read failed: {exception.Message}");
            return null;
        }

        var result = new List<ProbeCandidate>();
        for (int index = 0; index < XbPartitionEntryCount; index++)
        {
            int offset = XbPartitionEntryOffset + index * XbPartitionEntrySize;
            ReadOnlySpan<byte> entry = prefix.AsSpan(offset, XbPartitionEntrySize);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..20]);
            if ((flags & XbPartitionInUse) == 0) continue;

            uint startSectors = BinaryPrimitives.ReadUInt32LittleEndian(entry[20..24]);
            uint lengthSectors = BinaryPrimitives.ReadUInt32LittleEndian(entry[24..28]);
            long partitionOffset = (long)startSectors * DeviceSectorSize;
            long partitionLength = (long)lengthSectors * DeviceSectorSize;
            string rawName = Encoding.ASCII.GetString(entry[..16]).TrimEnd('\0', ' ');
            string name = OriginalXboxPartitionName(index, rawName, partitionOffset);
            if (!WithinSource(partitionOffset, partitionLength, sourceLength))
            {
                diagnostic?.Invoke($"XBPartitioner entry {index + 1} ({name}) was ignored because " +
                    $"0x{partitionOffset:X}+0x{partitionLength:X} is outside the source.");
                continue;
            }
            result.Add(new ProbeCandidate(name, partitionOffset, partitionLength, true));
        }

        ProbeCandidate[] ordered = result.OrderBy(candidate => candidate.Offset).ToArray();
        for (int index = 1; index < ordered.Length; index++)
        {
            ProbeCandidate previous = ordered[index - 1];
            ProbeCandidate current = ordered[index];
            if (previous.Offset + previous.Length > current.Offset)
            {
                diagnostic?.Invoke("XBPartitioner table was ignored because active entries overlap.");
                return [];
            }
        }

        diagnostic?.Invoke($"Accepted XBPartitioner table with {ordered.Length} bounded active entries.");
        return ordered;
    }

    private static string OriginalXboxPartitionName(int index, string rawName, long offset)
    {
        foreach (ProbeCandidate candidate in FixedOriginalXboxPartitions)
            if (candidate.Offset == offset) return candidate.Name;
        if (rawName.Equals("XBOX F", StringComparison.OrdinalIgnoreCase) || index == 5) return "F (Extended)";
        if (rawName.Equals("XBOX G", StringComparison.OrdinalIgnoreCase) || index == 6) return "G (Extended)";
        return string.IsNullOrWhiteSpace(rawName) ? $"Xbox partition {index + 1}" : rawName;
    }

    private static List<FatxPartitionCandidate> ProbeCandidates(
        Stream source,
        long sourceLength,
        IEnumerable<ProbeCandidate> candidates,
        FatxByteOrder requiredByteOrder,
        Action<string>? diagnostic)
    {
        var found = new List<FatxPartitionCandidate>();
        foreach (ProbeCandidate candidate in candidates)
        {
            if (found.Any(existing => existing.Offset == candidate.Offset)) continue;
            FatxPartitionCandidate? result = ProbeCandidateVolume(source, sourceLength, candidate,
                requiredByteOrder, diagnostic);
            if (result is not null) found.Add(result);
        }
        return found;
    }

    private static FatxPartitionCandidate? ProbeCandidateVolume(
        Stream source,
        long sourceLength,
        ProbeCandidate candidate,
        FatxByteOrder requiredByteOrder,
        Action<string>? diagnostic)
    {
        if (!WithinSource(candidate.Offset, candidate.Length, sourceLength))
        {
            diagnostic?.Invoke($"{candidate.Name}: skipped because 0x{candidate.Offset:X}+0x{candidate.Length:X} is outside the source.");
            return null;
        }
        try
        {
            diagnostic?.Invoke($"{candidate.Name}: probing offset 0x{candidate.Offset:X}, length 0x{candidate.Length:X}.");
            FatxVolume volume = FatxVolume.Open(source, candidate.Offset, candidate.Length, sourceLength,
                candidate.SectorSize);
            diagnostic?.Invoke($"{candidate.Name}: header parsed as {volume.Metadata.ByteOrder}, {volume.Metadata.AllocationTable}, " +
                $"sector={volume.Metadata.SectorSize}, SPC={volume.Metadata.SectorsPerCluster}, " +
                $"root={volume.Metadata.RootFirstCluster}, data=0x{volume.Metadata.DataOffset:X}.");
            if (volume.Metadata.ByteOrder != requiredByteOrder)
            {
                diagnostic?.Invoke($"{candidate.Name}: rejected because this layout requires {requiredByteOrder} FATX metadata.");
                return null;
            }
            IReadOnlyList<FatxDirectoryEntry> root = volume.ListRootDirectory();
            diagnostic?.Invoke($"{candidate.Name}: accepted with {root.Count} root entries.");
            return new FatxPartitionCandidate(candidate.Name, candidate.Offset, candidate.Length,
                volume.Metadata, candidate.SupportsWrite);
        }
        catch (FatxFormatException exception)
        {
            diagnostic?.Invoke($"{candidate.Name}: rejected: {exception.Message}");
        }
        catch (EndOfStreamException exception)
        {
            diagnostic?.Invoke($"{candidate.Name}: truncated source: {exception.Message}");
        }
        catch (IOException exception)
        {
            diagnostic?.Invoke($"{candidate.Name}: device read failed: {exception.Message} (HRESULT 0x{exception.HResult:X8}).");
        }
        return null;
    }

    private static bool WithinSource(long offset, long length, long sourceLength) =>
        offset >= 0 && length >= 0 && offset <= sourceLength && length <= sourceLength - offset;

    private static void ValidateSource(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("The source must be readable and seekable.", nameof(source));
    }
}
