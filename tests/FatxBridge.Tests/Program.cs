using System.Buffers.Binary;
using FatxBridge.Core;

var tests = new (string Name, Action Run)[]
{
    ("valid FATX header and root listing", ValidHeaderAndRootListing),
    ("FAT16 root chain traversal", Fat16ChainTraversal),
    ("FAT32 root chain traversal", Fat32ChainTraversal),
    ("invalid signature/header rejection", InvalidHeaderRejected),
    ("out-of-range and cyclic chain rejection", BadChainsRejected),
    ("standard offset sparse partition detection", StandardOffsetDetection),
    ("deleted directory entry filtering", DeletedEntriesIgnored),
    ("truncated stream handling", TruncatedStreamRejected),
    ("canonical 2 GiB FAT32 geometry", CanonicalLargeGeometry),
    ("allocation-table media marker semantics", MediaMarkerSemantics),
    ("invalid FAT chain marker rejection", InvalidChainMarkersRejected),
    ("retail probe endian and root validation", RetailProbeValidation),
    ("directory entry limit tail validation", DirectoryEntryLimitTailValidation),
    ("explicit raw-device length bypass", ExplicitRawDeviceLengthBypass),
    ("4 KiB-aligned USB bridge reads", AlignedUsbBridgeReads),
    ("nested FATX traversal and ranged file reads", NestedTraversalAndReads),
    ("FAT16 create overwrite truncate rename and reuse", Fat16Mutations),
    ("FAT32 mutations and name validation", Fat32MutationsAndNames),
    ("non-empty directory deletion refusal", NonEmptyDirectoryRefusal),
    ("4 KiB-aligned FATX writes", AlignedWrites),
    ("empty files retain a valid cluster and survive reopen", EmptyFileAndReopen),
    ("directory growth spans FATX clusters", DirectoryGrowth),
    ("FATX packed timestamps", PackedTimestamps),
    ("directory descendant moves are refused", DescendantMoveRefusal),
    ("disk-full writes preserve existing files", DiskFullPreservesExistingFiles),
    ("failed create preserves unrelated data", FailedCreatePreservesSentinel),
    ("free-space scan uses FAT pages", FreeSpaceUsesFatPages),
    ("bulk preallocation and aligned writes are batched", BulkWritesAreBatched),
    ("cached open-file writes avoid directory seek thrashing", CachedOpenFileWritesAvoidMetadataThrashing),
};

int failures = 0;
foreach ((string name, Action run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {name}: {exception.Message}"); }
}
return failures == 0 ? 0 : 1;

static void ValidHeaderAndRootListing()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    fixture.WriteEntry(1, 0, "CONTENT", 0x10, 1, 0);
    FatxVolume volume = fixture.Open();
    Assert(volume.Metadata.AllocationTable == FatxAllocationTable.Fat16, "expected FAT16");
    Assert(volume.ListRootDirectory().Single().Name == "CONTENT", "root name mismatch");
}

static void Fat16ChainTraversal()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    fixture.SetFat(1, 2); fixture.SetFat(2, fixture.EndOfChain);
    fixture.FillDeleted(1);
    fixture.WriteEntry(2, 0, "SECOND", 0, 3, 44);
    FatxVolume volume = fixture.Open();
    Assert(volume.ListRootDirectory().Single().Name == "SECOND", "FAT16 continuation was not read");
}

static void Fat32ChainTraversal()
{
    using var fixture = FatxFixture.Create(80 * 1024 * 1024);
    Assert(fixture.Table == FatxAllocationTable.Fat32, "fixture did not select FAT32");
    fixture.SetFat(1, 2); fixture.SetFat(2, fixture.EndOfChain);
    fixture.FillDeleted(1);
    fixture.WriteEntry(2, 0, "FAT32", 0, 7, 1234);
    FatxVolume volume = fixture.Open();
    Assert(volume.ListRootDirectory().Single().FileSize == 1234, "FAT32 continuation was not read");
}

