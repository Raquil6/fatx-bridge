namespace FatxBridge.Core;

public sealed record FatxPartitionCandidate(string Name, long Offset, long Length, FatxVolumeMetadata Metadata);

public static class FatxPartitionProbe
{
    private static readonly (string Name, long Offset, long Length)[] FixedRetailPartitions =
    {
        ("Cache 0", 0x80000, 0x80000000),
        ("Cache 1", 0x80080000, 0x80000000),
        ("System Auxiliary", 0x10C080000, 0xCE30000),
        ("System Extended", 0x118EB0000, 0x8000000),
        ("System / Compatibility", 0x120EB0000, 0x10000000),
    };
    private const long ContentOffset = 0x130EB0000;

    public static IReadOnlyList<FatxPartitionCandidate> DetectStandardRetailPartitions(
        Stream source,
        long? sourceLength = null,
        Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("The source must be readable and seekable.", nameof(source));
        long length = sourceLength ?? source.Length;
        diagnostic?.Invoke($"Source length: {length} bytes (0x{length:X}).");
        var candidates = new List<(string Name, long Offset, long Length)>(FixedRetailPartitions);
        if (length > ContentOffset)
            candidates.Add(("Content", ContentOffset, length - ContentOffset));
        var found = new List<FatxPartitionCandidate>();
        foreach (var candidate in candidates)
        {
            if (candidate.Offset < 0 || candidate.Length < 0 || candidate.Offset > length || candidate.Length > length - candidate.Offset)
            {
                diagnostic?.Invoke($"{candidate.Name}: skipped because 0x{candidate.Offset:X}+0x{candidate.Length:X} is outside the source.");
                continue;
            }
            try
            {
                diagnostic?.Invoke($"{candidate.Name}: probing offset 0x{candidate.Offset:X}, length 0x{candidate.Length:X}.");
                FatxVolume volume = FatxVolume.Open(source, candidate.Offset, candidate.Length, length);
                diagnostic?.Invoke($"{candidate.Name}: header parsed as {volume.Metadata.ByteOrder}, {volume.Metadata.AllocationTable}, " +
                    $"SPC={volume.Metadata.SectorsPerCluster}, root={volume.Metadata.RootFirstCluster}, data=0x{volume.Metadata.DataOffset:X}.");
                if (volume.Metadata.ByteOrder != FatxByteOrder.BigEndian)
                {
                    diagnostic?.Invoke($"{candidate.Name}: rejected because the retail Xbox 360 probe requires big-endian XTAF.");
                    continue;
                }
                IReadOnlyList<FatxDirectoryEntry> root = volume.ListRootDirectory();
                diagnostic?.Invoke($"{candidate.Name}: accepted with {root.Count} root entries.");
                found.Add(new FatxPartitionCandidate(candidate.Name, candidate.Offset, candidate.Length, volume.Metadata));
            }
            catch (FatxFormatException exception)
            {
                diagnostic?.Invoke($"{candidate.Name}: rejected: {exception.Message}");
                // A candidate location without a fully valid FATX volume is not displayed.
            }
            catch (EndOfStreamException exception)
            {
                diagnostic?.Invoke($"{candidate.Name}: truncated source: {exception.Message}");
                // Truncated device data is not a valid candidate.
            }
            catch (IOException exception)
            {
                diagnostic?.Invoke($"{candidate.Name}: device read failed: {exception.Message} (HRESULT 0x{exception.HResult:X8}).");
                // A USB bridge can reject an individual region. Keep probing the
                // remaining standard partition offsets instead of losing the disk.
            }
        }
        return found;
    }
}
