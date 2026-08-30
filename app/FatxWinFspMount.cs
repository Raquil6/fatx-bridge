using System.Runtime.InteropServices;
using System.Reflection;
using System.Diagnostics;
using System.Buffers;
using FileStream = System.IO.FileStream;
using IOException = System.IO.IOException;
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using Fsp;
using Fsp.Interop;
using FatxBridge.Core;

namespace FatxBridge.Windows;

/// <summary>Owns the real WinFsp host and its raw-device stream for one mounted FATX partition.</summary>
public sealed class FatxWinFspMount : IFatxMountSession
{
    private readonly FileStream stream;
    private readonly FileSystemHost host;
    private bool disposed;
    private FatxWinFspMount(FileStream stream, FileSystemHost host, string mountPath)
    {
        this.stream = stream; this.host = host; MountPath = mountPath;
    }
    public string MountPath { get; }
    public bool IsReadOnly { get; private set; }
    public void OpenExplorer() => Process.Start(new ProcessStartInfo
    {
        FileName = "explorer.exe",
        Arguments = $"\"{MountPath}\"",
        UseShellExecute = true,
    });
    public static FatxWinFspMount Mount(string physicalPath, long capacity, FatxPartitionCandidate partition, bool readOnly)
    {
        FileStream? stream = null;
        try
        {
            stream = readOnly ? DriveScanner.OpenReadOnly(physicalPath) : DriveScanner.OpenReadWrite(physicalPath);
            return MountOpened(stream, capacity, partition, readOnly, useDriveLetter: false);
        }
        catch { stream?.Dispose(); throw; }
    }

    internal static FatxWinFspMount MountOpened(FileStream stream, long capacity, FatxPartitionCandidate partition,
        bool readOnly, bool useDriveLetter, string? volumeLabel = null)
    {
        FileSystemHost? host = null;
        try
        {
            var volume = FatxVolume.Open(stream, partition.Offset, partition.Length, capacity,
                partition.Metadata.SectorSize);
            var provider = new FatxWinFspFileSystem(volume, readOnly, VolumeLabelStore.Normalize(volumeLabel ?? $"FATX {partition.Name}"));
            host = new FileSystemHost(provider)
            {
                SectorSize = checked((ushort)volume.Metadata.SectorSize),
                SectorsPerAllocationUnit = checked((ushort)volume.Metadata.SectorsPerCluster),
                MaxComponentLength = 42,
                VolumeSerialNumber = volume.Metadata.SerialNumber,
                FileSystemName = volume.Metadata.AllocationTable == FatxAllocationTable.Fat16 ? "FATX16" : "FATX32",
                CaseSensitiveSearch = false,
                CasePreservedNames = true,
                UnicodeOnDisk = false,
            };
            // Initialize WinFsp's native binding under the elevated process token.
            // Only the following drive-letter registration needs the normal Explorer
            // token; initializing the binding while impersonating can fail to locate
            // the installed runtime.
            _ = FileSystemHost.Version();
            string mountPath;
            string? mountArgument;
            if (useDriveLetter)
            {
                mountPath = string.Empty;
                mountArgument = null;
            }
            else
            {
                string mountRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FatxBridge", "Mounts");
                Directory.CreateDirectory(mountRoot);
                mountPath = Path.Combine(mountRoot, $"FATX-{volume.Metadata.SerialNumber:X8}-{Guid.NewGuid():N}");
                mountArgument = @"\\.\" + mountPath;
            }
            // A directory mount is global to the filesystem. A drive letter is
            // instead scoped to the elevated process' UAC logon token and is not
            // visible to the normal Explorer process.
            // WinFsp directory mounts use the Mount Manager path form. A normal
            // Win32 path is interpreted as a device name and fails before callbacks.
            bool diagnosticLogging = string.Equals(Environment.GetEnvironmentVariable("FATXBRIDGE_WINFSP_DEBUG"), "1", StringComparison.Ordinal);
            if (diagnosticLogging)
                _ = FileSystemHost.SetDebugLogFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatxBridge", "winfsp-debug.log"));
            int mountStatus = host.Mount(mountArgument, null, false, diagnosticLogging ? uint.MaxValue : 0);
            if (mountStatus < 0)
                throw new IOException($"WinFsp could not mount {(useDriveLetter ? "a drive letter" : $"at {mountPath}")} (NTSTATUS 0x{mountStatus:X8}). Ensure the signed WinFsp runtime is installed.");

            string? publishedPath = host.MountPoint();
            if (string.IsNullOrWhiteSpace(publishedPath))
            {
                throw new IOException(
                    "WinFsp mounted the FATX volume but did not publish its folder mount point.");
            }

            string publishedMountPath = useDriveLetter
                ? publishedPath.EndsWith(':') ? publishedPath + @"\" : publishedPath
                : mountPath;
            return new FatxWinFspMount(stream, host, publishedMountPath)
            {
                IsReadOnly = readOnly
            };
        }
        catch { host?.Dispose(); stream.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { host.Unmount(); }
        finally { host.Dispose(); stream.Dispose(); }
    }
}

/// <summary>WinFsp callbacks over the FATX core. The core serializes all metadata mutations.</summary>
#pragma warning disable CA1725 // WinFsp dispatches callback arguments by its documented signature and parameter names are not semantic.
public sealed class FatxWinFspFileSystem(FatxVolume volume, bool readOnly, string initialVolumeLabel) : FileSystemBase
{
    private const int Success = 0, NotFound = unchecked((int)0xC0000034), Exists = unchecked((int)0xC0000035), AccessDenied = unchecked((int)0xC0000022), InvalidParameter = unchecked((int)0xC000000D), NotDirectory = unchecked((int)0xC0000103), DirectoryNotEmpty = unchecked((int)0xC0000101), DiskFull = unchecked((int)0xC000007F), WriteProtected = unchecked((int)0xC00000A2);
    private const byte FatxReadOnlyAttribute = 0x01;
    private const byte FatxHiddenAttribute = 0x02;
    private const byte FatxSystemAttribute = 0x04;
    private const byte FatxDirectoryAttribute = 0x10;
    private const byte FatxArchiveAttribute = 0x20;
    private const uint WindowsReadOnlyAttribute = 0x01;
    private const uint WindowsHiddenAttribute = 0x02;
    private const uint WindowsSystemAttribute = 0x04;
    private const uint WindowsDirectoryAttribute = 0x10;
    private const uint WindowsArchiveAttribute = 0x20;
    private const uint WindowsNormalAttribute = 0x80;
    private string volumeLabel = initialVolumeLabel;