static void InvalidHeaderRejected()
{
    using var stream = new SparseStream(0x4000);
    AssertThrows<FatxFormatException>(() => FatxVolume.Open(stream, 0, 0x4000));
}

static void BadChainsRejected()
{
    using (var outside = FatxFixture.Create(2 * 1024 * 1024))
    {
        outside.SetFat(1, (uint)(outside.ClusterCount + 1));
        outside.FillDeleted(1);
        FatxVolume volume = outside.Open();
        AssertThrows<FatxFormatException>(() => volume.ListRootDirectory());
    }
    using (var cycle = FatxFixture.Create(2 * 1024 * 1024))
    {
        cycle.SetFat(1, 2); cycle.SetFat(2, 1);
        cycle.FillDeleted(1); cycle.FillDeleted(2);
        FatxVolume volume = cycle.Open();
        AssertThrows<FatxFormatException>(() => volume.ListRootDirectory());
    }
}

static void StandardOffsetDetection()
{
    const long offset = 0x80000;
    const long length = 0x80000000;
    using var disk = new SparseStream(offset + length);
    FatxFixture.WriteVolume(disk, offset, length, FatxByteOrder.BigEndian, 32);
    IReadOnlyList<FatxPartitionCandidate> found = FatxPartitionProbe.DetectStandardRetailPartitions(disk);
    Assert(found.Count == 1 && found[0].Name == "Cache 0", "standard Cache 0 was not detected");
}

static void DeletedEntriesIgnored()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    fixture.WriteDeletedEntry(1, 0);
    fixture.WriteEntry(1, 0x40, "LIVE", 0, 1, 9);
    FatxVolume volume = fixture.Open();
    IReadOnlyList<FatxDirectoryEntry> items = volume.ListRootDirectory();
    Assert(items.Count == 1 && items[0].Name == "LIVE", "deleted entry appeared in listing");
}

static void TruncatedStreamRejected()
{
    using var source = new MemoryStream(new byte[32], writable: false);
    AssertThrows<FatxFormatException>(() => FatxVolume.Open(source, 0, 0x1000));
}

static void CanonicalLargeGeometry()
{
    const long length = 0x80000000;
    using var stream = new SparseStream(length);
    FatxFixture fixture = FatxFixture.WriteVolume(stream, 0, length, FatxByteOrder.BigEndian, 0x20);
    fixture.WriteEntry(1, 0, "CANONICAL", 0, 1, 42);
    FatxVolume volume = fixture.Open();
    Assert(volume.Metadata.AllocationTable == FatxAllocationTable.Fat32, "2 GiB volume must be FAT32");
    Assert(volume.Metadata.DataOffset == 0x82000, "canonical FATX data offset mismatch");
    Assert(volume.ListRootDirectory().Single().Name == "CANONICAL", "canonical data address was not read");
}

static void MediaMarkerSemantics()
{
    using (var accepted = FatxFixture.Create(2 * 1024 * 1024))
    {
        Assert(accepted.Open().ListRootDirectory().Count == 0, "media marker should be accepted");
    }
    using (var rejected = FatxFixture.Create(2 * 1024 * 1024))
    {
        rejected.SetFat(0, rejected.EndOfChain);
        AssertThrows<FatxFormatException>(() => rejected.Open());
    }
}

static void InvalidChainMarkersRejected()
{
    foreach (uint marker in new uint[] { 0, 0xFFF7, 0xFFF8, 0xFFFE })
    {
        using var fixture = FatxFixture.Create(2 * 1024 * 1024);
        fixture.SetFat(1, marker);
        AssertThrows<FatxFormatException>(() => fixture.Open().ListRootDirectory());
    }
    using var exactLast = FatxFixture.Create(2 * 1024 * 1024);
    exactLast.SetFat(1, exactLast.EndOfChain);
    Assert(exactLast.Open().ListRootDirectory().Count == 0, "only the exact LAST marker should terminate a chain");
}

