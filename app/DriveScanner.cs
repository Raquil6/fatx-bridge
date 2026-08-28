using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FatxBridge.Core;

namespace FatxBridge.Windows;

public sealed record DriveScanItem(string DisplayName, string Path, long Capacity, IReadOnlyList<FatxPartitionCandidate> Partitions)
{
    public string Details => $"{FormatCapacity(Capacity)}  •  {Path}  •  {Partitions.Count} validated FATX partition(s)";
    private static string FormatCapacity(long bytes) => $"{bytes / 1024d / 1024d / 1024d:0.##} GB";
}

public sealed record DriveScanResult(IReadOnlyList<DriveScanItem> Drives, IReadOnlyList<string> Diagnostics);

public static class DriveScanner
{
    private const int MaximumPhysicalDriveNumber = 31;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    // IOCTL_DISK_GET_LENGTH_INFO is a read-only metadata query. Unlike FileStream.Length,
    // it is supported by raw disks exposed through many USB/SATA bridges.
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlStorageReadCapacity = 0x002D5140;
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatxBridge", "drive-scan.log");

    public static DriveScanResult Scan()
    {
        using ScanLog log = ScanLog.Create(LogPath);
        log.Write($"FATX Bridge 0.2.0 Beta scan started at {DateTimeOffset.Now:O}.");
        log.Write($"Process: {Environment.ProcessPath}");
        log.Write($"64-bit process: {Environment.Is64BitProcess}; OS: {Environment.OSVersion}");
        var drives = new List<DriveScanItem>();
        var diagnostics = new List<string>();
        for (int number = 0; number <= MaximumPhysicalDriveNumber; number++)
        {
            string path = $@"\\.\PhysicalDrive{number}";
            string stage = "opening its read-only raw handle";
            try
            {
                log.Write($"{path}: opening with native CreateFileW(GENERIC_READ, FILE_SHARE_READ|FILE_SHARE_WRITE).");
                using FileStream stream = OpenReadOnly(path);
                log.Write($"{path}: raw handle opened successfully.");
                stage = "querying its capacity";
                long capacity = GetPhysicalDriveLength(stream, log.Write);
                log.Write($"{path}: capacity is {capacity} bytes (0x{capacity:X}).");
                stage = "validating its Xbox 360 FATX partitions";
                IReadOnlyList<FatxPartitionCandidate> partitions = FatxPartitionProbe.DetectStandardRetailPartitions(
                    stream, capacity, message => log.Write($"{path}: {message}"));
                if (partitions.Count > 0)
                {
                    log.Write($"{path}: detected {partitions.Count} validated retail FATX partition(s).");
                    drives.Add(new DriveScanItem($"Xbox storage on PhysicalDrive{number}", path, capacity, partitions));
                }
                else
                {
                    log.Write($"{path}: no validated retail FATX partitions.");
                }
            }
            catch (UnauthorizedAccessException)
            {
                log.Write($"{path}: ACCESS DENIED while {stage}.");
                diagnostics.Add($"Access denied for {path}. Start FATX Bridge as administrator.");
            }
            catch (IOException exception) when (IsUnavailableDevice(exception))
            {
                log.Write($"{path}: unavailable while {stage}: {exception.GetType().Name}: {exception.Message}");
                // Empty card-reader slots and unsupported pseudo-devices commonly return
                // INVALID_PARAMETER, NOT_READY, or NO_MEDIA_IN_DRIVE. They are not disks.
            }
            catch (IOException exception)
            {
                log.Write($"{path}: FAILED while {stage}: {exception.GetType().Name}: {exception.Message}");
                diagnostics.Add($"Could not inspect {path} while {stage}: {exception.Message}");
            }
            catch (NotSupportedException exception)
            {
                log.Write($"{path}: NOT SUPPORTED while {stage}: {exception.Message}");
                diagnostics.Add($"Could not inspect {path}: {exception.Message}");
            }
        }
        log.Write($"Scan complete: {drives.Count} Xbox drive(s), {diagnostics.Count} diagnostic message(s).");
        return new DriveScanResult(drives, diagnostics);
    }

    public static FileStream OpenReadOnly(string path)
    {
        SafeFileHandle handle = CreateFile(path, GenericRead, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == 5)
                throw new UnauthorizedAccessException($"Access denied for {path}.");
            throw new NativeDeviceIOException(error, $"Windows could not open {path}: {new Win32Exception(error).Message}");
        }

