using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FatxBridge.Core;

public enum Xbox360UsbConfigurationType
{
    Unknown,
    Type1ConsoleConfigured,
    Type2MicrosoftProvisioned,
}

public enum Xbox360UsbConfigurationValidationState
{
    Unavailable,
    Truncated,
    Invalid,
    StructurallyValid,
}

/// <summary>
/// Read-only metadata from the first 0x400 bytes of Data0000. The
/// <see cref="DeviceIdFingerprint"/> is a SHA-256 fingerprint, never the raw
/// 0x14-byte device identifier. Signature verification is deliberately not
/// performed by this read-only layer.
/// </summary>
public sealed record Xbox360UsbConfiguration(
    Xbox360UsbConfigurationType CertificateType,
    uint? CertificateSize,
    ulong? DeviceCapacityBytes,
    ushort? ReadSpeedKilobytesPerSecond,
    ushort? WriteSpeedKilobytesPerSecond,
    string? DeviceIdFingerprint,
    Xbox360UsbConfigurationValidationState ValidationState,
    int BytesRead)
{
    public Xbox360UsbConfigurationType Type => CertificateType;
    public ulong? DeclaredCapacityBytes => DeviceCapacityBytes;
    public ushort? ReadSpeedKbps => ReadSpeedKilobytesPerSecond;
    public ushort? WriteSpeedKbps => WriteSpeedKilobytesPerSecond;
    public string? DeviceIdSha256 => DeviceIdFingerprint;
    public bool IsBestEffort => ValidationState != Xbox360UsbConfigurationValidationState.StructurallyValid;
    public bool SignatureWasVerified { get; init; }
}

/// <summary>Documented legacy FATX range within the concatenated USB stream.</summary>
public sealed record Xbox360UsbPartitionLayout(string Name, long Offset, long? FixedLength);

public sealed record Xbox360UsbDiagnostic(string Stage, string Message, string? CandidateName = null);

/// <summary>
/// Read-only view of the legacy Xbox 360 USB Data0000 container. The supplied
/// segment streams are owned by the segmented wrapper unless <paramref
/// name="leaveOpen"/> is true.
/// </summary>
public sealed class Xbox360UsbContainer : IDisposable
{
    public const int ConfigurationSize = 0x400;
    public const long SystemCacheOffset = 0x08000400;
    public const long SystemCacheLength = 0x047FF000;
    public const long SystemExtendedOffset = 0x08115200;
    public const long SystemExtendedLength = 0x08000000;
    public const long SystemExtended2Offset = 0x12000400;
    public const long SystemExtended2Length = 0x0DFFFC00;
    public const long DataOffset = 0x20000000;

    private static readonly Xbox360UsbPartitionLayout[] LegacyLayouts =
    [
        new("System Cache", SystemCacheOffset, SystemCacheLength),
        new("SysExt", SystemExtendedOffset, SystemExtendedLength),
        new("SysExt2", SystemExtended2Offset, SystemExtended2Length),
        new("Data", DataOffset, null),
    ];

    private readonly SegmentedReadOnlyStream stream;
    private readonly bool readOnly = true;
    private bool disposed;

