using System.Buffers.Binary;
using System.Text;

namespace FatxBridge.Core;

public enum FatxByteOrder { LittleEndian, BigEndian }
public enum FatxAllocationTable { Fat16, Fat32 }
public sealed record FatxVolumeMetadata(uint SerialNumber, uint SectorsPerCluster, uint RootFirstCluster, FatxByteOrder ByteOrder, FatxAllocationTable AllocationTable, long PartitionOffset, long PartitionLength, long FatOffset, long DataOffset, long ClusterCount, int SectorSize);
public sealed record FatxDirectoryEntry(string Name, byte Attributes, uint FirstCluster, uint FileSize, uint CreationTimestamp, uint LastWriteTimestamp, uint LastAccessTimestamp) { public bool IsDirectory => (Attributes & 0x10) != 0; }
public sealed record FatxPathEntry(string Path, FatxDirectoryEntry Entry);
public sealed class FatxFormatException : IOException { public FatxFormatException(string message) : base(message) { } }

/// <summary>A partition-bounded FATX volume. FATX is non-journaled: writes are carefully ordered but cannot be crash-atomic.</summary>
public sealed class FatxVolume
{
    private const int HeaderSize = 0x1000, RawAlignment = 0x1000, EntrySize = 0x40, MaximumDirectoryEntries = 4096;
    private static readonly Encoding Ascii = Encoding.ASCII;
    private readonly Stream source;
    private readonly object mutationGate = new();
    private readonly Dictionary<uint, List<uint>> chainCache = [];
    private byte[]? fatPage;
    private long fatPageOffset = -1;
    private bool fatPageDirty;
    private long? cachedFreeClusterCount;
    private uint allocationCursor = 1;
    private FatxVolume(Stream source, FatxVolumeMetadata metadata) { this.source = source; Metadata = metadata; }
    public FatxVolumeMetadata Metadata { get; }
    public bool CanWrite => source.CanWrite;
    public long ClusterSize => checked((long)Metadata.SectorsPerCluster * Metadata.SectorSize);
    public long TotalSize => checked(Metadata.ClusterCount * ClusterSize);
    public long FreeSpace { get { lock (mutationGate) return checked(CountFreeClusters() * ClusterSize); } }
    /// <summary>
    /// A non-blocking free-space estimate for filesystem hosts. Counting every FAT
    /// entry on a USB-attached Xbox drive can take long enough for Windows to time
    /// out its first volume-information request, so an unknown count is reported as
    /// the usable volume size until a normal allocation scan has populated the cache.
    /// </summary>
    public long ReportedFreeSpace { get { lock (mutationGate) return cachedFreeClusterCount is long count ? checked(count * ClusterSize) : TotalSize; } }
    public long UsedSpace => TotalSize - FreeSpace;