        try
        {
            return new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Opens a physical device for an explicitly-confirmed experimental write mount.
    /// Sharing read access lets Windows service a mounted volume but prevents another writer.</summary>
    public static FileStream OpenReadWrite(string path)
    {
        SafeFileHandle handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead,
            IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == 5) throw new UnauthorizedAccessException($"Access denied for {path}.");
            throw new NativeDeviceIOException(error, $"Windows could not open {path} for exclusive write ownership: {new Win32Exception(error).Message}");
        }
        try { return new FileStream(handle, FileAccess.ReadWrite, bufferSize: 4096, isAsync: false); }
        catch { handle.Dispose(); throw; }
    }

    private static long GetPhysicalDriveLength(FileStream stream, Action<string>? diagnostic = null)
    {
        if (DeviceIoControlLength(stream.SafeFileHandle, IoctlDiskGetLengthInfo, IntPtr.Zero, 0,
                out GetLengthInformation lengthInformation, (uint)Marshal.SizeOf<GetLengthInformation>(),
                out _, IntPtr.Zero) && lengthInformation.Length > 0)
        {
            diagnostic?.Invoke("Capacity query succeeded with IOCTL_DISK_GET_LENGTH_INFO.");
            return lengthInformation.Length;
        }

        int diskLengthError = Marshal.GetLastWin32Error();
        diagnostic?.Invoke($"IOCTL_DISK_GET_LENGTH_INFO failed with Win32 error {diskLengthError}.");
        if (DeviceIoControlCapacity(stream.SafeFileHandle, IoctlStorageReadCapacity, IntPtr.Zero, 0,
                out StorageReadCapacity readCapacity, (uint)Marshal.SizeOf<StorageReadCapacity>(),
                out _, IntPtr.Zero) && readCapacity.DiskLength > 0)
        {
            diagnostic?.Invoke("Capacity query succeeded with IOCTL_STORAGE_READ_CAPACITY.");
            return readCapacity.DiskLength;
        }

        int storageCapacityError = Marshal.GetLastWin32Error();
        diagnostic?.Invoke($"IOCTL_STORAGE_READ_CAPACITY failed with Win32 error {storageCapacityError}.");
        if (GetFileSizeEx(stream.SafeFileHandle, out long fileSize) && fileSize > 0)
        {
            diagnostic?.Invoke("Capacity query succeeded with GetFileSizeEx.");
            return fileSize;
        }

        int fileSizeError = Marshal.GetLastWin32Error();
        diagnostic?.Invoke($"GetFileSizeEx failed with Win32 error {fileSizeError}.");
        throw new NativeDeviceIOException(fileSizeError,
            $"Windows rejected all read-only capacity queries " +
            $"(disk={diskLengthError}, storage={storageCapacityError}, file={fileSizeError}).");
    }

    private static bool IsUnavailableDevice(IOException exception)
    {
        int code = exception is NativeDeviceIOException native ? native.NativeErrorCode : exception.HResult & 0xFFFF;
        return code is
            1 or    // ERROR_INVALID_FUNCTION (empty pseudo-device)
            2 or    // ERROR_FILE_NOT_FOUND
            3 or    // ERROR_PATH_NOT_FOUND
            21 or   // ERROR_NOT_READY
            1112 or // ERROR_NO_MEDIA_IN_DRIVE
            1167;   // ERROR_DEVICE_NOT_CONNECTED
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GetLengthInformation
    {
        public long Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageReadCapacity
    {
        public uint Version;
        public uint Size;
        public uint BlockLength;
        private readonly uint alignmentPadding;
        public long NumberOfBlocks;
        public long DiskLength;
    }

    private sealed class NativeDeviceIOException(int nativeErrorCode, string message) : IOException(message)
    {
        public int NativeErrorCode { get; } = nativeErrorCode;
    }

    private sealed class ScanLog : IDisposable
    {
        private readonly StreamWriter? writer;

        private ScanLog(StreamWriter? writer) => this.writer = writer;

        public static ScanLog Create(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return new ScanLog(new StreamWriter(path, append: false) { AutoFlush = true });
            }
            catch (IOException)
            {
                return new ScanLog(null);
            }
            catch (UnauthorizedAccessException)
            {
                return new ScanLog(null);
            }
        }

        public void Write(string message) => writer?.WriteLine(message);
        public void Dispose() => writer?.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlLength(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        out GetLengthInformation outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlCapacity(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        out StorageReadCapacity outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle file, out long fileSize);
}