static void RetailProbeValidation()
{
    const long offset = 0x80000;
    const long length = 0x80000000;
    using (var bigEndian = new SparseStream(offset + length))
    {
        FatxFixture.WriteVolume(bigEndian, offset, length, FatxByteOrder.BigEndian, 32);
        Assert(FatxPartitionProbe.DetectStandardRetailPartitions(bigEndian).Count == 1, "XTAF Cache 0 should be detected");
    }
    using (var littleEndian = new SparseStream(offset + length))
    {
        FatxFixture.WriteVolume(littleEndian, offset, length, FatxByteOrder.LittleEndian, 32);
        Assert(FatxPartitionProbe.DetectStandardRetailPartitions(littleEndian).Count == 0, "little-endian FATX must not be labeled retail Xbox 360");
    }
    foreach (uint rootValue in new uint[] { 0, 2, 0xFFFFFFFE })
    {
        using var invalid = new SparseStream(offset + length);
        FatxFixture fixture = FatxFixture.WriteVolume(invalid, offset, length, FatxByteOrder.BigEndian, 32);
        fixture.SetFat(1, rootValue == 2 ? 1 : rootValue);
        Assert(FatxPartitionProbe.DetectStandardRetailPartitions(invalid).Count == 0, "invalid root chain was advertised");
    }
}

static void DirectoryEntryLimitTailValidation()
{
    const long offset = 0x80000;
    const long length = 0x80000000;
    using (var malformed = new SparseStream(offset + length))
    {
        FatxFixture fixture = FatxFixture.WriteVolume(malformed, offset, length, FatxByteOrder.BigEndian, 0x20);
        WriteMaximumRootEntries(fixture, tail: 0);
        AssertThrows<FatxFormatException>(() => fixture.Open().ListRootDirectory());
        Assert(FatxPartitionProbe.DetectStandardRetailPartitions(malformed).Count == 0, "probe advertised a 4096-entry root with a free tail");
    }
    using (var valid = new SparseStream(offset + length))
    {
        FatxFixture fixture = FatxFixture.WriteVolume(valid, offset, length, FatxByteOrder.BigEndian, 0x20);
        WriteMaximumRootEntries(fixture, fixture.EndOfChain);
        Assert(fixture.Open().ListRootDirectory().Count == 4096, "exact LAST tail should accept exactly 4096 entries");
        Assert(FatxPartitionProbe.DetectStandardRetailPartitions(valid).Count == 1, "probe rejected a well-terminated 4096-entry root");
    }
}

static void ExplicitRawDeviceLengthBypass()
{
    const long offset = 0x80000;
    const long length = 0x80000000;
    using var backing = new SparseStream(offset + length);
    FatxFixture.WriteVolume(backing, offset, length, FatxByteOrder.BigEndian, 0x20);
    using var rawDevice = new LengthlessStream(backing);

    IReadOnlyList<FatxPartitionCandidate> found =
        FatxPartitionProbe.DetectStandardRetailPartitions(rawDevice, offset + length);
    Assert(found.Count == 1, "explicit device capacity should avoid querying Stream.Length");

    FatxVolume volume = FatxVolume.Open(rawDevice, offset, length, offset + length);
    Assert(volume.ListRootDirectory().Count == 0, "explicit source length should open the raw-device volume");
}

static void AlignedUsbBridgeReads()
{
    const long offset = 0x80000;
    const long length = 0x80000000;
    using var backing = new SparseStream(offset + length);
    FatxFixture.WriteVolume(backing, offset, length, FatxByteOrder.BigEndian, 0x20);
    using var bridge = new StrictAlignedReadStream(backing, 0x1000);

    IReadOnlyList<FatxPartitionCandidate> found =
        FatxPartitionProbe.DetectStandardRetailPartitions(bridge, offset + length);
    Assert(found.Count == 1, "aligned-only USB bridge should be probed successfully");
}

