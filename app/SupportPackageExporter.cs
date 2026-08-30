using System.IO.Compression;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FatxBridge.Windows;

/// <summary>
/// Creates a local-only support package containing a fixed set of FATX Bridge
/// diagnostic logs. The exporter never enumerates or recursively traverses the
/// diagnostics directory.
/// </summary>
public static class SupportPackageExporter
{
    /// <summary>Maximum number of source bytes read from one diagnostic.</summary>
    public const long MaximumDiagnosticFileBytes = 1L * 1024 * 1024;

    /// <summary>Maximum total number of uncompressed diagnostic bytes in a package.</summary>
    public const long MaximumTotalDiagnosticBytes = 8L * 1024 * 1024;

    private const int ManifestSchemaVersion = 1;
    private const int ReadBufferSize = 64 * 1024;
    private const string ManifestEntryName = "manifest.json";
    private const string DiagnosticsEntryDirectory = "diagnostics/";
    private const string DiagnosticsDirectoryName = "FatxBridge";
    private const string PackagePartialSuffix = ".fatxbridge-partial";
    private const string NotFoundReason = "not-found";
    private const string DirectoryReason = "directory-not-a-log-file";
    private const string ReparsePointReason = "reparse-point-not-followed";
    private const string UnavailableReason = "unavailable-to-read";
    private const string NonTextReason = "non-text-diagnostic";
    private const string TotalLimitReason = "total-size-limit";
    private const string DiagnosticsDirectoryReparseReason = "diagnostics-directory-reparse-point";
    private const string DiagnosticsDirectoryUnavailableReason = "diagnostics-directory-unavailable";
    private const string DiagnosticsDirectoryNotDirectoryReason = "diagnostics-directory-not-a-directory";
    private const string LocalDataUnavailableReason = "local-app-data-unavailable";
    private const string NonLocalSourceReason = "diagnostics-location-not-local";
    private const string NonLocalDestinationMessage = "Support packages must be written to a local Windows path.";

    private static readonly DateTimeOffset ZipEpoch =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // Keep this list in ordinal order. These are the exact log names currently
    // written by the application; do not replace it with a wildcard or a
    // directory enumeration.
    private static readonly string[] AllowlistedNames =
    [
        "broker-probe.log",
        "drive-scan.log",
        "elevated-drive-host-steps.log",
        "elevated-drive-host.log",
        "fatx-callback.log",
        "mount-broker.log",
        "mount-probe.log",
        "winfsp-debug.log",
        "write-benchmark.log",
        "write-performance.log",
    ];

