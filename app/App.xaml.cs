using System.Windows;
using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using System.Buffers.Binary;
using FatxBridge.Core;

namespace FatxBridge.Windows;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        int hostArgument = Array.FindIndex(e.Args, value => value.Equals("--mount-host", StringComparison.OrdinalIgnoreCase));
        if (hostArgument >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (hostArgument + 1 >= e.Args.Length) { Shutdown(2); return; }
            _ = RunMountHostAsync(e.Args[hostArgument + 1]);
            return;
        }

        if (e.Args.Contains("--probe-broker", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunBrokerProbeAsync();
            return;
        }

        if (e.Args.Contains("--benchmark-write", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunWriteBenchmarkAsync();
            return;
        }

        int driveHostArgument = Array.FindIndex(e.Args,
            value => value.Equals("--elevated-drive-host", StringComparison.OrdinalIgnoreCase));
        if (driveHostArgument >= 0)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (driveHostArgument + 2 >= e.Args.Length ||
                !int.TryParse(e.Args[driveHostArgument + 2], out int targetProcessId))
            { Shutdown(2); return; }
            _ = RunElevatedDriveHostAsync(e.Args[driveHostArgument + 1], targetProcessId);
            return;
        }

        if (e.Args.Contains("--probe-mount", StringComparer.OrdinalIgnoreCase))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunMountProbeAsync(e.Args.Contains("--probe-hold", StringComparer.OrdinalIgnoreCase));
            return;
        }

        new MainWindow().Show();
    }

    private async Task RunMountHostAsync(string pipeName)
    {
        try { await FatxMountBroker.RunHostAsync(pipeName); }
        finally { Shutdown(); }
    }

    private async Task RunElevatedDriveHostAsync(string pipeName, int targetProcessId)
    {
        try { await ElevatedDriveBroker.RunHostAsync(pipeName, targetProcessId); }
        catch (Exception exception)
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatxBridge");
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "elevated-drive-host.log"), exception.ToString());
            }
            catch { }
        }
        finally { Shutdown(); }
    }

    private async Task RunBrokerProbeAsync()
    {
        string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatxBridge", "broker-probe.log");
        string imagePath = Path.Combine(Path.GetTempPath(), $"fatxbridge-probe-{Guid.NewGuid():N}.fatx");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        try
        {
            const long length = 8L * 1024 * 1024;
            const int headerSize = 0x1000, sectorSize = 4096, sectorsPerCluster = 4;
            const int clusterSize = sectorSize * sectorsPerCluster;
            long mapEntries = length / clusterSize + 1;
            long dataOffset = headerSize + ((mapEntries * 2 + headerSize - 1) / headerSize * headerSize);
            await using (var image = new FileStream(imagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                image.SetLength(length);
                byte[] header = new byte[headerSize];
                "FATX"u8.CopyTo(header);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x53494445);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), sectorsPerCluster);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 1);
                await image.WriteAsync(header);
                image.Position = headerSize;
                byte[] fat = new byte[6];
                BinaryPrimitives.WriteUInt16LittleEndian(fat, 0xFFF8);
                BinaryPrimitives.WriteUInt16LittleEndian(fat.AsSpan(2), 0xFFFF);
                BinaryPrimitives.WriteUInt16LittleEndian(fat.AsSpan(4), 0xFFFF);
                await image.WriteAsync(fat);
                image.Position = dataOffset;
                byte[] entry = new byte[0x40];
                entry[0] = 9;
                "hello.txt"u8.CopyTo(entry.AsSpan(2));
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(0x2C), 2);
                byte[] greeting = "FATX Bridge works!"u8.ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(0x30), checked((uint)greeting.Length));
                await image.WriteAsync(entry);
                image.Position = dataOffset + clusterSize;
                await image.WriteAsync(greeting);
            }

            FatxPartitionCandidate partition;
            using (var image = File.OpenRead(imagePath))
            {
                FatxStorageDetection detection = FatxPartitionProbe.DetectSupportedStorage(image, length)
                    ?? throw new IOException("The synthetic original Xbox memory unit was not detected.");
                if (detection.Kind != FatxStorageKind.OriginalXboxMemoryUnit)
                    throw new IOException($"The synthetic memory unit was misclassified as {detection.Kind}.");
                partition = detection.Partitions.Single();
            }
            string mountPath;
            string[] entries;
            string content;
            bool deferredLengthReadWasZero;
            bool mountedDeletePassed;
            using (var mount = await FatxMountBroker.MountAsync(imagePath, length, partition, readOnly: false))
            {
                mountPath = mount.MountPath;
                entries = Directory.GetFileSystemEntries(mount.MountPath);
                content = await File.ReadAllTextAsync(Path.Combine(mount.MountPath, "hello.txt"));
                await File.WriteAllTextAsync(Path.Combine(mount.MountPath, "copied.txt"), "Written through Windows Explorer");
                byte[] prefix = Enumerable.Range(0, 64 * 1024).Select(index => unchecked((byte)(index * 17 + 3))).ToArray();
                byte[] unwritten = new byte[64 * 1024];
                using var deferred = new FileStream(Path.Combine(mount.MountPath, "deferred.bin"), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None);
                deferred.SetLength(1024 * 1024);
                deferred.Write(prefix);
                deferred.Position = 512 * 1024;
                _ = deferred.Read(unwritten);
                deferredLengthReadWasZero = unwritten.All(value => value == 0);
                if (!deferredLengthReadWasZero) throw new IOException("An unwritten deferred-length range did not read back as zero.");
                using var shrink = new FileStream(Path.Combine(mount.MountPath, "shrink.bin"), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None);
                shrink.SetLength(1024 * 1024);
                shrink.Write(prefix);
                shrink.SetLength(32 * 1024);

                string deleteFile = Path.Combine(mount.MountPath, "delete-me.txt");
                await File.WriteAllTextAsync(deleteFile, "delete through WinFsp cleanup");
                File.Delete(deleteFile);
                if (File.Exists(deleteFile)) throw new IOException("A deleted mounted FATX file remained visible.");
                string deleteDirectory = Path.Combine(mount.MountPath, "delete-dir");
                Directory.CreateDirectory(deleteDirectory);
                Directory.Delete(deleteDirectory);
                if (Directory.Exists(deleteDirectory)) throw new IOException("A deleted mounted FATX directory remained visible.");
                mountedDeletePassed = true;
            }
            string persisted;
            using (var image = File.OpenRead(imagePath))
            {
                FatxVolume volume = FatxVolume.Open(image, 0, length, length, partition.Metadata.SectorSize);
                int persistedLength = checked((int)volume.GetEntry("/copied.txt").Entry.FileSize);
                persisted = System.Text.Encoding.UTF8.GetString(volume.ReadFile("/copied.txt", 0, persistedLength));
                FatxDirectoryEntry deferredEntry = volume.GetEntry("/deferred.bin").Entry;
                if (deferredEntry.FileSize != 1024 * 1024) throw new IOException("The deferred file length was not finalized on close.");
                if (volume.ReadFile("/deferred.bin", 512 * 1024, 64 * 1024).Any(value => value != 0))
                    throw new IOException("The finalized unwritten file tail did not persist as zero.");
                if (volume.GetEntry("/shrink.bin").Entry.FileSize != 32 * 1024)
                    throw new IOException("Shrinking a deferred file length did not persist the requested size.");
            }
            await File.WriteAllTextAsync(logPath,
                $"PASS {DateTimeOffset.Now:O}{Environment.NewLine}Layout: original Xbox MU, {partition.Metadata.SectorSize}-byte sectors{Environment.NewLine}Mount: {mountPath}{Environment.NewLine}Entries: {string.Join(", ", entries.Select(Path.GetFileName))}{Environment.NewLine}Read: {content}{Environment.NewLine}Persisted write: {persisted}{Environment.NewLine}Deferred EOF zero-fill: {deferredLengthReadWasZero}, persisted and finalized{Environment.NewLine}Deferred EOF shrink: persisted{Environment.NewLine}Mounted file/directory delete: {mountedDeletePassed}");
        }
        catch (Exception exception) { await File.WriteAllTextAsync(logPath, $"FAIL {DateTimeOffset.Now:O}{Environment.NewLine}{exception}"); }
        finally
        {
            try { File.Delete(imagePath); } catch { }
            Shutdown();
        }
    }

    private async Task RunWriteBenchmarkAsync()
    {
        string diagnosticsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatxBridge");
        string logPath = Path.Combine(diagnosticsDirectory, "write-benchmark.log");
        string imagePath = Path.Combine(Path.GetTempPath(), $"fatxbridge-benchmark-{Guid.NewGuid():N}.fatx");
        string sourcePath = Path.Combine(Path.GetTempPath(), $"fatxbridge-source-{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(diagnosticsDirectory);
        try
        {
            const long imageLength = 512L * 1024 * 1024;
            const int headerSize = 0x1000, sectorsPerCluster = 8, clusterSize = sectorsPerCluster * 512;
            const int transferSize = 128 * 1024 * 1024, blockSize = 4 * 1024 * 1024;
            long mapEntries = imageLength / clusterSize + 1;
            long dataOffset = headerSize + ((mapEntries * 4 + headerSize - 1) / headerSize * headerSize);

            await using (var image = new FileStream(imagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                image.SetLength(imageLength);
                byte[] header = new byte[headerSize];
                "FATX"u8.CopyTo(header);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x53494445);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), sectorsPerCluster);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 1);
                await image.WriteAsync(header);
                image.Position = headerSize;
                byte[] fat = new byte[8];
                BinaryPrimitives.WriteUInt32LittleEndian(fat, 0xFFFFFFF8);
                BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(4), 0xFFFFFFFF);
                await image.WriteAsync(fat);
                image.Position = dataOffset;
                await image.WriteAsync(new byte[clusterSize]);
            }

            byte[] block = new byte[blockSize];
            for (int index = 0; index < block.Length; index++) block[index] = unchecked((byte)(index * 31 + 17));
            await using (var source = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                blockSize, FileOptions.SequentialScan))
            {
                for (int written = 0; written < transferSize; written += block.Length) await source.WriteAsync(block);
                await source.FlushAsync();
            }

            FatxVolumeMetadata metadata;
            using (var image = File.OpenRead(imagePath)) metadata = FatxVolume.Open(image, 0, imageLength, imageLength).Metadata;
            var partition = new FatxPartitionCandidate("Benchmark", 0, imageLength, metadata);
            string mountPath;
            var stopwatch = new Stopwatch();
            Environment.SetEnvironmentVariable("FATXBRIDGE_WINFSP_DEBUG", "1");
            Environment.SetEnvironmentVariable("FATXBRIDGE_PERFORMANCE_LOG", "1");
            using (var mount = await FatxMountBroker.MountAsync(imagePath, imageLength, partition, readOnly: false))
            {
                mountPath = mount.MountPath;
                stopwatch.Start();
                File.Copy(sourcePath, Path.Combine(mountPath, "speed.bin"), overwrite: false);
                stopwatch.Stop();
            }

            using (var image = File.OpenRead(imagePath))
            {
                FatxVolume volume = FatxVolume.Open(image, 0, imageLength, imageLength);
                FatxDirectoryEntry entry = volume.GetEntry("/speed.bin").Entry;
                if (entry.FileSize != transferSize) throw new IOException($"The benchmark file persisted with {entry.FileSize} bytes instead of {transferSize}.");
                byte[] first = volume.ReadFile("/speed.bin", 0, block.Length);
                byte[] last = volume.ReadFile("/speed.bin", transferSize - block.Length, block.Length);
                if (!first.AsSpan().SequenceEqual(block) || !last.AsSpan().SequenceEqual(block))
                    throw new IOException("The mounted bulk-write benchmark did not persist the expected bytes.");
            }

            double mib = transferSize / 1048576d;
            double speed = mib / stopwatch.Elapsed.TotalSeconds;
            await File.WriteAllTextAsync(logPath,
                $"PASS {DateTimeOffset.Now:O}{Environment.NewLine}Mount: {mountPath}{Environment.NewLine}" +
                $"Transfer: {mib:F0} MiB{Environment.NewLine}Elapsed: {stopwatch.Elapsed.TotalSeconds:F3} s{Environment.NewLine}" +
                $"Throughput: {speed:F1} MiB/s{Environment.NewLine}Persistence: first and last 4 MiB verified");
        }
        catch (Exception exception) { await File.WriteAllTextAsync(logPath, $"FAIL {DateTimeOffset.Now:O}{Environment.NewLine}{exception}"); }
        finally
        {
            try { File.Delete(sourcePath); } catch { }
            try { File.Delete(imagePath); } catch { }
            Shutdown();
        }
    }

    private static bool IsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private async Task RunMountProbeAsync(bool holdMount)
    {
        string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FatxBridge", "mount-probe.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        try
        {
            Environment.SetEnvironmentVariable("FATXBRIDGE_WINFSP_DEBUG", "1");
            await using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
            await log.WriteLineAsync($"Probe started {DateTimeOffset.Now:O}");
            using var broker = await ElevatedDriveBroker.StartAsync();
            DriveScanResult scan = await broker.ScanAsync();
            if (scan.Drives.Count == 0) throw new IOException("No validated Xbox FATX drive was detected.");
            DriveScanItem drive = scan.Drives[0];
            FatxPartitionCandidate partition = drive.Partitions[0];
            for (int index = 0; index < drive.Partitions.Count; index++)
                if (drive.Partitions[index].Name == "Content") { partition = drive.Partitions[index]; break; }
            await log.WriteLineAsync($"Mounting {drive.Path}, partition {partition.Name}.");
            FileStream stream = await broker.OpenAsync(drive.Path, readOnly: true);
            using var mount = FatxWinFspMount.MountOpened(stream, drive.Capacity, partition,
                readOnly: true, useDriveLetter: true);
            await log.WriteLineAsync($"Normal-user WinFsp host mounted: {mount.MountPath}");
            string[] entries = Directory.GetFileSystemEntries(mount.MountPath);
            await log.WriteLineAsync($"Explorer-context root query passed with {entries.Length} entries.");
            foreach (string entry in entries.Take(20)) await log.WriteLineAsync(Path.GetFileName(entry));
            if (holdMount)
            {
                mount.OpenExplorer();
                await log.WriteLineAsync("Explorer opened; mount is held for 20 seconds for visible verification.");
                await Task.Delay(TimeSpan.FromSeconds(20));
            }
        }
        catch (Exception exception)
        {
            await File.WriteAllTextAsync(logPath, $"Probe failed {DateTimeOffset.Now:O}{Environment.NewLine}{exception}");
        }
        finally { Shutdown(); }
    }
}