static void NestedTraversalAndReads()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    FatxVolume volume = fixture.Open();
    volume.CreateDirectory("/A"); volume.CreateDirectory("/A/B"); volume.CreateFile("/A/B/FILE");
    volume.WriteFile("/A/B/FILE", 0, "abcdefghijklmnop"u8);
    Assert(volume.EnumerateDirectory("/A").Single().Name == "B", "nested directory was not enumerated");
    Assert(System.Text.Encoding.ASCII.GetString(volume.ReadFile("/A/B/FILE", 3, 5)) == "defgh", "ranged read mismatch");
}

static void Fat16Mutations()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    FatxVolume volume = fixture.Open();
    volume.CreateDirectory("/ONE"); volume.CreateDirectory("/TWO"); volume.CreateFile("/ONE/FILE");
    byte[] first = Enumerable.Range(0, 2500).Select(i => (byte)(i % 251)).ToArray();
    volume.WriteFile("/ONE/FILE", 0, first); volume.WriteFile("/ONE/FILE", 1000, "SIDE"u8);
    Assert(volume.ReadFile("/ONE/FILE", 1000, 4).SequenceEqual("SIDE"u8.ToArray()), "overwrite was not persisted");
    volume.SetLength("/ONE/FILE", 600); Assert(volume.GetEntry("/ONE/FILE").Entry.FileSize == 600, "truncate failed");
    volume.Move("/ONE/FILE", "/TWO/MOVED"); Assert(volume.GetEntry("/TWO/MOVED").Entry.FileSize == 600, "cross-directory rename failed");
    long before = volume.FreeSpace; volume.DeleteFile("/TWO/MOVED"); Assert(volume.FreeSpace > before, "deleted chain was not released");
}

static void Fat32MutationsAndNames()
{
    using var fixture = FatxFixture.Create(80 * 1024 * 1024);
    FatxVolume volume = fixture.Open(); volume.CreateFile("/LOWER"); volume.WriteFile("/LOWER", 0, "fat32"u8);
    Assert(System.Text.Encoding.ASCII.GetString(volume.ReadFile("/lower", 0, 5)) == "fat32", "case-insensitive lookup failed");
    AssertThrows<IOException>(() => volume.CreateFile("/lower"));
    AssertThrows<ArgumentException>(() => volume.CreateFile("/bad:name"));
    AssertThrows<IOException>(() => volume.SetLength("/LOWER", (long)uint.MaxValue + 1));
}

static void NonEmptyDirectoryRefusal()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    FatxVolume volume = fixture.Open(); volume.CreateDirectory("/DIR"); volume.CreateFile("/DIR/CHILD");
    AssertThrows<IOException>(() => volume.DeleteDirectory("/DIR"));
}

static void AlignedWrites()
{
    using var backing = new SparseStream(2 * 1024 * 1024);
    FatxFixture.WriteVolume(backing, 0, 2 * 1024 * 1024, FatxByteOrder.LittleEndian, 2);
    using var bridge = new StrictAlignedWriteStream(backing, 0x1000);
    FatxVolume volume = FatxVolume.Open(bridge, 0, 2 * 1024 * 1024);
    volume.CreateFile("/WRITTEN"); volume.WriteFile("/WRITTEN", 0, "aligned"u8);
    Assert(System.Text.Encoding.ASCII.GetString(volume.ReadFile("/WRITTEN", 0, 7)) == "aligned", "aligned write failed");
}

static void EmptyFileAndReopen()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    FatxVolume volume = fixture.Open();
    volume.CreateFile("/EMPTY");
    Assert(volume.GetEntry("/EMPTY").Entry.FirstCluster != 0, "empty file did not receive a valid cluster");
    volume.SetLength("/EMPTY", 0);
    Assert(fixture.Open().GetEntry("/EMPTY").Entry.FirstCluster != 0, "truncated empty file lost its cluster after reopen");
}