    private static readonly IReadOnlyList<string> ReadOnlyAllowlistedNames =
        Array.AsReadOnly(AllowlistedNames);

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    private static readonly Regex MountPathLabelRegex = new(
        @"(?<prefix>\b(?:mount(?:ed|ing)?(?:\s+(?:path|at))?|mountpath)\s*[:=]\s*)(?<value>(?:[A-Z]:[\\/]|\\\\)[^\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex MountedAtRegex = new(
        @"(?<prefix>\bmount(?:ed)?\s+at\s+)(?<value>(?:[A-Z]:[\\/]|\\\\)[^\r\n]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex PipePathRegex = new(
        @"(?<prefix>\\\\\.\\pipe\\)[^\s,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PipeAssignmentRegex = new(
        @"(?<prefix>\bpipe(?:name|id)?\s*[:=]\s*)[^\r\n,;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex SerialFieldRegex = new(
        @"(?<prefix>[""']?\b(?:serial(?:\s*(?:number|no\.?)?)?|device\s+serial(?:\s*(?:number|no\.?)?)?|disk\s+serial(?:\s*(?:number|no\.?)?)?|volume\s+serial(?:\s*(?:number|no\.?)?)?|serialnumber)[""']?\s*[:=]\s*)(?<value>""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex PhysicalDriveNumberRegex = new(
        @"\bPhysicalDrive\s*[-_]?\d+\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GenericUserProfileSegmentRegex = new(
        @"(?<![\p{L}\p{N}])(?:[A-Z]:[\\/](?:Users|Documents and Settings)[\\/])[^\\/\s""'<>(),;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GenericUncUserProfileSegmentRegex = new(
        @"(?<![\p{L}\p{N}])\\\\[^\\/\s]+[\\/](?:Users|Documents and Settings)[\\/][^\\/\s""'<>(),;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex GuidRegex = new(
        @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Returns the exact diagnostic filenames that may be read by the exporter.
    /// No other files, including application state files, are eligible.
    /// </summary>
    public static IReadOnlyList<string> AllowlistedDiagnosticFileNames => ReadOnlyAllowlistedNames;

    /// <summary>
    /// Redacts common host identifiers from diagnostic text using the same
    /// policy as <see cref="ExportAsync"/>.
    /// </summary>
    public static SupportPackageRedactionResult RedactDiagnosticText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        string value = text;

        value = ReplaceLiteral(value, "%LOCALAPPDATA%", "<LOCAL_APP_DATA>", "local-app-data-path", counts);
        value = ReplaceLiteral(value, "%USERPROFILE%", "<USER_PROFILE>", "user-profile-path", counts);
        value = ReplaceLiteral(value, "%APPDATA%", "<APP_DATA>", "app-data-path", counts);
        value = ReplaceLiteral(value, "%TEMP%", "<TEMP_PATH>", "temp-path", counts);
        value = ReplaceLiteral(value, "%TMP%", "<TEMP_PATH>", "temp-path", counts);

        // A mount label is more useful when retained, but the value can expose
        // a drive letter, UNC path, or a WinFsp path. Replace only the value.
        value = ReplaceRegex(value, MountPathLabelRegex,
            match => match.Groups["prefix"].Value + "<MOUNT_PATH>", "mount-path", counts);
        value = ReplaceRegex(value, MountedAtRegex,
            match => match.Groups["prefix"].Value + "<MOUNT_PATH>", "mount-path", counts);

        value = ReplaceRegex(value, PipeAssignmentRegex,
            match => match.Groups["prefix"].Value + "<PIPE_ID>", "pipe-id", counts);
        value = ReplaceRegex(value, PipePathRegex,
            match => match.Groups["prefix"].Value + "<PIPE_ID>", "pipe-id", counts);
        value = ReplaceRegex(value, SerialFieldRegex,
            match => match.Groups["prefix"].Value + "<SERIAL_NUMBER>", "serial-number", counts);
        value = ReplaceRegex(value, PhysicalDriveNumberRegex, "PhysicalDrive#", "physical-drive-number", counts);

        // Resolve the current machine's path aliases without ever writing the
        // resolved path to the manifest. Longest paths first prevents a temp or
        // LocalApplicationData path from being partially replaced by the user
        // profile path.
        var pathReplacements = new List<(string Path, string Replacement, string Kind)>();
        AddPathReplacement(pathReplacements, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "<LOCAL_APP_DATA>", "local-app-data-path");
        AddPathReplacement(pathReplacements, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "<APP_DATA>", "app-data-path");
        AddPathReplacement(pathReplacements, Path.GetTempPath(), "<TEMP_PATH>", "temp-path");
        AddPathReplacement(pathReplacements, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "<USER_PROFILE>", "user-profile-path");

        foreach ((string path, string replacement, string kind) in pathReplacements
                     .OrderByDescending(item => item.Path.Length)
                     .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase))
        {
            value = ReplacePathLiteral(value, path, replacement, kind, counts);
        }

        // Cover profile roots for a different account or a path spelling that
        // is not the current user's exact profile. Only the user-directory
        // segment is retained as a stable placeholder; the subpath is still
        // useful for finding the kind of failure in a log.
        value = ReplaceRegex(value, GenericUserProfileSegmentRegex, "<USER_PROFILE>",
            "user-profile-path", counts);
        value = ReplaceRegex(value, GenericUncUserProfileSegmentRegex, "<USER_PROFILE>",
            "user-profile-path", counts);
        value = ReplaceRegex(value, GuidRegex, "<GUID>", "guid", counts);