    public static FatxVolume Open(Stream source, long partitionOffset, long partitionLength, long? sourceLength = null, int sectorSize = 512)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek) throw new ArgumentException("The FATX source must be readable and seekable.", nameof(source));
        if (sectorSize is not (512 or 4096))
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "FATX logical sectors must be 512 or 4096 bytes.");
        long total = sourceLength ?? source.Length;
        if (partitionOffset < 0 || partitionLength < HeaderSize || !Within(partitionOffset, partitionLength, total)) throw new FatxFormatException("The partition range is outside the source stream.");
        var header = new byte[HeaderSize]; ReadAt(source, partitionOffset, header, partitionOffset, partitionLength);
        FatxByteOrder order = header.AsSpan(0, 4).SequenceEqual("FATX"u8) ? FatxByteOrder.LittleEndian : header.AsSpan(0, 4).SequenceEqual("XTAF"u8) ? FatxByteOrder.BigEndian : throw new FatxFormatException("The partition does not have a FATX signature.");
        uint spc = U32(header.AsSpan(8, 4), order), root = U32(header.AsSpan(12, 4), order);
        if (spc is not (2 or 4 or 8 or 16 or 32 or 64 or 128)) throw new FatxFormatException("FATX sectors-per-cluster is invalid.");
        var layout = Layout(partitionLength, checked((long)spc * sectorSize));
        if (root is 0 || root > layout.Count) throw new FatxFormatException("The FATX root cluster is outside the data area.");
        var result = new FatxVolume(source, new FatxVolumeMetadata(U32(header.AsSpan(4, 4), order), spc, root, order, layout.Table, partitionOffset, partitionLength, partitionOffset + HeaderSize, partitionOffset + layout.Data, layout.Count, sectorSize));
        if (result.ReadFat(0) != result.Media) throw new FatxFormatException("The FATX allocation-table media marker is invalid.");
        return result;
    }

    /// <summary>Encodes the Xbox 360 FATX packed date/time representation (UTC, 1980-based, two-second precision).</summary>
    public static uint EncodeTimestamp(DateTimeOffset value) => EncodeTimestamp(value, FatxByteOrder.BigEndian);

    /// <summary>Encodes the platform-specific FATX packed date/time representation.</summary>
    public static uint EncodeTimestamp(DateTimeOffset value, FatxByteOrder byteOrder)
    {
        DateTime utc = value.UtcDateTime;
        int epoch = byteOrder == FatxByteOrder.LittleEndian ? 2000 : 1980;
        if (utc.Year < epoch) utc = new DateTime(epoch, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        if (utc.Year > epoch + 127) utc = new DateTime(epoch + 127, 12, 31, 23, 59, 58, DateTimeKind.Utc);
        return checked((uint)(((utc.Year - epoch) << 25) | (utc.Month << 21) | (utc.Day << 16) |
            (utc.Hour << 11) | (utc.Minute << 5) | (utc.Second / 2)));
    }

    /// <summary>Decodes an Xbox 360 FATX packed date/time. Zero and malformed values have no usable timestamp.</summary>
    public static DateTimeOffset? DecodeTimestamp(uint value) => DecodeTimestamp(value, FatxByteOrder.BigEndian);

    /// <summary>Decodes a platform-specific FATX packed date/time.</summary>
    public static DateTimeOffset? DecodeTimestamp(uint value, FatxByteOrder byteOrder)
    {
        if (value == 0) return null;
        int second = checked((int)(value & 0x1F)) * 2;
        int minute = checked((int)((value >> 5) & 0x3F));
        int hour = checked((int)((value >> 11) & 0x1F));
        int day = checked((int)((value >> 16) & 0x1F));
        int month = checked((int)((value >> 21) & 0x0F));
        int epoch = byteOrder == FatxByteOrder.LittleEndian ? 2000 : 1980;
        int year = checked((int)((value >> 25) & 0x7F)) + epoch;
        try { return new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.Zero); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    public IReadOnlyList<FatxDirectoryEntry> ListRootDirectory() => EnumerateDirectory("/");
    public IReadOnlyList<FatxDirectoryEntry> EnumerateDirectory(string path) { lock (mutationGate) return ReadDirectory(ResolveDirectory(path)).Where(s => !s.Deleted && !s.Empty).Select(s => s.Entry!).ToArray(); }
    public FatxPathEntry GetEntry(string path) { lock (mutationGate) { string p = CanonicalPath(path); if (p == "/") throw new ArgumentException("The root has no entry.", nameof(path)); Slot slot = FindEntry(p) ?? throw new FileNotFoundException("The FATX path does not exist.", path); return new FatxPathEntry(p, slot.Entry!); } }
    public bool TryGetEntry(string path, out FatxPathEntry? entry) { try { entry = GetEntry(path); return true; } catch (FileNotFoundException) { entry = null; return false; } }
    public byte[] ReadFile(string path, long offset, int count) { ArgumentOutOfRangeException.ThrowIfNegative(offset); ArgumentOutOfRangeException.ThrowIfNegative(count); var result = new byte[count]; int read = ReadFile(path, offset, result); return read == count ? result : result[..read]; }
    public int ReadFile(string path, long offset, Span<byte> target)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset); lock (mutationGate) { Slot slot = FindEntry(CanonicalPath(path)) ?? throw new FileNotFoundException("The FATX path does not exist.", path); if (slot.Entry!.IsDirectory) throw new IOException("A directory cannot be read as a file."); int read = checked((int)Math.Min(target.Length, Math.Max(0, (long)slot.Entry.FileSize - offset))); ReadFileData(slot.Entry, offset, target[..read]); return read; }
    }
    /// <summary>Reads through a previously opened entry without rescanning its parent directory.</summary>
    public int ReadFile(FatxDirectoryEntry entry, long offset, Span<byte> target)
    {
        ArgumentNullException.ThrowIfNull(entry); ArgumentOutOfRangeException.ThrowIfNegative(offset);
        lock (mutationGate) { if (entry.IsDirectory) throw new IOException("A directory cannot be read as a file."); int read = checked((int)Math.Min(target.Length, Math.Max(0, (long)entry.FileSize - offset))); ReadFileData(entry, offset, target[..read]); return read; }
    }
    public void CreateFile(string path) => Create(path, false);
    public void CreateDirectory(string path) => Create(path, true);
    public FatxDirectoryEntry WriteFile(string path, long offset, ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        lock (mutationGate)
        {
            EnsureWritable();
            Slot slot = FindEntry(CanonicalPath(path)) ?? throw new FileNotFoundException("The FATX path does not exist.", path);
            FatxDirectoryEntry entry = WriteFileCore(slot.Entry!, offset, data);
            if (!ReferenceEquals(entry, slot.Entry)) WriteEntry(slot.Position, entry);
            return entry;
        }
    }
    /// <summary>Writes through a cached open-file entry. Call <see cref="CommitFile"/> at a durability boundary.</summary>
    public FatxDirectoryEntry WriteFile(FatxDirectoryEntry entry, long offset, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(entry); ArgumentOutOfRangeException.ThrowIfNegative(offset);
        lock (mutationGate) { EnsureWritable(); return WriteFileCore(entry, offset, data); }
    }
    /// <summary>Publishes a cached open-file entry to its parent directory.</summary>
    public void CommitFile(string path, FatxDirectoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (mutationGate)
        {
            EnsureWritable(); Slot slot = FindEntry(CanonicalPath(path)) ?? throw new FileNotFoundException("The FATX path does not exist.", path);
            if (slot.Entry!.IsDirectory || entry.IsDirectory || slot.Entry.FirstCluster != entry.FirstCluster)
                throw new IOException("The cached FATX file entry no longer matches its directory slot.");
            WriteEntry(slot.Position, entry with { Name = slot.Entry.Name });
        }
    }
    public FatxDirectoryEntry SetLength(string path, long length)
    {
        if (length < 0 || length > uint.MaxValue) throw new IOException("FATX files are limited to the 32-bit on-disk size field."); lock (mutationGate) { EnsureWritable(); Slot slot = FindEntry(CanonicalPath(path)) ?? throw new FileNotFoundException("The FATX path does not exist.", path); if (slot.Entry!.IsDirectory) throw new IOException("A directory cannot have a file length."); if (length > slot.Entry.FileSize) return ExtendFile(slot, (uint)length); if (length < slot.Entry.FileSize) return TruncateFile(slot, (uint)length); return slot.Entry; }
    }
    public FatxDirectoryEntry Preallocate(string path, long length)
    {
        if (length < 0 || length > uint.MaxValue) throw new IOException("FATX files are limited to the 32-bit on-disk size field."); lock (mutationGate) { EnsureWritable(); Slot slot = FindEntry(CanonicalPath(path)) ?? throw new FileNotFoundException("The FATX path does not exist.", path); if (slot.Entry!.IsDirectory) throw new IOException("A directory cannot have allocation space."); EnsureCapacity(slot.Entry, checked((uint)length)); return slot.Entry; }
    }
    public void Flush() { lock (mutationGate) { FlushFatPage(); source.Flush(); } }
    public void Move(string sourcePath, string destinationPath)
    {
        lock (mutationGate) { EnsureWritable(); string from = CanonicalPath(sourcePath), to = CanonicalPath(destinationPath); if (from == "/" || to == "/") throw new IOException("The FATX root cannot be renamed."); Slot sourceSlot = FindEntry(from) ?? throw new FileNotFoundException("The FATX path does not exist.", sourcePath); if (sourceSlot.Entry!.IsDirectory && to.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase)) throw new IOException("A FATX directory cannot be moved into itself or one of its descendants."); (uint directory, string name) = Parent(to); ValidateName(name); if (FindInDirectory(directory, name) is not null) throw new IOException("A FATX item with that name already exists."); Slot target = FindFreeSlot(directory); WriteEntry(target.Position, sourceSlot.Entry! with { Name = name }); source.Flush(); DeleteSlot(sourceSlot.Position); source.Flush(); }
    }
    public void DeleteFile(string path) => Delete(path, false);
    public void DeleteDirectory(string path) => Delete(path, true);

    private FatxDirectoryEntry WriteFileCore(FatxDirectoryEntry entry, long offset, ReadOnlySpan<byte> data)
    {
        if (entry.IsDirectory) throw new IOException("A directory cannot be written as a file.");
        long end = checked(offset + data.Length);
        if (end > uint.MaxValue) throw new IOException("FATX files are limited to the 32-bit on-disk size field.");
        if (end > entry.FileSize)
        {
            EnsureCapacity(entry, checked((uint)end));
            if (offset > entry.FileSize) ZeroFileRange(entry, entry.FileSize, offset - entry.FileSize);
            WriteFileData(entry, offset, data);
            return entry with { FileSize = checked((uint)end), LastWriteTimestamp = Now() };
        }
        WriteFileData(entry, offset, data);
        return entry;
        // The filesystem host flushes at the Windows Flush/Cleanup/Close durability
        // boundaries. Forcing FlushFileBuffers or rewriting the directory entry after
        // every callback turns a sequential USB transfer into metadata seek thrashing.
    }

    private void Create(string path, bool directory)
    {
        lock (mutationGate) { EnsureWritable(); string p = CanonicalPath(path); if (p == "/") throw new IOException("The FATX root already exists."); (uint parent, string name) = Parent(p); ValidateName(name); if (FindInDirectory(parent, name) is not null) throw new IOException("A FATX item with that name already exists."); uint first = AllocateClusters(1)[0]; if (directory) { ZeroCluster(first); source.Flush(); } WriteFat(first, Last); FlushFatPage(); source.Flush(); chainCache[first] = [first]; Slot slot = FindFreeSlot(parent); uint now = Now(); WriteEntry(slot.Position, new FatxDirectoryEntry(name, directory ? (byte)0x10 : (byte)0, first, 0, now, now, now)); source.Flush(); }
    }
    private void Delete(string path, bool directory)
    {
        lock (mutationGate) { EnsureWritable(); string p = CanonicalPath(path); if (p == "/") throw new IOException("The FATX root cannot be deleted."); Slot slot = FindEntry(p) ?? throw new FileNotFoundException("The FATX path does not exist.", path); if (slot.Entry!.IsDirectory != directory) throw new IOException(directory ? "The path is not a directory." : "The path is not a file."); if (directory && ReadDirectory(slot.Entry.FirstCluster).Any(s => !s.Deleted && !s.Empty)) throw new IOException("A non-empty FATX directory cannot be deleted."); DeleteSlot(slot.Position); source.Flush(); FreeChain(slot.Entry.FirstCluster); FlushFatPage(); source.Flush(); }
    }
    private FatxDirectoryEntry ExtendFile(Slot slot, uint length)
    {
        FatxDirectoryEntry old = slot.Entry!; EnsureCapacity(old, length); ZeroFileRange(old, old.FileSize, length - old.FileSize); source.Flush(); FatxDirectoryEntry updated = old with { FileSize = length, LastWriteTimestamp = Now() }; WriteEntry(slot.Position, updated); source.Flush(); return updated;
    }
    private FatxDirectoryEntry TruncateFile(Slot slot, uint length)
    {
        FatxDirectoryEntry old = slot.Entry!; List<uint> chain = GetChain(old.FirstCluster); int keep = ClustersFor(length); FatxDirectoryEntry updated = old with { FileSize = length, LastWriteTimestamp = Now() }; WriteEntry(slot.Position, updated); source.Flush(); if (chain.Count > keep) { uint[] freed = chain.Skip(keep).ToArray(); WriteFat(chain[keep - 1], Last); FlushFatPage(); source.Flush(); foreach (uint c in freed) WriteFat(c, 0); FlushFatPage(); source.Flush(); chain.RemoveRange(keep, chain.Count - keep); } return updated;
    }

    private void EnsureCapacity(FatxDirectoryEntry entry, uint length)
    {
        List<uint> chain = GetChain(entry.FirstCluster); int needed = ClustersFor(length);
        if (needed <= chain.Count) return;
        uint[] add = AllocateClusters(needed - chain.Count);
        for (int i = 0; i < add.Length; i++) WriteFat(add[i], i + 1 < add.Length ? add[i + 1] : Last);
        WriteFat(chain[^1], add[0]);
        FlushFatPage(); source.Flush();
        chain.AddRange(add);
    }

    private void ZeroFileRange(FatxDirectoryEntry entry, long offset, long length)
    {
        if (length <= 0) return;
        byte[] zeros = new byte[checked((int)Math.Min(length, 1024 * 1024))];
        long written = 0;
        while (written < length)
        {
            int take = checked((int)Math.Min(zeros.Length, length - written));
            WriteFileData(entry, offset + written, zeros.AsSpan(0, take));
            written += take;
        }
    }

    private long CountFreeClusters()
    {
        if (cachedFreeClusterCount is long cached) return cached;
        long count = 0;
        for (uint c = 1; c <= Metadata.ClusterCount; c++) if (ReadFat(c) == 0) count++;
        return (cachedFreeClusterCount = count).Value;
    }
    private uint ResolveDirectory(string path) { string p = CanonicalPath(path); if (p == "/") return Metadata.RootFirstCluster; Slot slot = FindEntry(p) ?? throw new DirectoryNotFoundException(path); if (!slot.Entry!.IsDirectory) throw new IOException("The FATX path is not a directory."); return slot.Entry.FirstCluster; }
    private Slot? FindEntry(string p)
    {
        if (p == "/") return null; string[] parts = p[1..].Split('/'); uint directory = Metadata.RootFirstCluster;
        for (int i = 0; i < parts.Length; i++) { Slot? found = FindInDirectory(directory, parts[i]); if (found is null) return null; if (i == parts.Length - 1) return found; if (!found.Entry!.IsDirectory) return null; directory = found.Entry.FirstCluster; }
        return null;
    }
    private (uint Directory, string Name) Parent(string p) { int slash = p.LastIndexOf('/'); return (ResolveDirectory(slash == 0 ? "/" : p[..slash]), p[(slash + 1)..]); }
    private Slot? FindInDirectory(uint directory, string name) => ReadDirectory(directory).FirstOrDefault(s => !s.Deleted && !s.Empty && string.Equals(s.Entry!.Name, name, StringComparison.OrdinalIgnoreCase));
    private Slot FindFreeSlot(uint directory)
    {
        Slot? free = ReadDirectory(directory).FirstOrDefault(s => s.Deleted || s.Empty); if (free is not null) return free; List<uint> chain = GetChain(directory); uint tail = chain[^1], extension = AllocateClusters(1)[0]; ZeroCluster(extension); source.Flush(); WriteFat(extension, Last); FlushFatPage(); source.Flush(); WriteFat(tail, extension); FlushFatPage(); source.Flush(); chain.Add(extension); return new Slot(ClusterOffset(extension), null, false, true);
    }
    private IEnumerable<Slot> ReadDirectory(uint first)
    {
        if (!Data(first)) throw new FatxFormatException("A FATX directory has no valid first cluster."); int count = 0; bool terminal = false;
        List<uint> chain = GetChain(first);
        for (int clusterIndex = 0; clusterIndex < chain.Count; clusterIndex++)
        {
            uint c = chain[clusterIndex]; var b = new byte[checked((int)ClusterSize)];
            ReadAt(source, ClusterOffset(c), b, Metadata.PartitionOffset, Metadata.PartitionLength);
            for (int o = 0; o < b.Length; o += EntrySize)
            {
                byte marker = b[o]; bool deleted = marker == 0xE5, empty = marker is 0 or 0xFF;
                if (!empty && !deleted && marker > 42) throw new FatxFormatException("A FATX directory entry has an invalid name length.");
                if (!terminal) { yield return new Slot(ClusterOffset(c) + o, empty || deleted ? null : Parse(b.AsSpan(o, EntrySize)), deleted, empty); if (empty) terminal = true; }
                if (!empty && !deleted && ++count == MaximumDirectoryEntries)
                {
                    if (clusterIndex + 1 < chain.Count) throw new FatxFormatException("The directory exceeds the 4096-entry safety limit.");
                    yield break;
                }
            }
        }
    }
    private FatxDirectoryEntry Parse(ReadOnlySpan<byte> b) => new(Ascii.GetString(b.Slice(2, b[0])), b[1], U32(b.Slice(0x2C, 4), Metadata.ByteOrder), U32(b.Slice(0x30, 4), Metadata.ByteOrder), U32(b.Slice(0x34, 4), Metadata.ByteOrder), U32(b.Slice(0x38, 4), Metadata.ByteOrder), U32(b.Slice(0x3C, 4), Metadata.ByteOrder));
    private Slot ReadSlot(long position) { var b = new byte[EntrySize]; ReadAt(source, position, b, Metadata.PartitionOffset, Metadata.PartitionLength); return new Slot(position, b[0] is 0 or 0xFF or 0xE5 ? null : Parse(b), b[0] == 0xE5, b[0] is 0 or 0xFF); }
    private void WriteEntry(long at, FatxDirectoryEntry e) { ValidateName(e.Name); var b = new byte[EntrySize]; b[0] = (byte)e.Name.Length; b[1] = e.Attributes; Ascii.GetBytes(e.Name, b.AsSpan(2)); Put32(b.AsSpan(0x2C), e.FirstCluster); Put32(b.AsSpan(0x30), e.FileSize); Put32(b.AsSpan(0x34), e.CreationTimestamp); Put32(b.AsSpan(0x38), e.LastWriteTimestamp); Put32(b.AsSpan(0x3C), e.LastAccessTimestamp); WriteAt(at, b); }
    private void DeleteSlot(long at) => WriteAt(at, new byte[] { 0xE5 });
    private void ReadFileData(FatxDirectoryEntry e, long offset, Span<byte> target)
    {
        if (target.IsEmpty) return;
        if (e.FirstCluster == 0) throw new FatxFormatException("A non-empty FATX file has no cluster chain.");
        List<uint> chain = GetChain(e.FirstCluster); int done = 0;
        while (done < target.Length)
        {
            long at = offset + done; int index = checked((int)(at / ClusterSize)), inside = (int)(at % ClusterSize);
            if (index >= chain.Count) throw new FatxFormatException("A FATX file chain is shorter than its size.");
            int maximumRun = checked((int)Math.Min(chain.Count - index, (inside + (long)(target.Length - done) + ClusterSize - 1) / ClusterSize));
            int run = ContiguousRun(chain, index, maximumRun); long available = checked(run * ClusterSize - inside);
            int take = checked((int)Math.Min(target.Length - done, available));
            ReadAt(source, ClusterOffset(chain[index]) + inside, target.Slice(done, take), Metadata.PartitionOffset, Metadata.PartitionLength);
            done += take;
        }
    }
    private void WriteFileData(FatxDirectoryEntry e, long offset, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        List<uint> chain = GetChain(e.FirstCluster); int done = 0;
        while (done < data.Length)
        {
            long at = offset + done; int index = checked((int)(at / ClusterSize)), inside = (int)(at % ClusterSize);
            if (index >= chain.Count) throw new FatxFormatException("A FATX file chain is shorter than its allocation.");
            int maximumRun = checked((int)Math.Min(chain.Count - index, (inside + (long)(data.Length - done) + ClusterSize - 1) / ClusterSize));
            int run = ContiguousRun(chain, index, maximumRun); long available = checked(run * ClusterSize - inside);
            int take = checked((int)Math.Min(data.Length - done, available));
            WriteAt(ClusterOffset(chain[index]) + inside, data.Slice(done, take));
            done += take;
        }
    }
    private static int ContiguousRun(List<uint> chain, int index, int maximum)
    {
        int run = 1;
        while (run < maximum && chain[index + run] == chain[index + run - 1] + 1) run++;
        return run;
    }
    private uint[] AllocateClusters(int count)
    {
        if (count < 1) return [];
        if (cachedFreeClusterCount is long known && known < count) throw new IOException("The FATX partition is full.");
        var found = new List<uint>(count);
        uint start = allocationCursor;
        uint c = start;
        do
        {
            if (ReadFat(c) == 0) found.Add(c);
            if (found.Count == count) break;
            c = c == Metadata.ClusterCount ? 1 : c + 1;
        } while (c != start);
        if (found.Count != count) throw new IOException("The FATX partition is full.");
        return found.ToArray();
    }
    private void FreeChain(uint first) { foreach (uint c in GetChain(first)) WriteFat(c, 0); chainCache.Remove(first); }
    private List<uint> GetChain(uint first)
    {
        if (first == 0) return [];
        if (chainCache.TryGetValue(first, out List<uint>? cached)) return cached;
        var result = new List<uint>(); var seen = new HashSet<uint>(); uint c = first;
        for (long n = 0; n < Metadata.ClusterCount; n++)
        {
            if (!Data(c) || !seen.Add(c)) throw new FatxFormatException("A FATX cluster chain is invalid or cyclic.");
            result.Add(c);
            uint next = ReadFat(c);
            if (next == Last) { chainCache[first] = result; return result; }
            if (!Data(next)) throw new FatxFormatException("A FATX chain points outside the data area.");
            c = next;
        }
        throw new FatxFormatException("A FATX cluster chain exceeds the volume.");
    }
    private uint ReadFat(uint c)
    {
        int n = Metadata.AllocationTable == FatxAllocationTable.Fat16 ? 2 : 4;
        ReadOnlySpan<byte> entry = GetFatPage(Metadata.FatOffset + (long)c * n).AsSpan((int)((Metadata.FatOffset + (long)c * n) % RawAlignment), n);
        return n == 2 ? U16(entry, Metadata.ByteOrder) : U32(entry, Metadata.ByteOrder);
    }
    private void WriteFat(uint c, uint value)
    {
        int n = Metadata.AllocationTable == FatxAllocationTable.Fat16 ? 2 : 4;
        if (n == 2 && value > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
        long position = Metadata.FatOffset + (long)c * n;
        byte[] page = GetFatPage(position);
        Span<byte> entry = page.AsSpan((int)(position % RawAlignment), n);
        uint old = n == 2 ? U16(entry, Metadata.ByteOrder) : U32(entry, Metadata.ByteOrder);
        if (n == 2) Put16(entry, (ushort)value); else Put32(entry, value);
        fatPageDirty = true;
        if (cachedFreeClusterCount is long known) cachedFreeClusterCount = checked(known - (old == 0 ? 1 : 0) + (value == 0 ? 1 : 0));
        allocationCursor = c == Metadata.ClusterCount ? 1 : c + 1;
    }
    private byte[] GetFatPage(long position)
    {
        long page = position / RawAlignment * RawAlignment;
        if (fatPage is not null && fatPageOffset == page) return fatPage;
        FlushFatPage();
        var next = new byte[RawAlignment];
        ReadAt(source, page, next, Metadata.PartitionOffset, Metadata.PartitionLength);
        fatPage = next;
        fatPageOffset = page;
        return next;
    }
    private void FlushFatPage() { if (!fatPageDirty || fatPage is null) return; WriteExact(source, fatPageOffset, fatPage); fatPageDirty = false; }
    private void ZeroCluster(uint c) => WriteAt(ClusterOffset(c), new byte[checked((int)ClusterSize)]);
    private int ClustersFor(uint size) => Math.Max(1, checked((int)(((long)size + ClusterSize - 1) / ClusterSize)));
    private bool Data(uint c) => c >= 1 && c <= Metadata.ClusterCount;
    private uint Media => Metadata.AllocationTable == FatxAllocationTable.Fat16 ? 0xFFF8u : 0xFFFFFFF8u;
    private uint Last => Metadata.AllocationTable == FatxAllocationTable.Fat16 ? 0xFFFFu : 0xFFFFFFFFu;
    private long ClusterOffset(uint c) => checked(Metadata.DataOffset + ((long)c - 1) * ClusterSize);
    private void EnsureWritable() { if (!source.CanWrite) throw new UnauthorizedAccessException("This FATX volume was opened read-only."); }
    private uint Now() => EncodeTimestamp(DateTimeOffset.UtcNow, Metadata.ByteOrder);
    private static string CanonicalPath(string path) { if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A FATX path is required.", nameof(path)); string p = path.Replace('\\', '/').Trim(); if (!p.StartsWith('/')) p = "/" + p; if (p.Contains("//", StringComparison.Ordinal) || p.Split('/').Any(x => x is "." or "..")) throw new ArgumentException("The FATX path is invalid.", nameof(path)); return p.Length == 1 ? p : p.TrimEnd('/'); }
    private static void ValidateName(string name) { if (name.Length is < 1 or > 42 || name.Any(c => c > 0x7F || c < 0x20 || c is '/' or '\\' or '"' or '*' or ':' or '<' or '>' or '?' or '|')) throw new ArgumentException("FATX names must be 1–42 ASCII bytes and cannot contain separators, controls, or Xbox-invalid characters.", nameof(name)); }
    private static (FatxAllocationTable Table, long Count, long Data) Layout(long length, long cluster) { long entries = length / cluster + 1; if (entries is < 2 or > 0x0FFFFFFF) throw new FatxFormatException("The FATX allocation-table size is impossible."); FatxAllocationTable table = entries < 0xFFF0 ? FatxAllocationTable.Fat16 : FatxAllocationTable.Fat32; long data = HeaderSize + Align(entries * (table == FatxAllocationTable.Fat16 ? 2 : 4), HeaderSize); long count = (length - data) / cluster; if (data >= length || count < 1 || count > entries - 1) throw new FatxFormatException("The FATX data-cluster range is impossible."); return (table, count, data); }
    private static uint U16(ReadOnlySpan<byte> b, FatxByteOrder o) => o == FatxByteOrder.LittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(b) : BinaryPrimitives.ReadUInt16BigEndian(b);
    private static uint U32(ReadOnlySpan<byte> b, FatxByteOrder o) => o == FatxByteOrder.LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(b) : BinaryPrimitives.ReadUInt32BigEndian(b);
    private void Put16(Span<byte> b, ushort v) { if (Metadata.ByteOrder == FatxByteOrder.LittleEndian) BinaryPrimitives.WriteUInt16LittleEndian(b, v); else BinaryPrimitives.WriteUInt16BigEndian(b, v); }
    private void Put32(Span<byte> b, uint v) { if (Metadata.ByteOrder == FatxByteOrder.LittleEndian) BinaryPrimitives.WriteUInt32LittleEndian(b, v); else BinaryPrimitives.WriteUInt32BigEndian(b, v); }
    private static long Align(long value, int alignment) => checked((value + alignment - 1) / alignment * alignment);
    private static bool Within(long at, long length, long total) => at >= 0 && length >= 0 && at <= total && length <= total - at;
    private static void ReadAt(Stream s, long at, Span<byte> b, long po, long pl) => TransferRead(s, at, b, po, pl);
    private void WriteAt(long at, ReadOnlySpan<byte> b) => TransferWrite(source, at, b, Metadata.PartitionOffset, Metadata.PartitionLength);
    private static void TransferRead(Stream s, long at, Span<byte> b, long po, long pl) { ValidateTransfer(at, b.Length, po, pl); if (at % RawAlignment == 0 && b.Length % RawAlignment == 0) { ReadExact(s, at, b); return; } long aligned = at / RawAlignment * RawAlignment, end = Align(at + b.Length, RawAlignment); if (aligned >= po && end <= po + pl) { var all = new byte[checked((int)(end - aligned))]; ReadExact(s, aligned, all); all.AsSpan((int)(at - aligned), b.Length).CopyTo(b); } else ReadExact(s, at, b); }
    private static void TransferWrite(Stream s, long at, ReadOnlySpan<byte> b, long po, long pl) { ValidateTransfer(at, b.Length, po, pl); if (at % RawAlignment == 0 && b.Length % RawAlignment == 0) { WriteExact(s, at, b); return; } long aligned = at / RawAlignment * RawAlignment, end = Align(at + b.Length, RawAlignment); if (aligned >= po && end <= po + pl) { var all = new byte[checked((int)(end - aligned))]; ReadExact(s, aligned, all); b.CopyTo(all.AsSpan((int)(at - aligned))); WriteExact(s, aligned, all); } else WriteExact(s, at, b); }
    private static void ValidateTransfer(long at, int length, long po, long pl) { if (at < po || !Within(at, length, checked(po + pl))) throw new FatxFormatException("A FATX transfer would exceed the partition boundary."); }
    private static void ReadExact(Stream s, long at, Span<byte> b) { s.Position = at; int offset = 0; while (offset < b.Length) { int read = s.Read(b[offset..]); if (read == 0) throw new FatxFormatException("The FATX source ended unexpectedly."); offset += read; } }
    private static void WriteExact(Stream s, long at, ReadOnlySpan<byte> b) { s.Position = at; s.Write(b); }
    private sealed record Slot(long Position, FatxDirectoryEntry? Entry, bool Deleted, bool Empty);
}
