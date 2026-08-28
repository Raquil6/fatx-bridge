using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using FatxBridge.Core;

namespace FatxBridge.Windows;

/// <summary>
/// Keeps raw-disk access in the elevated UI while hosting WinFsp in the ordinary
/// desktop user's logon token, where Explorer can see the resulting drive letter.
/// </summary>
public sealed class FatxMountBroker : IFatxMountSession
{
    private readonly NamedPipeServerStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly Process helper;
    private bool disposed;

    private FatxMountBroker(NamedPipeServerStream pipe, StreamReader reader, StreamWriter writer,
        Process helper, string mountPath)
    {
        this.pipe = pipe;
        this.reader = reader;
        this.writer = writer;
        this.helper = helper;
        MountPath = mountPath;
    }

    public string MountPath { get; }

    public static async Task<FatxMountBroker> MountAsync(string physicalPath, long capacity,
        FatxPartitionCandidate partition, bool readOnly, CancellationToken cancellationToken = default)
    {
        string pipeName = $"FatxBridge-Mount-{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        FileStream? rawStream = null;
        Process? helper = null;
        StreamReader? reader = null;
        StreamWriter? writer = null;
        try
        {
            CleanupLaunchShortcuts();
            rawStream = readOnly ? DriveScanner.OpenReadOnly(physicalPath) : DriveScanner.OpenReadWrite(physicalPath);
            StartDesktopHelper($"--mount-host \"{pipeName}\"");
            await pipe.WaitForConnectionAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            CleanupLaunchShortcuts();
            reader = new StreamReader(pipe, leaveOpen: true);
            writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            string hello = await reader.ReadLineAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                ?? throw new IOException("The desktop mount helper closed during startup.");
            string[] helloParts = hello.Split('\t');
            if (helloParts.Length != 3 || helloParts[0] != "PID" ||
                !int.TryParse(helloParts[1], out int helperProcessId) ||
                !bool.TryParse(helloParts[2], out bool helperElevated))
                throw new IOException("The desktop mount helper returned an invalid startup response.");
            if (helperElevated)
                throw new IOException("Windows started the mount helper elevated; refusing a drive letter that Explorer cannot see.");
            helper = Process.GetProcessById(helperProcessId);

            if (!DuplicateHandle(GetCurrentProcess(), rawStream.SafeFileHandle.DangerousGetHandle(),
                    helper.Handle, out nint childHandle, 0, false, DuplicateSameAccess))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not pass the raw-disk handle to the mount helper.");

            var request = new MountRequest((long)childHandle, capacity, partition.Name, partition.Offset,
                partition.Length, partition.Metadata, readOnly);
            await writer.WriteLineAsync(JsonSerializer.Serialize(request));

            string response = await reader.ReadLineAsync(cancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(20), cancellationToken)
                ?? throw new IOException("The mount helper closed before reporting its result.");
            if (!response.StartsWith("OK\t", StringComparison.Ordinal))
                throw new IOException(response.StartsWith("ERR\t", StringComparison.Ordinal) ? response[4..] : response);

            rawStream.Dispose();
            rawStream = null;
            return new FatxMountBroker(pipe, reader, writer, helper, response[3..]);
        }
        catch
        {
            rawStream?.Dispose();
            reader?.Dispose();
            writer?.Dispose();
            pipe.Dispose();
            if (helper is { HasExited: false }) helper.Kill(entireProcessTree: true);
            helper?.Dispose();
            throw;
        }
    }