        return new SupportPackageRedactionResult(value, ToReadOnlyCounts(counts));
    }

    /// <summary>
    /// Exports the allowlisted diagnostics to <paramref name="destinationPath"/>
    /// and atomically publishes the completed ZIP. The destination is never
    /// written into the manifest.
    /// </summary>
    public static async Task<SupportPackageExportResult> ExportAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        EnsureLocalPath(fullDestinationPath);
        if (Directory.Exists(fullDestinationPath))
            throw new ArgumentException("The support package destination must be a file path.", nameof(destinationPath));

        string destinationDirectory = Path.GetDirectoryName(fullDestinationPath)
            ?? throw new ArgumentException("The support package destination must have a parent directory.", nameof(destinationPath));
        Directory.CreateDirectory(destinationDirectory);

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        IReadOnlyList<SupportPackageDiagnostic> included;
        IReadOnlyList<SupportPackageDiagnostic> skipped;
        IReadOnlyList<CollectedDiagnostic> collected;

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            included = Array.Empty<SupportPackageDiagnostic>();
            skipped = AllowlistedNames
                .Select(name => CreateSkipped(name, LocalDataUnavailableReason, null))
                .ToArray();
            collected = Array.Empty<CollectedDiagnostic>();
        }
        else
        {
            string diagnosticsDirectory = Path.GetFullPath(Path.Combine(localAppData, DiagnosticsDirectoryName));
            if (IsUncPath(diagnosticsDirectory))
            {
                included = Array.Empty<SupportPackageDiagnostic>();
                skipped = AllowlistedNames
                    .Select(name => CreateSkipped(name, NonLocalSourceReason, null))
                    .ToArray();
                collected = Array.Empty<CollectedDiagnostic>();
            }
            else
            {
                RejectDiagnosticDestination(fullDestinationPath, diagnosticsDirectory);
                DirectoryState directoryState = GetDirectoryState(diagnosticsDirectory);
                if (directoryState != DirectoryState.Available)
                {
                    string reason = directoryState switch
                    {
                        DirectoryState.Missing => NotFoundReason,
                        DirectoryState.ReparsePoint => DiagnosticsDirectoryReparseReason,
                        DirectoryState.NotDirectory => DiagnosticsDirectoryNotDirectoryReason,
                        _ => DiagnosticsDirectoryUnavailableReason,
                    };
                    included = Array.Empty<SupportPackageDiagnostic>();
                    skipped = AllowlistedNames.Select(name => CreateSkipped(name, reason, null)).ToArray();
                    collected = Array.Empty<CollectedDiagnostic>();
                }
                else
                {
                    (collected, included, skipped) = await CollectDiagnosticsAsync(
                        diagnosticsDirectory, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] manifest = CreateManifest(included, skipped);
        string temporaryPath = fullDestinationPath + PackagePartialSuffix + $"-{Guid.NewGuid():N}.zip";
        bool published = false;
        try
        {
            long packageSize;
            await using (var packageStream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             ReadBufferSize,
                             FileOptions.SequentialScan | FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteZipEntry(archive, ManifestEntryName, manifest, cancellationToken);
                    foreach (CollectedDiagnostic diagnostic in collected)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        WriteZipEntry(archive, DiagnosticsEntryDirectory + diagnostic.Metadata.FileName,
                            diagnostic.Content, cancellationToken);
                    }
                }

                await packageStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                packageStream.Flush(flushToDisk: true);
                packageSize = packageStream.Length;
            }

            cancellationToken.ThrowIfCancellationRequested();
            // The temporary path is adjacent to the destination, so this move
            // remains a same-volume atomic publish on Windows. An existing
            // package is replaced only after the new ZIP is complete.
            File.Move(temporaryPath, fullDestinationPath, overwrite: true);
            published = true;

            return new SupportPackageExportResult(
                fullDestinationPath,
                included.Count,
                packageSize,
                included,
                skipped);
        }
        finally
        {
            if (!published)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task<(
        IReadOnlyList<CollectedDiagnostic> Collected,
        IReadOnlyList<SupportPackageDiagnostic> Included,
        IReadOnlyList<SupportPackageDiagnostic> Skipped)> CollectDiagnosticsAsync(
        string diagnosticsDirectory,
        CancellationToken cancellationToken)
    {
        var collected = new List<CollectedDiagnostic>();
        var included = new List<SupportPackageDiagnostic>();
        var skipped = new List<SupportPackageDiagnostic>();
        long remainingTotalBytes = MaximumTotalDiagnosticBytes;

        foreach (string fileName in AllowlistedNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(diagnosticsDirectory, fileName);

            FileAttributes attributes;
            long sourceSize;
            try
            {
                attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    skipped.Add(CreateSkipped(fileName, DirectoryReason, null));
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    skipped.Add(CreateSkipped(fileName, ReparsePointReason, null));
                    continue;
                }

                sourceSize = new FileInfo(path).Length;
            }
            catch (FileNotFoundException)
            {
                skipped.Add(CreateSkipped(fileName, NotFoundReason, null));
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                skipped.Add(CreateSkipped(fileName, NotFoundReason, null));
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                skipped.Add(CreateSkipped(fileName, UnavailableReason, null));
                continue;
            }
            catch (IOException)
            {
                skipped.Add(CreateSkipped(fileName, UnavailableReason, null));
                continue;
            }
            catch (SecurityException)
            {
                skipped.Add(CreateSkipped(fileName, UnavailableReason, null));
                continue;
            }

            if (remainingTotalBytes == 0 && sourceSize != 0)
            {
                skipped.Add(CreateSkipped(fileName, TotalLimitReason, sourceSize));
                continue;
            }

            long sourceReadLimit = Math.Min(sourceSize, MaximumDiagnosticFileBytes);
            sourceReadLimit = Math.Min(sourceReadLimit, remainingTotalBytes);
            byte[] sourceBytes;
            try
            {
                sourceBytes = await ReadDiagnosticBytesAsync(path, sourceReadLimit, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                skipped.Add(CreateSkipped(fileName, NotFoundReason, sourceSize));
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                skipped.Add(CreateSkipped(fileName, NotFoundReason, sourceSize));
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                skipped.Add(CreateSkipped(fileName, UnavailableReason, sourceSize));
                continue;
            }
            catch (IOException)
            {
                skipped.Add(CreateSkipped(fileName, UnavailableReason, sourceSize));
                continue;
            }
            catch (SecurityException)
            {
                skipped.Add(CreateSkipped(fileName, UnavailableReason, sourceSize));
                continue;
            }

            if (!LooksLikeText(sourceBytes))
            {
                skipped.Add(CreateSkipped(fileName, NonTextReason, sourceSize));
                continue;
            }

            SupportPackageRedactionResult redaction = RedactDiagnosticText(Utf8NoBom.GetString(sourceBytes));
            byte[] redactedBytes = Utf8NoBom.GetBytes(redaction.Text);
            long outputLimit = Math.Min(MaximumDiagnosticFileBytes, remainingTotalBytes);
            bool truncated = sourceSize > sourceBytes.LongLength || redactedBytes.LongLength > outputLimit;
            if (redactedBytes.LongLength > outputLimit)
                redactedBytes = TrimUtf8(redactedBytes, checked((int)outputLimit));

            var metadata = new SupportPackageDiagnostic(
                fileName,
                sourceSize,
                redactedBytes.LongLength,
                truncated,
                null,
                redaction.RedactionCounts);
            collected.Add(new CollectedDiagnostic(metadata, redactedBytes));
            included.Add(metadata);
            remainingTotalBytes -= redactedBytes.LongLength;
        }

        return (collected.AsReadOnly(), included.AsReadOnly(), skipped.AsReadOnly());
    }

    private static async Task<byte[]> ReadDiagnosticBytesAsync(
        string path,
        long byteCount,
        CancellationToken cancellationToken)
    {
        if (byteCount <= 0) return Array.Empty<byte>();
        byte[] bytes = new byte[checked((int)byteCount)];
        int offset = 0;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            ReadBufferSize,
            FileOptions.SequentialScan | FileOptions.Asynchronous);
        while (offset < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(offset, bytes.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }

        if (offset == bytes.Length) return bytes;
        Array.Resize(ref bytes, offset);
        return bytes;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return true;
        int controlBytes = 0;
        foreach (byte value in bytes)
        {
            if (value == 0) return false;
            if (value < 0x09 || value > 0x0D && value < 0x20) controlBytes++;
        }

        // A named .log file containing a binary payload is not a diagnostic we
        // should copy. A small number of control characters can occur in a
        // legitimate trace, so use a conservative one-percent threshold.
        return controlBytes <= Math.Max(8, bytes.Length / 100);
    }

    private static byte[] TrimUtf8(byte[] bytes, int maximumBytes)
    {
        if (bytes.Length <= maximumBytes) return bytes;
        if (maximumBytes <= 0) return Array.Empty<byte>();

        int length = maximumBytes;
        int sequenceStart = length - 1;
        while (sequenceStart > 0 && (bytes[sequenceStart] & 0xC0) == 0x80) sequenceStart--;
        byte leading = bytes[sequenceStart];
        int expectedLength = leading switch
        {
            >= 0xF0 and <= 0xF7 => 4,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xC0 and <= 0xDF => 2,
            _ => 1,
        };
        if ((leading & 0xC0) == 0x80 || expectedLength > length - sequenceStart)
            length = sequenceStart;

        if (length <= 0) return Array.Empty<byte>();
        var result = new byte[length];
        Buffer.BlockCopy(bytes, 0, result, 0, length);
        return result;
    }

    private static byte[] CreateManifest(
        IReadOnlyList<SupportPackageDiagnostic> included,
        IReadOnlyList<SupportPackageDiagnostic> skipped)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", ManifestSchemaVersion);
            writer.WriteString("application", "FATX Bridge");
            writer.WriteString("applicationVersion", GetApplicationVersion());
            writer.WriteString("assemblyVersion", typeof(SupportPackageExporter).Assembly.GetName().Version?.ToString());

            writer.WriteStartObject("runtime");
            writer.WriteString("framework", RuntimeInformation.FrameworkDescription);
            writer.WriteString("runtimeVersion", Environment.Version.ToString());
            writer.WriteString("processArchitecture", RuntimeInformation.ProcessArchitecture.ToString());
            writer.WriteBoolean("is64BitProcess", Environment.Is64BitProcess);
            writer.WriteEndObject();

            writer.WriteStartObject("windows");
            writer.WriteString("description", RuntimeInformation.OSDescription);
            writer.WriteString("version", Environment.OSVersion.VersionString);
            writer.WriteString("architecture", RuntimeInformation.OSArchitecture.ToString());
            writer.WriteBoolean("is64Bit", Environment.Is64BitOperatingSystem);
            writer.WriteEndObject();

            writer.WriteStartObject("export");
            writer.WriteString("diagnosticsDirectory", "%LOCALAPPDATA%\\FatxBridge");
            writer.WriteString("manifestEntry", ManifestEntryName);
            writer.WriteString("diagnosticEntryDirectory", DiagnosticsEntryDirectory);
            writer.WriteBoolean("networkAccess", false);
            writer.WriteBoolean("destinationPathIncluded", false);
            writer.WriteNumber("maximumDiagnosticFileBytes", MaximumDiagnosticFileBytes);
            writer.WriteNumber("maximumTotalDiagnosticBytes", MaximumTotalDiagnosticBytes);
            writer.WriteString("oversizedDiagnosticPolicy", "include-redacted-prefix");
            writer.WriteStartArray("allowlistedDiagnosticNames");
            foreach (string fileName in AllowlistedNames) writer.WriteStringValue(fileName);
            writer.WriteEndArray();

            writer.WriteStartArray("redactions");
            foreach (string redaction in new[]
                     {
                         "local-app-data-path",
                         "app-data-path",
                         "temp-path",
                         "user-profile-path",
                         "mount-path",
                         "physical-drive-number",
                         "pipe-id",
                         "guid",
                         "serial-number",
                     })
            {
                writer.WriteStringValue(redaction);
            }
            writer.WriteEndArray();

            writer.WriteStartObject("diagnostics");
            WriteDiagnosticArray(writer, "included", included, includeEntryName: true);
            WriteDiagnosticArray(writer, "skipped", skipped, includeEntryName: false);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
        }

        return buffer.ToArray();
    }

    private static void WriteDiagnosticArray(
        Utf8JsonWriter writer,
        string propertyName,
        IReadOnlyList<SupportPackageDiagnostic> diagnostics,
        bool includeEntryName)
    {
        writer.WriteStartArray(propertyName);
        foreach (SupportPackageDiagnostic diagnostic in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("name", diagnostic.FileName);
            if (includeEntryName)
                writer.WriteString("entry", DiagnosticsEntryDirectory + diagnostic.FileName);
            if (diagnostic.SourceSizeBytes is long sourceSize)
                writer.WriteNumber("sourceSizeBytes", sourceSize);
            else
                writer.WriteNull("sourceSizeBytes");
            writer.WriteNumber("exportedSizeBytes", diagnostic.ExportedSizeBytes);
            writer.WriteBoolean("truncated", diagnostic.IsTruncated);
            if (diagnostic.SkipReason is not null)
                writer.WriteString("reason", diagnostic.SkipReason);
            writer.WriteStartObject("redactions");
            foreach ((string kind, int count) in diagnostic.Redactions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                writer.WriteNumber(kind, count);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteZipEntry(
        ZipArchive archive,
        string entryName,
        byte[] content,
        CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = ZipEpoch;
        entry.ExternalAttributes = 0;
        using Stream stream = entry.Open();
        cancellationToken.ThrowIfCancellationRequested();
        stream.Write(content, 0, content.Length);
    }

    private static string GetApplicationVersion()
    {
        Assembly assembly = typeof(SupportPackageExporter).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static SupportPackageDiagnostic CreateSkipped(string fileName, string reason, long? sourceSize)
        => new(fileName, sourceSize, 0, false, reason,
            ToReadOnlyCounts(new Dictionary<string, int>(StringComparer.Ordinal)));

    private static DirectoryState GetDirectoryState(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (!attributes.HasFlag(FileAttributes.Directory)) return DirectoryState.NotDirectory;
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) return DirectoryState.ReparsePoint;
            return DirectoryState.Available;
        }
        catch (FileNotFoundException) { return DirectoryState.Missing; }
        catch (DirectoryNotFoundException) { return DirectoryState.Missing; }
        catch (UnauthorizedAccessException) { return DirectoryState.Unavailable; }
        catch (IOException) { return DirectoryState.Unavailable; }
        catch (SecurityException) { return DirectoryState.Unavailable; }
    }

    private static void RejectDiagnosticDestination(string destinationPath, string diagnosticsDirectory)
    {
        foreach (string name in AllowlistedNames)
        {
            string diagnosticPath = Path.GetFullPath(Path.Combine(diagnosticsDirectory, name));
            if (string.Equals(destinationPath, diagnosticPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The support package destination cannot replace an allowlisted diagnostic.",
                    nameof(destinationPath));
        }
    }

    private static void EnsureLocalPath(string path)
    {
        if (IsUncPath(path)) throw new NotSupportedException(NonLocalDestinationMessage);
    }

    private static bool IsUncPath(string path)
        => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase);

    private static void AddPathReplacement(
        List<(string Path, string Replacement, string Kind)> replacements,
        string path,
        string replacement,
        string kind)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length < 4) return;
        replacements.Add((trimmed, replacement, kind));
    }

    private static string ReplacePathLiteral(
        string input,
        string path,
        string replacement,
        string kind,
        IDictionary<string, int> counts)
    {
        string value = input;
        foreach (string variant in new[] { path, path.Replace('\\', '/') }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string pattern = $"(?<![\\p{{L}}\\p{{N}}]){Regex.Escape(variant)}";
            value = Regex.Replace(value, pattern, match =>
            {
                int end = match.Index + match.Length;
                if (end < value.Length && (char.IsLetterOrDigit(value[end]) || value[end] == '_'))
                    return match.Value;
                Increment(counts, kind);
                return replacement;
            }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return value;
    }

    private static string ReplaceLiteral(
        string input,
        string literal,
        string replacement,
        string kind,
        IDictionary<string, int> counts)
    {
        return Regex.Replace(input, Regex.Escape(literal), _ =>
        {
            Increment(counts, kind);
            return replacement;
        }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ReplaceRegex(
        string input,
        Regex regex,
        string replacement,
        string kind,
        IDictionary<string, int> counts)
        => ReplaceRegex(input, regex, _ => replacement, kind, counts);

    private static string ReplaceRegex(
        string input,
        Regex regex,
        MatchEvaluator replacement,
        string kind,
        IDictionary<string, int> counts)
    {
        return regex.Replace(input, match =>
        {
            Increment(counts, kind);
            return replacement(match);
        });
    }

    private static void Increment(IDictionary<string, int> counts, string kind)
        => counts[kind] = counts.TryGetValue(kind, out int count) ? count + 1 : 1;

    private static System.Collections.ObjectModel.ReadOnlyDictionary<string, int> ToReadOnlyCounts(
        IReadOnlyDictionary<string, int> counts)
        => new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(
            new Dictionary<string, int>(counts, StringComparer.Ordinal));

    private sealed record CollectedDiagnostic(SupportPackageDiagnostic Metadata, byte[] Content);

    private enum DirectoryState
    {
        Missing,
        Available,
        ReparsePoint,
        NotDirectory,
        Unavailable,
    }
}

/// <summary>Redaction output and counts recorded in the support manifest.</summary>
public sealed record SupportPackageRedactionResult(
    string Text,
    IReadOnlyDictionary<string, int> RedactionCounts)
{
    public int TotalRedactions => RedactionCounts.Values.Sum();
}

/// <summary>Metadata for one included or skipped allowlisted diagnostic.</summary>
public sealed record SupportPackageDiagnostic(
    string FileName,
    long? SourceSizeBytes,
    long ExportedSizeBytes,
    bool IsTruncated,
    string? SkipReason,
    IReadOnlyDictionary<string, int> Redactions)
{
    public bool Included => SkipReason is null;
    public string? Reason => SkipReason;
    public long IncludedSizeBytes => ExportedSizeBytes;
    public bool Truncated => IsTruncated;
}

/// <summary>Result returned after a support package is atomically published.</summary>
public sealed class SupportPackageExportResult
{
    public SupportPackageExportResult(
        string destinationPath,
        int fileCount,
        long packageSizeBytes,
        IReadOnlyList<SupportPackageDiagnostic> includedDiagnostics,
        IReadOnlyList<SupportPackageDiagnostic> skippedDiagnostics)
    {
        DestinationPath = destinationPath;
        FileCount = fileCount;
        PackageSizeBytes = packageSizeBytes;
        IncludedDiagnostics = includedDiagnostics;
        SkippedDiagnostics = skippedDiagnostics;
    }

    public string DestinationPath { get; }
    public int FileCount { get; }
    public int IncludedFileCount => FileCount;
    public long PackageSizeBytes { get; }
    public long SizeBytes => PackageSizeBytes;
    public IReadOnlyList<SupportPackageDiagnostic> IncludedDiagnostics { get; }
    public IReadOnlyList<SupportPackageDiagnostic> SkippedDiagnostics { get; }
    public int TotalRedactions => IncludedDiagnostics.Sum(diagnostic => diagnostic.Redactions.Values.Sum());
}