static void DirectoryGrowth()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    FatxVolume volume = fixture.Open();
    volume.CreateDirectory("/DIR");
    for (int i = 0; i < 17; i++) volume.CreateFile($"/DIR/F{i:D2}");
    Assert(volume.EnumerateDirectory("/DIR").Count == 17, "directory did not expand into a second cluster");
    Assert(fixture.Open().EnumerateDirectory("/DIR").Count == 17, "expanded directory failed reopen validation");
}

static void PackedTimestamps()
{
    var source = new DateTimeOffset(2024, 2, 3, 4, 5, 7, TimeSpan.Zero);
    uint packed = FatxVolume.EncodeTimestamp(source);
    DateTimeOffset decoded = FatxVolume.DecodeTimestamp(packed) ?? throw new InvalidOperationException("timestamp did not decode");
    Assert(decoded == new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.Zero), "FATX timestamp packing is incorrect");
    Assert(FatxVolume.DecodeTimestamp(0) is null, "zero FATX timestamp must remain unset");
}

static void DescendantMoveRefusal()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    FatxVolume volume = fixture.Open(); volume.CreateDirectory("/A"); volume.CreateDirectory("/A/B");
    AssertThrows<IOException>(() => volume.Move("/A", "/A/B/A"));
    Assert(volume.EnumerateDirectory("/").Single().Name == "A", "failed descendant move changed the source directory");
}

static void DiskFullPreservesExistingFiles()
{
    using var fixture = FatxFixture.Create(20 * 1024);
    FatxVolume volume = fixture.Open(); volume.CreateFile("/KEEP"); volume.WriteFile("/KEEP", 0, "safe"u8);
    bool full = false;
    for (int i = 0; i < 32; i++)
    {
        try { volume.CreateFile($"/F{i:D2}"); }
        catch (IOException exception) when (exception.Message.Contains("full", StringComparison.OrdinalIgnoreCase)) { full = true; break; }
    }
    Assert(full, "small fixture did not reach FATX disk-full state");
    FatxVolume reopened = fixture.Open();
    Assert(System.Text.Encoding.ASCII.GetString(reopened.ReadFile("/KEEP", 0, 4)) == "safe", "disk-full attempt altered an existing file");
}

static void FailedCreatePreservesSentinel()
{
    // File creation now has two durable writes: allocate the FAT entry, then publish
    // the directory entry. Inject failure before each to preserve the crash-safety check.
    for (int successfulWrites = 0; successfulWrites < 2; successfulWrites++)
    {
        using var fixture = FatxFixture.Create(2 * 1024 * 1024);
        FatxVolume baseline = fixture.Open(); baseline.CreateFile("/KEEP"); baseline.WriteFile("/KEEP", 0, "sentinel"u8);
        using var failing = new FailingWriteStream(fixture.Stream, successfulWrites);
        FatxVolume attempted = FatxVolume.Open(failing, 0, fixture.Length);
        AssertThrows<IOException>(() => attempted.CreateFile("/NEW"));
        FatxVolume reopened = fixture.Open();
        Assert(System.Text.Encoding.ASCII.GetString(reopened.ReadFile("/KEEP", 0, 8)) == "sentinel", "failed create overwrote an unrelated file");
    }
}

static void FreeSpaceUsesFatPages()
{
    using var backing = new SparseStream(80 * 1024 * 1024);
    FatxFixture.WriteVolume(backing, 0, backing.Length, FatxByteOrder.LittleEndian, 2);
    using var counting = new CountingReadStream(backing);
    FatxVolume volume = FatxVolume.Open(counting, 0, backing.Length);
    _ = volume.FreeSpace;
    Assert(counting.ReadCalls < 100, "free-space scan issued one raw read per FAT cluster instead of using cached FAT pages");
}

