using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace FatxBridge.Windows;

/// <summary>
/// A normal-user client for the narrowly scoped elevated raw-disk helper. WinFsp
/// remains in this normal process, so its drive letter shares Explorer's namespace.
/// </summary>
public sealed class ElevatedDriveBroker : IDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly Process helper;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    private ElevatedDriveBroker(NamedPipeServerStream pipe, StreamReader reader, StreamWriter writer, Process helper)
    {
        this.pipe = pipe;
        this.reader = reader;
        this.writer = writer;
        this.helper = helper;
    }

    public static async Task<ElevatedDriveBroker> StartAsync(CancellationToken cancellationToken = default)
    {
        string pipeName = $"FatxBridge-Raw-{Guid.NewGuid():N}";
        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        Process? helper = null;
        try
        {
            string executable = Environment.ProcessPath ?? throw new IOException("FATX Bridge's executable path is unavailable.");
            helper = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"--elevated-drive-host \"{pipeName}\" {Environment.ProcessId}",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
                Verb = "runas",
            }) ?? throw new IOException("Windows did not start FATX Bridge's physical-drive helper.");
            await pipe.WaitForConnectionAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            var reader = new StreamReader(pipe, leaveOpen: true);
            var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            string ready = await reader.ReadLineAsync(cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                ?? throw new IOException("The physical-drive helper closed during startup.");
            if (ready != "READY") throw new IOException(ready.StartsWith("ERR\t", StringComparison.Ordinal) ? ready[4..] : ready);
            return new ElevatedDriveBroker(pipe, reader, writer, helper);
        }
        catch
        {
            pipe.Dispose();
            if (helper is { HasExited: false }) helper.Kill(entireProcessTree: true);
            helper?.Dispose();
            throw;
        }
    }

    public async Task<DriveScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync("SCAN");
            string response = await ReadResponseAsync(cancellationToken);
            return JsonSerializer.Deserialize<DriveScanResult>(response)
                ?? throw new IOException("The physical-drive helper returned an invalid scan result.");
        }
        finally { gate.Release(); }
    }

    public async Task<FileStream> OpenAsync(string path, bool readOnly, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync("OPEN\t" + JsonSerializer.Serialize(new OpenRequest(path, readOnly)));
            string response = await ReadResponseAsync(cancellationToken);
            if (!long.TryParse(response, out long handleValue) || handleValue == 0)
                throw new IOException("The physical-drive helper returned an invalid raw handle.");
            var handle = new SafeFileHandle(new IntPtr(handleValue), ownsHandle: true);
            return new FileStream(handle, readOnly ? FileAccess.Read : FileAccess.ReadWrite, 4096, false);
        }
        finally { gate.Release(); }
    }

    private async Task<string> ReadResponseAsync(CancellationToken cancellationToken)
    {
        string response = await reader.ReadLineAsync(cancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(60), cancellationToken)
            ?? throw new IOException("The physical-drive helper disconnected.");
        if (response.StartsWith("OK\t", StringComparison.Ordinal)) return response[3..];
        throw new IOException(response.StartsWith("ERR\t", StringComparison.Ordinal) ? response[4..] : response);
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
            gate.Dispose();
        }
    }

    internal static async Task RunHostAsync(string pipeName, int targetProcessId)
    {
        TraceHost($"Host entry. PID={Environment.ProcessId}, target={targetProcessId}, pipe={pipeName}.");
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(60000);
        TraceHost("Pipe connected.");
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        Process? target = null;
        try
        {
            target = Process.GetProcessById(targetProcessId);
            _ = target.Handle;
            TraceHost("Target process handle opened.");
            await writer.WriteLineAsync("READY");
            TraceHost("READY flushed; entering command loop.");
            while (await reader.ReadLineAsync() is { } command)
            {
                if (command == "STOP") break;
                try
                {
                    if (command == "SCAN")
                    {
                        DriveScanResult result = await Task.Run(DriveScanner.Scan);
                        await writer.WriteLineAsync("OK\t" + JsonSerializer.Serialize(result));
                    }
                    else if (command.StartsWith("OPEN\t", StringComparison.Ordinal))
                    {
                        OpenRequest request = JsonSerializer.Deserialize<OpenRequest>(command[5..])
                            ?? throw new IOException("The raw-open request was invalid.");
                        using FileStream source = request.ReadOnly
                            ? DriveScanner.OpenReadOnly(request.Path)
                            : DriveScanner.OpenReadWrite(request.Path);
                        if (!DuplicateHandle(GetCurrentProcess(), source.SafeFileHandle.DangerousGetHandle(),
                                target.Handle, out nint targetHandle, 0, false, DuplicateSameAccess))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not transfer the physical-drive handle to FATX Bridge.");
                        await writer.WriteLineAsync("OK\t" + ((long)targetHandle).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else throw new IOException("The physical-drive helper received an unknown request.");
                }
                catch (Exception exception) { await writer.WriteLineAsync("ERR\t" + exception.GetBaseException().Message); }
            }
        }
        catch (Exception exception)
        {
            TraceHost("Host failure: " + exception);
            try { await writer.WriteLineAsync("ERR\t" + exception.GetBaseException().Message); } catch { }
        }
        finally { target?.Dispose(); TraceHost("Host exit."); }
    }

    private static void TraceHost(string message)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatxBridge");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "elevated-drive-host-steps.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private sealed record OpenRequest(string Path, bool ReadOnly);
    private const uint DuplicateSameAccess = 0x00000002;
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(nint sourceProcess,
        nint sourceHandle, nint targetProcess, out nint targetHandle, uint desiredAccess, bool inheritHandle, uint options);
}