    public Xbox360UsbContainer(
        IReadOnlyList<Stream> segments,
        bool leaveOpen = false,
        Action<string>? diagnostic = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        stream = new SegmentedReadOnlyStream(segments, leaveOpen);
        try
        {
            var diagnostics = new List<Xbox360UsbDiagnostic>();
            Configuration = ReadConfiguration(stream, diagnostics, diagnostic);
            Partitions = ProbePartitions(stream, diagnostics, diagnostic);
            Diagnostics = diagnostics.ToArray();
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static IReadOnlyList<Xbox360UsbPartitionLayout> DocumentedPartitionLayouts => LegacyLayouts;

    public SegmentedReadOnlyStream Stream
    {
        get
        {
            EnsureNotDisposed();
            return stream;
        }
    }

    public SegmentedReadOnlyStream ReadOnlyStream => Stream;
    public long Length => Stream.Length;
    public bool IsReadOnly => readOnly;
    public Xbox360UsbConfiguration Configuration { get; }
    public IReadOnlyList<FatxPartitionCandidate> Partitions { get; }
    public IReadOnlyList<FatxPartitionCandidate> ValidatedPartitions => Partitions;
    public IReadOnlyList<Xbox360UsbDiagnostic> Diagnostics { get; }

    public static Xbox360UsbConfiguration ParseConfiguration(ReadOnlySpan<byte> bytes)
    {
        int length = Math.Min(bytes.Length, ConfigurationSize);
        ReadOnlySpan<byte> config = bytes[..length];
        uint? certificateSize = length >= 0x240
            ? BinaryPrimitives.ReadUInt32BigEndian(config.Slice(0x23C, 4))
            : null;
        Xbox360UsbConfigurationType type = certificateSize switch
        {
            0x228 => Xbox360UsbConfigurationType.Type1ConsoleConfigured,
            0x100 => Xbox360UsbConfigurationType.Type2MicrosoftProvisioned,
            _ => Xbox360UsbConfigurationType.Unknown,
        };

        ulong? capacity = length >= 0x248
            ? BinaryPrimitives.ReadUInt64BigEndian(config.Slice(0x240, 8))
            : null;
        ushort? readSpeed = length >= 0x24A
            ? BinaryPrimitives.ReadUInt16BigEndian(config.Slice(0x248, 2))
            : null;
        ushort? writeSpeed = length >= 0x24C
            ? BinaryPrimitives.ReadUInt16BigEndian(config.Slice(0x24A, 2))
            : null;
        string? fingerprint = length >= 0x23C
            ? Convert.ToHexString(SHA256.HashData(config.Slice(0x228, 0x14)))
            : null;

        Xbox360UsbConfigurationValidationState state = length == 0
            ? Xbox360UsbConfigurationValidationState.Unavailable
            : length < ConfigurationSize
                ? Xbox360UsbConfigurationValidationState.Truncated
                : type == Xbox360UsbConfigurationType.Unknown
                    ? Xbox360UsbConfigurationValidationState.Invalid
                    : Xbox360UsbConfigurationValidationState.StructurallyValid;
        return new Xbox360UsbConfiguration(type, certificateSize, capacity, readSpeed, writeSpeed,
            fingerprint, state, length);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stream.Dispose();
    }

    private static Xbox360UsbConfiguration ReadConfiguration(
        SegmentedReadOnlyStream source,
        ICollection<Xbox360UsbDiagnostic> diagnostics,
        Action<string>? diagnostic)
    {
        byte[] buffer = new byte[ConfigurationSize];
        int total = 0;
        try
        {
            source.Position = 0;
            while (total < buffer.Length)
            {
                int read = source.Read(buffer, total, buffer.Length - total);
                if (read == 0) break;
                total += read;
            }
            Report(diagnostics, diagnostic, "configuration",
                $"USB configuration read {total}/0x{ConfigurationSize:X} bytes.");
        }
        catch (Exception exception) when (IsBestEffortReadFailure(exception))
        {
            Report(diagnostics, diagnostic, "configuration",
                $"USB configuration read stopped after {total}/0x{ConfigurationSize:X} bytes: {exception.Message}");
        }

        return ParseConfiguration(buffer.AsSpan(0, total));
    }

    private static FatxPartitionCandidate[] ProbePartitions(
        SegmentedReadOnlyStream source,
        ICollection<Xbox360UsbDiagnostic> diagnostics,
        Action<string>? diagnostic)
    {
        var found = new List<FatxPartitionCandidate>();
        long length = source.Length;
        foreach (Xbox360UsbPartitionLayout layout in LegacyLayouts)
        {
            if (layout.Offset < 0 || layout.Offset >= length)
            {
                Report(diagnostics, diagnostic, "layout",
                    $"{layout.Name}: skipped because offset 0x{layout.Offset:X} is outside the {length}-byte stream.", layout.Name);
                continue;
            }

            long partitionLength;
            try
            {
                partitionLength = layout.FixedLength ?? checked(length - layout.Offset);
            }
            catch (OverflowException)
            {
                Report(diagnostics, diagnostic, "layout",
                    $"{layout.Name}: skipped because its range overflowed.", layout.Name);
                continue;
            }
            if (partitionLength < 0x1000 || partitionLength > length - layout.Offset)
            {
                Report(diagnostics, diagnostic, "layout",
                    $"{layout.Name}: skipped because 0x{layout.Offset:X}+0x{partitionLength:X} is outside the stream.", layout.Name);
                continue;
            }

            try
            {
                FatxVolume volume = FatxVolume.Open(source, layout.Offset, partitionLength, length, 512);
                IReadOnlyList<FatxDirectoryEntry> root = volume.ListRootDirectory();
                found.Add(new FatxPartitionCandidate(layout.Name, layout.Offset, partitionLength,
                    volume.Metadata, SupportsWrite: false));
                Report(diagnostics, diagnostic, "probe",
                    $"{layout.Name}: accepted FATX {volume.Metadata.ByteOrder}/{volume.Metadata.AllocationTable} " +
                    $"partition at 0x{layout.Offset:X} with {root.Count} root entries; read-only.", layout.Name);
            }
            catch (Exception exception) when (IsBestEffortReadFailure(exception))
            {
                Report(diagnostics, diagnostic, "probe",
                    $"{layout.Name}: rejected during FATX header/root validation: {exception.Message}", layout.Name);
            }
        }

        return found.ToArray();
    }

    private static bool IsBestEffortReadFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        ArgumentException or
        InvalidOperationException or
        OverflowException or
        NotSupportedException;

    private static void Report(
        ICollection<Xbox360UsbDiagnostic> diagnostics,
        Action<string>? diagnostic,
        string stage,
        string message,
        string? candidateName = null)
    {
        diagnostics.Add(new Xbox360UsbDiagnostic(stage, message, candidateName));
        diagnostic?.Invoke(message);
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