static void BulkWritesAreBatched()
{
    var stream = new SparseStream(16 * 1024 * 1024);
    using var fixture = FatxFixture.WriteVolume(stream, 0, stream.Length, FatxByteOrder.LittleEndian, 8);
    FatxVolume volume = fixture.Open();
    volume.CreateFile("/bulk.bin");
    stream.ResetCounters();
    volume.Preallocate("/bulk.bin", 4 * 1024 * 1024);
    volume.Flush();
    Assert(volume.GetEntry("/bulk.bin").Entry.FileSize == 0, "preallocation changed logical file size");
    Assert(stream.BytesWritten <= 16 * 1024, $"preallocation wrote {stream.BytesWritten} bytes instead of batching FAT metadata");

    stream.ResetCounters();
    byte[] data = Enumerable.Repeat((byte)0x5A, 1024 * 1024).ToArray();
    volume.WriteFile("/bulk.bin", 0, data);
    volume.Flush();
    Assert(stream.WriteOperations <= 4, $"aligned 1 MiB write used {stream.WriteOperations} underlying writes");
    Assert(stream.ReadOperations <= 2, $"aligned 1 MiB write used {stream.ReadOperations} underlying reads");
    Assert(volume.ReadFile("/bulk.bin", 0, data.Length).SequenceEqual(data), "batched write data mismatch");
}

static void CachedOpenFileWritesAvoidMetadataThrashing()
{
    var stream = new SparseStream(32 * 1024 * 1024);
    using var fixture = FatxFixture.WriteVolume(stream, 0, stream.Length, FatxByteOrder.LittleEndian, 8);
    FatxVolume volume = fixture.Open();
    volume.CreateFile("/stream.bin");
    FatxDirectoryEntry entry = volume.Preallocate("/stream.bin", 8 * 1024 * 1024);
    byte[] block = Enumerable.Repeat((byte)0xA7, 64 * 1024).ToArray();

    stream.ResetCounters();
    for (int offset = 0; offset < 8 * 1024 * 1024; offset += block.Length)
        entry = volume.WriteFile(entry, offset, block);
    Assert(stream.ReadOperations == 0, $"cached sequential writes performed {stream.ReadOperations} metadata reads before commit");
    Assert(stream.WriteOperations == 128, $"cached sequential writes performed {stream.WriteOperations} writes for 128 data blocks");
    byte[] readBack = new byte[block.Length];
    Assert(volume.ReadFile(entry, 0, readBack) == block.Length && readBack.SequenceEqual(block),
        "cached entry could not read initialized data");

    stream.ResetCounters();
    volume.CommitFile("/stream.bin", entry);
    volume.Flush();
    Assert(stream.ReadOperations <= 2, $"one metadata commit performed {stream.ReadOperations} underlying reads");
    Assert(stream.WriteOperations <= 1, $"one metadata commit performed {stream.WriteOperations} underlying writes");
    Assert(volume.GetEntry("/stream.bin").Entry.FileSize == 8 * 1024 * 1024, "cached file size was not published at commit");
}

static void WriteMaximumRootEntries(FatxFixture fixture, uint tail)
{
    const int entriesPerCluster = 256; // 0x20 sectors × 0x200 bytes / 0x40-byte entry.
    for (uint cluster = 1; cluster <= 16; cluster++)
    {
        fixture.SetFat(cluster, cluster == 16 ? tail : cluster + 1);
        for (int entry = 0; entry < entriesPerCluster; entry++)
            fixture.WriteEntry(cluster, entry * 0x40, $"E{cluster:D2}{entry:D3}", 0, 1, 0);
    }
}