    public void OpenExplorer()
    {
        if (disposed) return;
        writer.WriteLine("OPEN");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (pipe.IsConnected) writer.WriteLine("STOP");
            if (!helper.WaitForExit(5000)) helper.Kill(entireProcessTree: true);
        }
        finally
        {
            reader.Dispose();
            writer.Dispose();
            pipe.Dispose();
            helper.Dispose();
        }
    }

    internal static async Task RunHostAsync(string pipeName)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(15000);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync($"PID\t{Environment.ProcessId}\t{IsCurrentProcessElevated()}");
        FatxWinFspMount? mount = null;
        try
        {
            string json = await reader.ReadLineAsync() ?? throw new IOException("The FATX Bridge UI closed before providing mount information.");
            MountRequest request = JsonSerializer.Deserialize<MountRequest>(json)
                ?? throw new IOException("The FATX Bridge mount request was invalid.");
            var safeHandle = new SafeFileHandle(new IntPtr(request.Handle), ownsHandle: true);
            var stream = new FileStream(safeHandle, request.ReadOnly ? FileAccess.Read : FileAccess.ReadWrite);
            var partition = new FatxPartitionCandidate(request.Name, request.Offset, request.Length, request.Metadata);
            mount = FatxWinFspMount.MountOpened(stream, request.Capacity, partition, request.ReadOnly, useDriveLetter: true);

            await Task.Delay(500);
            _ = Directory.GetFileSystemEntries(mount.MountPath);
            await writer.WriteLineAsync("OK\t" + mount.MountPath);

            while (await reader.ReadLineAsync() is { } command)
            {
                if (command == "STOP") break;
                if (command == "OPEN") mount.OpenExplorer();
            }
        }
        catch (Exception exception)
        {
            try { await writer.WriteLineAsync("ERR\t" + exception.GetBaseException().Message); }
            catch { }
        }
        finally { mount?.Dispose(); }
    }

    private static void StartDesktopHelper(string arguments)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("FATX Bridge's executable path is unavailable.");
        if (!IsCurrentProcessElevated())
        {
            _ = Process.Start(new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            }) ?? throw new IOException("Windows did not start FATX Bridge's mount helper.");
            return;
        }

        nint shellWindow = GetShellWindow();
        if (shellWindow == 0)
            throw new IOException("Windows Explorer is not running, so FATX Bridge could not locate the desktop user session.");
        _ = GetWindowThreadProcessId(shellWindow, out uint shellProcessId);
        using Process shellProcess = Process.GetProcessById(checked((int)shellProcessId));
        if (!OpenProcessToken(shellProcess.Handle, TokenQuery | TokenDuplicate | TokenAssignPrimary,
                out nint desktopToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "FATX Bridge could not open Windows Explorer's primary token.");
        try
        {
            var startup = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
            char[] commandLine = CommandLine(executable, arguments);
            ProcessInformation processInfo;
            EnablePrivilege("SeImpersonatePrivilege");
            if (!CreateProcessWithTokenW(desktopToken, LogonWithProfile, executable, commandLine,
                    0, 0, AppContext.BaseDirectory, ref startup, out processInfo))
            {
                int tokenError = Marshal.GetLastWin32Error();
                TraceNative($"CreateProcessWithTokenW using Explorer's primary token failed with Win32 {tokenError}; trying CreateProcessAsUserW.");
                EnablePrivilege("SeIncreaseQuotaPrivilege");
                EnablePrivilege("SeAssignPrimaryTokenPrivilege");
                commandLine = CommandLine(executable, arguments);
                        if (!CreateProcessAsUserW(desktopToken, executable, commandLine, 0, 0, false,
                                0, 0, AppContext.BaseDirectory, ref startup, out processInfo))
                        {
                            int asUserError = Marshal.GetLastWin32Error();
                            TraceNative($"CreateProcessAsUserW failed with Win32 {asUserError}; handing launch to the existing Explorer shell.");
                            if (TryStartThroughExistingExplorer(executable, arguments)) return;
                            throw new Win32Exception(asUserError,
                                $"Windows could not start FATX Bridge's standard-user mount helper (CreateProcessWithTokenW={tokenError}, CreateProcessAsUserW={asUserError}).");
                }
            }
            CloseHandle(processInfo.Thread);
            CloseHandle(processInfo.Process);
        }
        finally { CloseHandle(desktopToken); }
    }

    private static bool TryStartThroughExistingExplorer(string executable, string arguments)
    {
        try
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application")
                ?? throw new IOException("Windows Shell automation is unavailable.");
            object shell = Activator.CreateInstance(shellType)
                ?? throw new IOException("Windows Shell automation could not be started.");
            object? windows = null;
            try
            {
                windows = shellType.InvokeMember("Windows", BindingFlags.InvokeMethod, null, shell, null,
                    CultureInfo.InvariantCulture);
                if (windows is null) throw new IOException("Windows Shell did not expose its Explorer windows.");
                Type windowsType = windows.GetType();
                int count = Convert.ToInt32(windowsType.InvokeMember("Count", BindingFlags.GetProperty,
                    null, windows, null, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                for (int index = 0; index < count; index++)
                {
                    object? explorerWindow = null;
                    object? document = null;
                    object? application = null;
                    try
                    {
                        explorerWindow = windowsType.InvokeMember("Item", BindingFlags.InvokeMethod, null,
                            windows, [index], CultureInfo.InvariantCulture);
                        if (explorerWindow is null) continue;
                        Type windowType = explorerWindow.GetType();
                        string? fullName = windowType.InvokeMember("FullName", BindingFlags.GetProperty, null,
                            explorerWindow, null, CultureInfo.InvariantCulture) as string;
                        if (!string.Equals(Path.GetFileName(fullName), "explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        document = windowType.InvokeMember("Document", BindingFlags.GetProperty, null,
                            explorerWindow, null, CultureInfo.InvariantCulture);
                        if (document is null) continue;
                        application = document.GetType().InvokeMember("Application", BindingFlags.GetProperty,
                            null, document, null, CultureInfo.InvariantCulture);
                        if (application is null) continue;
                        _ = application.GetType().InvokeMember("ShellExecute", BindingFlags.InvokeMethod,
                            null, application, [executable, arguments, AppContext.BaseDirectory, string.Empty, 0],
                            CultureInfo.InvariantCulture);
                        return true;
                    }
                    finally
                    {
                        if (application is not null && Marshal.IsComObject(application)) Marshal.FinalReleaseComObject(application);
                        if (document is not null && Marshal.IsComObject(document)) Marshal.FinalReleaseComObject(document);
                        if (explorerWindow is not null && Marshal.IsComObject(explorerWindow)) Marshal.FinalReleaseComObject(explorerWindow);
                    }
                }
                throw new IOException("No running File Explorer window accepted the FATX Bridge helper launch.");
            }
            finally
            {
                if (windows is not null && Marshal.IsComObject(windows)) Marshal.FinalReleaseComObject(windows);
                if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception exception)
        {
            TraceNative($"Explorer shortcut launch failed: {exception.GetBaseException().Message}");
            return false;
        }
    }

    private static void CleanupLaunchShortcuts()
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FatxBridge", "Launch");
            if (!Directory.Exists(directory)) return;
            foreach (string path in Directory.EnumerateFiles(directory, "FatxBridge-Mount-*.lnk"))
                File.Delete(path);
        }
        catch { }
    }

    private static char[] CommandLine(string executable, string arguments) =>
        ($"\"{executable}\" {arguments}\0").ToCharArray();

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out nint token)) return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out Luid luid)) return;
            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = PrivilegeEnabled,
            };
            _ = AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0);
        }
        finally { CloseHandle(token); }
    }

    private static void TraceNative(string message)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatxBridge");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "mount-broker.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static bool IsCurrentProcessElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out nint token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            nint buffer = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                if (!GetTokenInformation(token, TokenElevationType, buffer, sizeof(int), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return Marshal.ReadInt32(buffer) == TokenElevationTypeFull;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    private sealed record MountRequest(long Handle, long Capacity, string Name, long Offset, long Length,
        FatxVolumeMetadata Metadata, bool ReadOnly);

    private const uint TokenAssignPrimary = 0x0001, TokenDuplicate = 0x0002, TokenQuery = 0x0008,
        TokenAdjustPrivileges = 0x0020;
    private const int TokenElevationType = 18, TokenElevationTypeFull = 2;
    private const uint LogonWithProfile = 0x00000001, DuplicateSameAccess = 0x00000002;
    private const uint PrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2Size;
        public nint Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(nint sourceProcess,
        nint sourceHandle, nint targetProcess, out nint targetHandle, uint desiredAccess, bool inheritHandle, uint options);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process,
        uint desiredAccess, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token,
        int informationClass, nint information, int informationLength, out int returnLength);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(nint token, uint logonFlags, string applicationName,
        [In, Out] char[] commandLine, uint creationFlags, nint environment, string currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUserW(nint token, string applicationName,
        [In, Out] char[] commandLine, nint processAttributes, nint threadAttributes, bool inheritHandles,
        uint creationFlags, nint environment, string currentDirectory, ref StartupInfo startupInfo,
        out ProcessInformation processInformation);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(nint token, bool disableAllPrivileges,
        ref TokenPrivileges newState, uint bufferLength, nint previousState, nint returnLength);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
