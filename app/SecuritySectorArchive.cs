using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using FatxBridge.Core;

namespace FatxBridge.Windows;

/// <summary>The result of publishing one validated Xbox 360 HDDSS payload to the local archive.</summary>
public sealed record SecuritySectorArchiveEntry(
    string Sha256,
    string ArchivePath,
    string ManifestPath,
    bool AlreadyExisted)
{
    /// <summary>Alias for callers that use file-path terminology.</summary>
    public string FilePath => ArchivePath;

    /// <summary>Alias for callers that use directory-entry terminology.</summary>
    public string Path => ArchivePath;

    /// <summary>Alias for callers that use fingerprint terminology.</summary>
    public string Fingerprint => Sha256;
}

/// <summary>
/// Stores validated Xbox 360 HDD security sectors in the current user's local application data.
/// The service accepts bytes or an inspection result; it never opens a disk or a source path.
/// </summary>
public sealed class SecuritySectorArchive
{
    private const int ManifestSchemaVersion = 1;
    private const string ManifestFormat = "Xbox360HddSecuritySector";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The default per-user archive location.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FatxBridge", "SecuritySectors");

    public SecuritySectorArchive(string? directory = null)
    {
        Directory = string.IsNullOrWhiteSpace(directory)
            ? DefaultDirectory
            : Path.GetFullPath(directory);
    }

    /// <summary>The directory used by this archive instance.</summary>
    public string Directory { get; }

    /// <summary>Alias for callers that use root-directory terminology.</summary>
    public string RootDirectory => Directory;

    /// <summary>
    /// Archives a core inspection result when, and only when, it is still structurally valid and
    /// present. Absent, invalid, unavailable, null, or tampered results are a no-op.
    /// </summary>
    public SecuritySectorArchiveEntry? Archive(Xbox360SecuritySectorResult? inspection)
    {
        if (inspection is null || !inspection.IsPresent || inspection.Bytes is null)
            return null;

        // Revalidate the caller-supplied bytes. A result owns a mutable byte[] for convenient
        // interop, so accepting its old status/hash without checking would permit an accidental
        // or malicious mutation to reach the archive.
        Xbox360SecuritySectorResult validated = Xbox360SecuritySector.Validate(inspection.Bytes);
        if (!validated.IsPresent || validated.Bytes is null)
            return null;

        return ArchiveValidatedBytes(validated.Bytes, validated.Sha256!);
    }

    /// <summary>Validates and archives exactly one complete 0xE00-byte HDDSS payload, if present.</summary>
    public SecuritySectorArchiveEntry? Archive(ReadOnlySpan<byte> payload)
    {
        Xbox360SecuritySectorResult validated = Xbox360SecuritySector.Validate(payload);
        return !validated.IsPresent || validated.Bytes is null
            ? null
            : ArchiveValidatedBytes(validated.Bytes, validated.Sha256!);
    }

    /// <summary>Validates and archives a read-only memory view of one complete HDDSS payload.</summary>
    public SecuritySectorArchiveEntry? Archive(ReadOnlyMemory<byte> payload) => Archive(payload.Span);

    /// <summary>Alias for <see cref="Archive(Xbox360SecuritySectorResult?)"/>.</summary>
    public SecuritySectorArchiveEntry? Store(Xbox360SecuritySectorResult? inspection) => Archive(inspection);

    private SecuritySectorArchiveEntry ArchiveValidatedBytes(byte[] bytes, string sha256)
    {
        DirectoryInfo directory = System.IO.Directory.CreateDirectory(Directory);
        string archivePath = Path.Combine(directory.FullName, sha256 + ".bin");
        string manifestPath = Path.Combine(directory.FullName, sha256 + ".json");

        bool payloadAlreadyExisted = HasMatchingPayload(archivePath, sha256);
        if (!payloadAlreadyExisted)
            WriteAtomically(archivePath, bytes);

        // Publish the manifest only after the payload has been durably written. The manifest
        // contains no source path, serial/model text, user name, or raw sector bytes.
        if (!HasMatchingManifest(manifestPath, sha256))
        {
            var manifest = new SecuritySectorManifest(
                ManifestSchemaVersion,
                ManifestFormat,
                sha256,
                Xbox360SecuritySector.PayloadSize,
                DateTimeOffset.UtcNow);
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            WriteAtomically(manifestPath, manifestBytes);
        }

        return new SecuritySectorArchiveEntry(sha256, archivePath, manifestPath, payloadAlreadyExisted);
    }

    private static bool HasMatchingPayload(string path, string expectedSha256)
    {
        try
        {
            FileInfo info = new(path);
            if (!info.Exists || info.Length != Xbox360SecuritySector.PayloadSize)
                return false;
            byte[] existing = File.ReadAllBytes(path);
            string actual = Convert.ToHexString(SHA256.HashData(existing));
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual), Convert.FromHexString(expectedSha256));
        }
        catch (Exception exception) when (IsArchiveReadFailure(exception))
        {
            return false;
        }
    }

    private static bool HasMatchingManifest(string path, string expectedSha256)
    {
        try
        {
            SecuritySectorManifest? manifest = JsonSerializer.Deserialize<SecuritySectorManifest>(
                File.ReadAllBytes(path), JsonOptions);
            return manifest is not null &&
                manifest.SchemaVersion == ManifestSchemaVersion &&
                string.Equals(manifest.Format, ManifestFormat, StringComparison.Ordinal) &&
                manifest.ByteLength == Xbox360SecuritySector.PayloadSize &&
                string.Equals(manifest.Sha256, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (IsArchiveReadFailure(exception) || exception is JsonException)
        {
            return false;
        }
    }

    private static void WriteAtomically(string path, ReadOnlySpan<byte> bytes)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            // The temporary file is in the same directory, so the publication move is atomic
            // with respect to readers of this archive on Windows/NTFS.
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // A failed cleanup must not mask the original archive error.
            }
            catch (UnauthorizedAccessException)
            {
                // A failed cleanup must not mask the original archive error.
            }
        }
    }

    private static bool IsArchiveReadFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        ObjectDisposedException or
        ArgumentException;

    private sealed record SecuritySectorManifest(
        int SchemaVersion,
        string Format,
        string Sha256,
        int ByteLength,
        DateTimeOffset ArchivedUtc);
}