static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void AssertThrows<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class FatxFixture : IDisposable
{
    private const int Header = 0x1000;
    private readonly SparseStream stream;
    private readonly long offset;
    private readonly long length;
    private readonly long dataOffset;
    private readonly int clusterBytes;
    private readonly int entryBytes;
    private readonly FatxByteOrder order;
    private FatxFixture(SparseStream stream, long offset, long length, long dataOffset, long count, FatxAllocationTable table, FatxByteOrder order, uint sectorsPerCluster)
    {
        this.stream = stream; this.offset = offset; this.length = length; this.dataOffset = dataOffset; ClusterCount = count; Table = table; this.order = order;
        clusterBytes = checked((int)sectorsPerCluster * 512); entryBytes = table == FatxAllocationTable.Fat16 ? 2 : 4;
    }
    public long ClusterCount { get; }
    public Stream Stream => stream;
    public long Length => length;
    public FatxAllocationTable Table { get; }
    public uint EndOfChain => Table == FatxAllocationTable.Fat16 ? 0xFFFFu : 0xFFFFFFFFu;
    public uint MediaMarker => Table == FatxAllocationTable.Fat16 ? 0xFFF8u : 0xFFFFFFF8u;
    public static FatxFixture Create(long length)
    {
        var stream = new SparseStream(length);
        return WriteVolume(stream, 0, length, FatxByteOrder.LittleEndian, 2);
    }
    public static FatxFixture WriteVolume(SparseStream stream, long offset, long length, FatxByteOrder order, uint sectorsPerCluster)
    {
        (FatxAllocationTable table, long count, long data) = KnownGeometry(length, sectorsPerCluster);
        var fixture = new FatxFixture(stream, offset, length, data, count, table, order, sectorsPerCluster);
        Span<byte> header = stackalloc byte[Header];
        (order == FatxByteOrder.LittleEndian ? "FATX"u8 : "XTAF"u8).CopyTo(header);
        WriteUInt(header.Slice(4, 4), 0x11223344, order);
        WriteUInt(header.Slice(8, 4), sectorsPerCluster, order);
        WriteUInt(header.Slice(12, 4), 1, order);
        fixture.Write(offset, header);
        fixture.SetFat(0, fixture.MediaMarker); fixture.SetFat(1, fixture.EndOfChain);
        return fixture;
    }
    public FatxVolume Open() => FatxVolume.Open(stream, offset, length);
    public void SetFat(uint index, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (entryBytes == 2) WriteUShort(bytes, checked((ushort)value), order); else WriteUInt(bytes, value, order);
        Write(offset + Header + (long)index * entryBytes, bytes[..entryBytes]);
    }
    public void WriteEntry(uint cluster, int entryOffset, string name, byte attributes, uint firstCluster, uint fileSize)
    {
        var entry = new byte[0x40]; entry[0] = checked((byte)name.Length); entry[1] = attributes;
        System.Text.Encoding.ASCII.GetBytes(name, entry.AsSpan(2));
        WriteUInt(entry.AsSpan(0x2C, 4), firstCluster, order); WriteUInt(entry.AsSpan(0x30, 4), fileSize, order);
        Write(ClusterPosition(cluster) + entryOffset, entry);
    }
    public void WriteDeletedEntry(uint cluster, int entryOffset)
    {
        Span<byte> entry = stackalloc byte[0x40]; entry[0] = 0xE5; Write(ClusterPosition(cluster) + entryOffset, entry);
    }
    public void FillDeleted(uint cluster)
    {
        for (int offset = 0; offset < clusterBytes; offset += 0x40) WriteDeletedEntry(cluster, offset);
    }
    private long ClusterPosition(uint cluster) => offset + dataOffset + ((long)cluster - 1) * clusterBytes;
    private void Write(long at, ReadOnlySpan<byte> bytes) { stream.Position = at; stream.Write(bytes); }
    public void Dispose() => stream.Dispose();
    // Deliberately direct fixture math (not the parser's old fixed-point algorithm): these
    // values model the documented chain map, then golden tests assert known on-disk addresses.
    private static (FatxAllocationTable, long, long) KnownGeometry(long length, uint sectorsPerCluster)
    {
        long bytesPerCluster = checked((long)sectorsPerCluster * 512);
        long mapEntries = length / bytesPerCluster + 1;
        FatxAllocationTable table = mapEntries < 0xFFF0 ? FatxAllocationTable.Fat16 : FatxAllocationTable.Fat32;
        long fatBytes = mapEntries * (table == FatxAllocationTable.Fat16 ? 2 : 4);
        long data = Header + Align(fatBytes, Header);
        return (table, (length - data) / bytesPerCluster, data);
    }
    private static long Align(long value, int alignment) => (value + alignment - 1) / alignment * alignment;
    private static void WriteUInt(Span<byte> target, uint value, FatxByteOrder order) { if (order == FatxByteOrder.LittleEndian) BinaryPrimitives.WriteUInt32LittleEndian(target, value); else BinaryPrimitives.WriteUInt32BigEndian(target, value); }
    private static void WriteUShort(Span<byte> target, ushort value, FatxByteOrder order) { if (order == FatxByteOrder.LittleEndian) BinaryPrimitives.WriteUInt16LittleEndian(target, value); else BinaryPrimitives.WriteUInt16BigEndian(target, value); }
}