    public override int Init(object hostObject)
    {
        if (readOnly && hostObject is FileSystemHost host)
        {
            // winfsp.net 2.1 exposes this volume flag only through its private
            // parameters structure. The driver uses it to route FILE_OPEN requests
            // correctly on a read-only filesystem.
            FieldInfo? parametersField = host.GetType().GetField("_VolumeParams", BindingFlags.NonPublic | BindingFlags.Instance);
            object? parameters = parametersField?.GetValue(host);
            FieldInfo? flagsField = parameters?.GetType().GetField("Flags", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            if (parameters is not null && flagsField is not null && flagsField.GetValue(parameters) is uint flags)
            {
                flagsField.SetValue(parameters, flags | 0x200u); // ReadOnlyVolume
                parametersField!.SetValue(host, parameters);
            }
        }
        return Success;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        try
        {
            Node node = Node.From(volume, ToFatxPath(fileName));
            fileAttributes = MapFatxAttributes(node.Entry.Attributes, readOnly);
            return Success;
        }
        catch (Exception exception)
        {
            fileAttributes = 0;
            return Status(exception);
        }
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        // This callback is part of Windows' first probe after mounting. Do not scan
        // a whole physical FATX allocation table here: on a USB HDD adapter that can
        // block the dispatcher long enough for Explorer to reject the drive.
        volumeInfo = new VolumeInfo { TotalSize = (ulong)volume.TotalSize, FreeSize = (ulong)volume.ReportedFreeSpace };
        volumeInfo.SetVolumeLabel(volumeLabel);
        return Success;
    }
    public override int SetVolumeLabel(string VolumeLabel, out VolumeInfo volumeInfo)
    {
        try
        {
            volumeLabel = VolumeLabelStore.Normalize(VolumeLabel);
            return GetVolumeInfo(out volumeInfo);
        }
        catch
        {
            volumeInfo = default;
            return InvalidParameter;
        }
    }
    public override int Open(string fileName, uint createOptions, uint grantedAccess, out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        Trace($"Open {fileName}");
        try { var node = Node.From(volume, ToFatxPath(fileName)); fileNode = node; fileDesc = node.IsDirectory ? new Cursor(node) : node; fileInfo = Info(node); normalizedName = fileName; Trace($"Open {fileName} => success"); return Success; }
        catch (Exception e) { fileNode = fileDesc = null!; fileInfo = default; normalizedName = null!; int status = Status(e); Trace($"Open {fileName} => 0x{status:X8}: {e}"); return status; }
    }
    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor, ulong allocationSize, out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
    {
        Trace($"Create {fileName}, options=0x{createOptions:X}, attributes=0x{fileAttributes:X}, allocation={allocationSize}");
        if (readOnly) { fileNode = fileDesc = null!; fileInfo = default; normalizedName = null!; return WriteProtected; }
        try
        {
            string path = ToFatxPath(fileName); bool directory = (fileAttributes & WindowsDirectoryAttribute) != 0;
            if (directory) volume.CreateDirectory(path); else { volume.CreateFile(path); if (allocationSize > 0) volume.Preallocate(path, checked((long)allocationSize)); }
            var node = Node.From(volume, path); fileNode = node; fileDesc = node.IsDirectory ? new Cursor(node) : node; fileInfo = Info(node); normalizedName = fileName; return Success;
        }
        catch (Exception e) { fileNode = fileDesc = null!; fileInfo = default; normalizedName = null!; return Status(e); }
    }
    public override int CreateEx(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes,
        byte[] securityDescriptor, ulong allocationSize, IntPtr extraBuffer, uint extraLength, bool extraBufferIsReparsePoint,
        out object fileNode, out object fileDesc, out FileInfo fileInfo, out string normalizedName)
        => Create(fileName, createOptions, grantedAccess, fileAttributes, securityDescriptor, allocationSize,
            out fileNode, out fileDesc, out fileInfo, out normalizedName);
    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        bytesTransferred = 0;
        try
        {
            var node = (Node)fileNode; if (node.IsDirectory) return InvalidParameter;
            lock (node.Gate)
            {
                FlushNodeWriteBuffer(node);
                DrainNodeWrites(node);
                long at = checked((long)offset);
                int count = checked((int)Math.Min(length, (ulong)Math.Max(0, node.EffectiveFileSize - at)));
                byte[] data = new byte[count];
                int initialized = checked((int)Math.Min(count, Math.Max(0, (long)node.Entry.FileSize - at)));
                if (initialized > 0) volume.ReadFile(node.Entry, at, data.AsSpan(0, initialized));
                Marshal.Copy(data, 0, buffer, data.Length); bytesTransferred = (uint)data.Length; return Success;
            }
        }
        catch (Exception e) { return Status(e); }
    }
    public override int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out FileInfo fileInfo)
    {
        bytesTransferred = 0; fileInfo = default; if (readOnly) return WriteProtected;
        byte[]? data = null; Node? telemetryNode = null; long callbackStarted = Stopwatch.GetTimestamp();
        try
        {
            var node = (Node)fileNode; telemetryNode = node; if (node.IsDirectory) return InvalidParameter;
            lock (node.Gate)
            {
                int count = checked((int)length);
                long at = writeToEndOfFile ? node.InitializedFileSize : checked((long)offset);
                if (node.CanBufferWrite(at, count))
                {
                    if (!node.TryBufferWrite(buffer, at, count))
                    {
                        FlushNodeWriteBuffer(node);
                        DrainNodeWritesIfQueueFull(node);
                        if (!node.TryBufferWrite(buffer, at, count)) throw new IOException("FATX Bridge could not coalesce a sequential FATX write.");
                    }
                    if (node.BufferedWriteLength == Node.WriteBufferCapacity)
                    {
                        FlushNodeWriteBuffer(node);
                        DrainNodeWritesIfQueueFull(node);
                    }
                }
                else
                {
                    FlushNodeWriteBuffer(node);
                    DrainNodeWrites(node);
                    data = ArrayPool<byte>.Shared.Rent(count);
                    Marshal.Copy(buffer, data, 0, count);
                    long deviceStarted = Stopwatch.GetTimestamp();
                    node.ReplaceEntry(volume.WriteFile(node.Entry, at, data.AsSpan(0, count)));
                    node.RecordDeviceWrite(count, Stopwatch.GetTimestamp() - deviceStarted, buffered: false);
                    node.MarkDirty();
                }
                fileInfo = Info(node); bytesTransferred = length; return Success;
            }
        }
        catch (Exception e) { return Status(e); }
        finally
        {
            if (data is not null) ArrayPool<byte>.Shared.Return(data);
            telemetryNode?.RecordWriteCallback(length, Stopwatch.GetTimestamp() - callbackStarted);
        }
    }
    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo) { try { var node = (Node)fileNode; lock (node.Gate) { if (!node.HasUncommittedChanges) node.Replace(Node.From(volume, node.Path)); fileInfo = Info(node); return Success; } } catch (Exception e) { fileInfo = default; return Status(e); } }
    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        fileInfo = default; if (readOnly) return WriteProtected;
        try
        {
            var node = (Node)fileNode; long size = checked((long)newSize);
            lock (node.Gate)
            {
                Trace($"SetFileSize {node.Path}, size={newSize}, allocationOnly={setAllocationSize}");
                FlushNodeWriteBuffer(node);
                DrainNodeWrites(node);
                if (setAllocationSize)
                {
                    long allocationStarted = Stopwatch.GetTimestamp();
                    _ = volume.Preallocate(node.Path, size);
                    node.RecordAllocation(Stopwatch.GetTimestamp() - allocationStarted);
                }
                else if (size > node.Entry.FileSize)
                {
                    long allocationStarted = Stopwatch.GetTimestamp();
                    _ = volume.Preallocate(node.Path, size);
                    node.RecordAllocation(Stopwatch.GetTimestamp() - allocationStarted);
                    node.SetPendingFileSize(checked((uint)size));
                }
                else
                {
                    CommitNode(node);
                    node.ClearPendingFileSize();
                    node.ResetEntry(volume.SetLength(node.Path, size));
                }
                fileInfo = Info(node); return Success;
            }
        }
        catch (Exception e) { return Status(e); }
    }
    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        try
        {
            Node? node = fileNode as Node;
            if (node is not null) { CommitNode(node); fileInfo = Info(node); } else fileInfo = default;
            long flushStarted = Stopwatch.GetTimestamp(); volume.Flush();
            node?.RecordDurabilityFlush(Stopwatch.GetTimestamp() - flushStarted);
            return Success;
        }
        catch (Exception e) { fileInfo = default; return Status(e); }
    }
    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        if (readOnly) return WriteProtected;
        try { var node = (Node)fileNode; lock (node.Gate) { CommitNode(node); string destination = ToFatxPath(newFileName); if (volume.TryGetEntry(destination, out _)) return Exists; volume.Move(node.Path, destination); node.Path = destination; node.Replace(Node.From(volume, destination)); return Success; } } catch (Exception e) { return Status(e); }
    }
    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        if (readOnly) return WriteProtected;
        try { var node = (Node)fileNode; if (node.IsDirectory && volume.EnumerateDirectory(node.Path).Count != 0) return DirectoryNotEmpty; return Success; } catch (Exception e) { return Status(e); }
    }
    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        if (readOnly) return;
        try
        {
            // CleanupDelete is authoritative. SetDelete is not called for handles opened
            // with FILE_DELETE_ON_CLOSE, so requiring a second private flag makes Explorer
            // report success while leaving the FATX directory entry behind.
            if ((flags & CleanupDelete) != 0 && fileNode is Node node)
            { lock (node.Gate) { node.DiscardWriteBuffer(); DrainNodeWrites(node); if (node.IsDirectory) volume.DeleteDirectory(node.Path); else volume.DeleteFile(node.Path); } }
            else if (fileNode is Node liveNode) lock (liveNode.Gate) FinalizePendingFileSize(liveNode);
            long flushStarted = Stopwatch.GetTimestamp(); volume.Flush();
            if (fileNode is Node cleanupNode)
            {
                cleanupNode.RecordDurabilityFlush(Stopwatch.GetTimestamp() - flushStarted);
                WritePerformanceLog(cleanupNode);
            }
        }
        catch { /* WinFsp has already received the cleanup disposition. */ }
    }
    public override void Close(object fileNode, object fileDesc)
    {
        if (readOnly) return;
        try
        {
            Node? node = fileNode as Node;
            if (node is not null) lock (node.Gate) FinalizePendingFileSize(node);
            long flushStarted = Stopwatch.GetTimestamp(); volume.Flush();
            node?.RecordDurabilityFlush(Stopwatch.GetTimestamp() - flushStarted);
        }
        catch { }
        finally
        {
            if (fileNode is Node node)
            {
                WritePerformanceLog(node);
                lock (node.Gate) node.ReleaseWriteBuffer();
            }
        }
    }
    public override int SetDelete(object fileNode, object fileDesc, string fileName, bool deleteFile)
    {
        if (readOnly) return WriteProtected;
        try
        {
            if (!deleteFile) return Success;
            var node = (Node)fileNode;
            if (node.IsDirectory && volume.EnumerateDirectory(node.Path).Count != 0) return DirectoryNotEmpty;
            return Success;
        }
        catch (Exception e) { return Status(e); }
    }
    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker, ref object context, out string fileName, out FileInfo fileInfo)
    {
        var cursor = fileDesc as Cursor ?? new Cursor((Node)fileNode); context = cursor; fileName = null!; fileInfo = default;
        if (cursor.Next >= cursor.Entries.Count) return false;
        Node child = cursor.Entries[cursor.Next++]; fileName = child.Name; fileInfo = Info(child); return true;
    }
    private void FinalizePendingFileSize(Node node)
    {
        CommitNode(node);
        if (node.PendingFileSize is not uint pending || pending <= node.Entry.FileSize) return;
        Trace($"FinalizeFileSize {node.Path}, initialized={node.Entry.FileSize}, requested={pending}");
        node.ReplaceEntry(volume.SetLength(node.Path, pending));
    }
    private void CommitNode(Node node)
    {
        lock (node.Gate)
        {
            FlushNodeWriteBuffer(node);
            DrainNodeWrites(node);
            if (!node.Dirty || node.IsDirectory) return;
            long commitStarted = Stopwatch.GetTimestamp();
            volume.CommitFile(node.Path, node.Entry);
            node.RecordMetadataCommit(Stopwatch.GetTimestamp() - commitStarted);
            node.ClearDirty();
        }
    }
    private void FlushNodeWriteBuffer(Node node)
    {
        if (node.BufferedWriteLength == 0) return;
        Node.BufferedWrite work = node.DetachBufferedWrite();
        Task<FatxDirectoryEntry> previous = node.PendingDeviceWrite ?? Task.FromResult(node.Entry);
        Task<FatxDirectoryEntry> pending = Task.Run(async () =>
        {
            try
            {
                FatxDirectoryEntry entry = await previous.ConfigureAwait(false);
                long deviceStarted = Stopwatch.GetTimestamp();
                try { return volume.WriteFile(entry, work.Offset, work.Buffer.AsSpan(0, work.Length)); }
                finally { node.RecordDeviceWrite(work.Length, Stopwatch.GetTimestamp() - deviceStarted, buffered: true); }
            }
            finally { ArrayPool<byte>.Shared.Return(work.Buffer); }
        });
        node.SetPendingDeviceWrite(pending, work.Length);
    }
    private static void DrainNodeWritesIfQueueFull(Node node)
    {
        if (node.PendingDeviceBytes >= Node.MaximumPendingDeviceBytes) DrainNodeWrites(node);
    }
    private static void DrainNodeWrites(Node node)
    {
        Task<FatxDirectoryEntry>? pending = node.PendingDeviceWrite;
        if (pending is null) return;
        try
        {
            node.ReplaceEntry(pending.GetAwaiter().GetResult());
            node.MarkDirty();
        }
        finally { node.ClearPendingDeviceWrite(); }
    }
    private static void WritePerformanceLog(Node node)
    {
        if (Environment.GetEnvironmentVariable("FATXBRIDGE_PERFORMANCE_LOG") != "1") return;
        Node.PerformanceSnapshot? performance = node.TakePerformanceSnapshot();
        if (performance is null) return;
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatxBridge");
            Directory.CreateDirectory(directory);
            double wallSeconds = Math.Max(performance.WallMilliseconds / 1000d, 0.000001);
            double mib = performance.Bytes / 1048576d;
            string report = $"{DateTimeOffset.Now:O} {node.Path}{Environment.NewLine}" +
                $"Bytes: {performance.Bytes}; wall: {performance.WallMilliseconds:F1} ms; observed: {mib / wallSeconds:F1} MiB/s{Environment.NewLine}" +
                $"Callbacks: {performance.Callbacks}; average: {performance.Bytes / Math.Max(1d, performance.Callbacks) / 1024d:F1} KiB; callback CPU/wait: {performance.CallbackMilliseconds:F1} ms{Environment.NewLine}" +
                $"Device writes: {performance.DeviceWrites}; average: {performance.DeviceBytes / Math.Max(1d, performance.DeviceWrites) / 1048576d:F2} MiB; device time: {performance.DeviceMilliseconds:F1} ms; buffered flushes: {performance.BufferedFlushes}{Environment.NewLine}" +
                $"Allocation: {performance.AllocationMilliseconds:F1} ms; metadata commit: {performance.CommitMilliseconds:F1} ms; durability flush: {performance.FlushMilliseconds:F1} ms{Environment.NewLine}{Environment.NewLine}";
            System.IO.File.AppendAllText(Path.Combine(directory, "write-performance.log"), report);
        }
        catch { }
    }
    private FileInfo Info(Node n) => new()
    {
        FileAttributes = MapFatxAttributes(n.Entry.Attributes, readOnly),
        FileSize = n.IsDirectory ? 0U : checked((ulong)n.EffectiveFileSize),
        AllocationSize = n.IsDirectory ? 0U : (ulong)((n.EffectiveFileSize + n.Volume.ClusterSize - 1) / n.Volume.ClusterSize * n.Volume.ClusterSize),
        CreationTime = ToFileTime(n.Entry.CreationTimestamp, n.Volume.Metadata.ByteOrder), LastWriteTime = ToFileTime(n.Entry.LastWriteTimestamp, n.Volume.Metadata.ByteOrder), LastAccessTime = ToFileTime(n.Entry.LastAccessTimestamp, n.Volume.Metadata.ByteOrder), ChangeTime = ToFileTime(n.Entry.LastWriteTimestamp, n.Volume.Metadata.ByteOrder),
    };
    internal static uint MapFatxAttributes(byte fatxAttributes, bool mountReadOnly)
    {
        uint windowsAttributes = 0;
        if ((fatxAttributes & FatxReadOnlyAttribute) != 0) windowsAttributes |= WindowsReadOnlyAttribute;
        if ((fatxAttributes & FatxHiddenAttribute) != 0) windowsAttributes |= WindowsHiddenAttribute;
        if ((fatxAttributes & FatxSystemAttribute) != 0) windowsAttributes |= WindowsSystemAttribute;
        bool directory = (fatxAttributes & FatxDirectoryAttribute) != 0;
        if (directory) windowsAttributes |= WindowsDirectoryAttribute;
        if ((fatxAttributes & FatxArchiveAttribute) != 0) windowsAttributes |= WindowsArchiveAttribute;
        if (mountReadOnly) windowsAttributes |= WindowsReadOnlyAttribute;
        if (!directory && windowsAttributes == 0) windowsAttributes = WindowsNormalAttribute;
        return windowsAttributes;
    }
    private static ulong ToFileTime(uint timestamp, FatxByteOrder byteOrder) => FatxVolume.DecodeTimestamp(timestamp, byteOrder) is DateTimeOffset time
        ? unchecked((ulong)time.UtcDateTime.ToFileTimeUtc()) : 0;
    private static string ToFatxPath(string value) => string.IsNullOrEmpty(value) || value == "\\" ? "/" : "/" + value.Trim('\\').Replace('\\', '/');
    private static int Status(Exception e) => e switch { UnauthorizedAccessException => AccessDenied, System.IO.FileNotFoundException or System.IO.DirectoryNotFoundException => NotFound, ArgumentException or ArgumentOutOfRangeException => InvalidParameter, IOException x when x.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase) => Exists, IOException x when x.Message.Contains("full", StringComparison.OrdinalIgnoreCase) => DiskFull, IOException x when x.Message.Contains("non-empty", StringComparison.OrdinalIgnoreCase) => DirectoryNotEmpty, IOException x when x.Message.Contains("not a directory", StringComparison.OrdinalIgnoreCase) => NotDirectory, _ => AccessDenied };
    private static void Trace(string message)
    {
        if (Environment.GetEnvironmentVariable("FATXBRIDGE_WINFSP_DEBUG") != "1") return;
        try { System.IO.File.AppendAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatxBridge", "fatx-callback.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch { }
    }
    private sealed class Node
    {
        public const int WriteBufferCapacity = 4 * 1024 * 1024;
        public const int MaximumPendingDeviceBytes = 16 * 1024 * 1024;
        private byte[]? writeBuffer;
        private int bufferedWriteLength;
        private long bufferedWriteOffset;
        private long acceptedFileSize;
        private Task<FatxDirectoryEntry>? pendingDeviceWrite;
        private long pendingDeviceBytes;
        private long performanceStarted;
        private long performanceBytes;
        private long performanceCallbacks;
        private long performanceCallbackTicks;
        private long performanceDeviceBytes;
        private long performanceDeviceWrites;
        private long performanceDeviceTicks;
        private long performanceBufferedFlushes;
        private long performanceAllocationTicks;
        private long performanceCommitTicks;
        private long performanceFlushTicks;
        private bool performanceReported;
        public required FatxVolume Volume { get; init; }
        public required string Path { get; set; }
        public object Gate { get; } = new();
        public FatxDirectoryEntry Entry { get; private set; } = null!;
        public uint? PendingFileSize { get; private set; }
        public long EffectiveFileSize => Math.Max(Entry.FileSize, PendingFileSize ?? 0U);
        public long InitializedFileSize => Math.Max(Entry.FileSize, acceptedFileSize);
        public int BufferedWriteLength => bufferedWriteLength;
        public long BufferedWriteOffset => bufferedWriteOffset;
        public Task<FatxDirectoryEntry>? PendingDeviceWrite => pendingDeviceWrite;
        public long PendingDeviceBytes => pendingDeviceBytes;
        public string Name => Path == "/" ? "\\" : Path[(Path.LastIndexOf('/') + 1)..];
        public bool IsDirectory => Path == "/" || Entry.IsDirectory;
        public bool Dirty { get; private set; }
        public bool HasUncommittedChanges => Dirty || bufferedWriteLength != 0 || pendingDeviceWrite is not null;
        public static Node From(FatxVolume volume, string path) => path == "/" ? new Node { Volume = volume, Path = path, Entry = new FatxDirectoryEntry("\\", 0x10, volume.Metadata.RootFirstCluster, 0, 0, 0, 0) } : new Node { Volume = volume, Path = path, Entry = volume.GetEntry(path).Entry };
        public void Replace(Node other) { ResetEntry(other.Entry); Dirty = false; }
        public void ReplaceEntry(FatxDirectoryEntry entry) { Entry = entry; acceptedFileSize = Math.Max(acceptedFileSize, entry.FileSize); if (PendingFileSize <= entry.FileSize) PendingFileSize = null; }
        public void ResetEntry(FatxDirectoryEntry entry) { Entry = entry; acceptedFileSize = entry.FileSize; if (PendingFileSize <= entry.FileSize) PendingFileSize = null; }
        public void SetPendingFileSize(uint size) => PendingFileSize = size > Entry.FileSize ? size : null;
        public void ClearPendingFileSize() => PendingFileSize = null;
        public void MarkDirty() => Dirty = true;
        public void ClearDirty() => Dirty = false;
        public bool CanBufferWrite(long offset, int count) => count <= WriteBufferCapacity && PendingFileSize is uint pending && offset >= 0 && count >= 0 && offset <= pending - (long)count;
        public bool TryBufferWrite(IntPtr source, long offset, int count)
        {
            if (bufferedWriteLength != 0 && offset != bufferedWriteOffset + bufferedWriteLength) return false;
            if (count > WriteBufferCapacity - bufferedWriteLength) return false;
            writeBuffer ??= ArrayPool<byte>.Shared.Rent(WriteBufferCapacity);
            if (bufferedWriteLength == 0) bufferedWriteOffset = offset;
            Marshal.Copy(source, writeBuffer, bufferedWriteLength, count);
            bufferedWriteLength += count;
            acceptedFileSize = Math.Max(acceptedFileSize, checked(offset + count));
            return true;
        }
        public BufferedWrite DetachBufferedWrite()
        {
            if (writeBuffer is null || bufferedWriteLength == 0) throw new InvalidOperationException("There is no buffered FATX write to detach.");
            var result = new BufferedWrite(writeBuffer, bufferedWriteOffset, bufferedWriteLength);
            writeBuffer = null; bufferedWriteLength = 0; bufferedWriteOffset = 0;
            return result;
        }
        public void SetPendingDeviceWrite(Task<FatxDirectoryEntry> task, int bytes)
        {
            pendingDeviceWrite = task; pendingDeviceBytes = checked(pendingDeviceBytes + bytes);
        }
        public void ClearPendingDeviceWrite() { pendingDeviceWrite = null; pendingDeviceBytes = 0; }
        public void ClearBufferedWrite() { bufferedWriteLength = 0; bufferedWriteOffset = 0; }
        public void DiscardWriteBuffer() { ClearBufferedWrite(); ReleaseWriteBuffer(); }
        public void ReleaseWriteBuffer()
        {
            if (writeBuffer is null) return;
            ArrayPool<byte>.Shared.Return(writeBuffer);
            writeBuffer = null;
        }
        public void RecordWriteCallback(uint bytes, long ticks)
        {
            lock (Gate)
            {
                if (performanceStarted == 0) performanceStarted = Stopwatch.GetTimestamp();
                performanceBytes += bytes; performanceCallbacks++; performanceCallbackTicks += ticks;
            }
        }
        public void RecordDeviceWrite(int bytes, long ticks, bool buffered)
        {
            Interlocked.Add(ref performanceDeviceBytes, bytes); Interlocked.Increment(ref performanceDeviceWrites); Interlocked.Add(ref performanceDeviceTicks, ticks);
            if (buffered) Interlocked.Increment(ref performanceBufferedFlushes);
        }
        public void RecordAllocation(long ticks) => performanceAllocationTicks += ticks;
        public void RecordMetadataCommit(long ticks) => performanceCommitTicks += ticks;
        public void RecordDurabilityFlush(long ticks) { lock (Gate) performanceFlushTicks += ticks; }
        public PerformanceSnapshot? TakePerformanceSnapshot()
        {
            lock (Gate)
            {
                if (performanceReported || performanceBytes < 32L * 1024 * 1024 || performanceStarted == 0) return null;
                performanceReported = true;
                long wallTicks = Stopwatch.GetTimestamp() - performanceStarted;
                return new PerformanceSnapshot(performanceBytes, performanceCallbacks, performanceCallbackTicks,
                    performanceDeviceBytes, performanceDeviceWrites, performanceDeviceTicks, performanceBufferedFlushes,
                    performanceAllocationTicks, performanceCommitTicks, performanceFlushTicks, wallTicks);
            }
        }
        public sealed record PerformanceSnapshot(long Bytes, long Callbacks, long CallbackTicks, long DeviceBytes,
            long DeviceWrites, long DeviceTicks, long BufferedFlushes, long AllocationTicks, long CommitTicks,
            long FlushTicks, long WallTicks)
        {
            public double CallbackMilliseconds => Milliseconds(CallbackTicks);
            public double DeviceMilliseconds => Milliseconds(DeviceTicks);
            public double AllocationMilliseconds => Milliseconds(AllocationTicks);
            public double CommitMilliseconds => Milliseconds(CommitTicks);
            public double FlushMilliseconds => Milliseconds(FlushTicks);
            public double WallMilliseconds => Milliseconds(WallTicks);
            private static double Milliseconds(long ticks) => ticks * 1000d / Stopwatch.Frequency;
        }
        public sealed record BufferedWrite(byte[] Buffer, long Offset, int Length);
    }
    private sealed class Cursor
    {
        public Cursor(Node node) { Entries = node.Volume.EnumerateDirectory(node.Path).Select(e => Node.From(node.Volume, node.Path == "/" ? "/" + e.Name : node.Path + "/" + e.Name)).ToList(); }
        public List<Node> Entries { get; }
        public int Next { get; set; }
    }
}
#pragma warning restore CA1725