sealed class SparseStream(long length) : Stream
{
    private const int BlockSize = 4096;
    private readonly Dictionary<long, byte[]> blocks = [];
    private long position;
    public long ReadOperations { get; private set; }
    public long WriteOperations { get; private set; }
    public long BytesRead { get; private set; }
    public long BytesWritten { get; private set; }
    public void ResetCounters() { ReadOperations = WriteOperations = BytesRead = BytesWritten = 0; }
    public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => true;
    public override long Length => length; public override long Position { get => position; set { if (value < 0 || value > length) throw new ArgumentOutOfRangeException(nameof(value)); position = value; } }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        int available = checked((int)Math.Min(buffer.Length, length - position));
        ReadOperations++; BytesRead += available;
        for (int i = 0; i < available; i++) { long block = (position + i) / BlockSize; int at = (int)((position + i) % BlockSize); buffer[i] = blocks.TryGetValue(block, out byte[]? data) ? data[at] : (byte)0; }
        position += available; return available;
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > length - position) throw new EndOfStreamException();
        WriteOperations++; BytesWritten += buffer.Length;
        for (int i = 0; i < buffer.Length; i++) { long block = (position + i) / BlockSize; int at = (int)((position + i) % BlockSize); if (!blocks.TryGetValue(block, out byte[]? data)) { data = new byte[BlockSize]; blocks.Add(block, data); } data[at] = buffer[i]; }
        position += buffer.Length;
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(position + offset), SeekOrigin.End => checked(length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
    public override void SetLength(long value) => throw new NotSupportedException();
}

sealed class LengthlessStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => throw new IOException("The parameter is incorrect.");
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class StrictAlignedReadStream(Stream inner, int alignment) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => throw new IOException("The parameter is incorrect.");
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (Position % alignment != 0 || buffer.Length % alignment != 0)
            throw new IOException("Incorrect function.");
        return inner.Read(buffer);
    }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class StrictAlignedWriteStream(Stream inner, int alignment) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) { if (Position % alignment != 0 || buffer.Length % alignment != 0) throw new IOException("Incorrect function."); return inner.Read(buffer); }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) { if (Position % alignment != 0 || buffer.Length % alignment != 0) throw new IOException("Incorrect function."); inner.Write(buffer); }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
}

sealed class FailingWriteStream(Stream inner, int successfulWrites) : Stream
{
    private int writesRemaining = successfulWrites;
    public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (writesRemaining-- <= 0) throw new IOException("Injected write failure.");
        inner.Write(buffer);
    }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
}

sealed class CountingReadStream(Stream inner) : Stream
{
    public int ReadCalls { get; private set; }
    public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) { ReadCalls++; return inner.Read(buffer); }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
