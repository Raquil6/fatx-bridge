using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace FatxBridge.Windows;

/// <summary>How much of the media state Windows was able to report.</summary>
public enum DeviceMediaState
{
    Unknown,
    Present,
    NoMedia,
}

/// <summary>Health state reported by the Windows storage class driver.</summary>
public enum DeviceHealthStatus
{
    Unknown,
    Healthy,
    PredictingFailure,
}

/// <summary>
/// Best-effort physical-device information returned by Windows storage IOCTLs.
/// Null fields mean that the device or its bridge did not expose that value.
/// SerialNumber is retained for callers that need to identify a device, but is
/// intentionally omitted from the drive-list display.
/// </summary>
public sealed record DeviceInformation(
    string? Model = null,
    string? SerialNumber = null,
    string? FirmwareRevision = null,
    string? BusType = null,
    int? LogicalSectorSize = null,
    int? PhysicalSectorSize = null,
    bool? IsRemovable = null,
    bool? IsHotPlug = null,
    DeviceMediaState MediaState = DeviceMediaState.Unknown,
    DeviceHealthStatus HealthStatus = DeviceHealthStatus.Unknown,
    bool? SmartSupported = null)
{
    public static DeviceInformation Unknown { get; } = new();

    /// <summary>
    /// Builds a concise list-friendly summary without including the raw serial
    /// number or other opaque identifiers.
    /// </summary>
    public string DisplaySummary
    {
        get
        {
            var parts = new List<string>();
            AddIfPresent(parts, Model);
            AddIfPresent(parts, BusType);

            if (LogicalSectorSize is { } logical && PhysicalSectorSize is { } physical)
            {
                parts.Add(logical == physical
                    ? $"{logical}-byte sectors"
                    : $"{logical}-byte logical / {physical}-byte physical sectors");
            }
            else if (LogicalSectorSize is { } logicalOnly)
            {
                parts.Add($"{logicalOnly}-byte logical sectors");
            }
            else if (PhysicalSectorSize is { } physicalOnly)
            {
                parts.Add($"{physicalOnly}-byte physical sectors");
            }

            if (FirmwareRevision is { Length: > 0 } firmware)
                parts.Add($"firmware {firmware}");

            if (IsRemovable is true)
                parts.Add("removable");
            else if (IsRemovable is false)
                parts.Add("fixed");

            if (MediaState == DeviceMediaState.Present)
                parts.Add("media present");
            else if (MediaState == DeviceMediaState.NoMedia)
                parts.Add("no media");

            if (HealthStatus == DeviceHealthStatus.Healthy)
                parts.Add("health OK");
            else if (HealthStatus == DeviceHealthStatus.PredictingFailure)
                parts.Add("health warning");

            return string.Join(" • ", parts);
        }
    }

    private static void AddIfPresent(List<string> parts, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) parts.Add(value);
    }
}

/// <summary>
/// Reads standard Windows storage descriptors. Every query is optional: a
/// bridge may reject one or all of these IOCTLs without affecting FATX scan.
/// </summary>
internal static class DeviceInformationReader
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlStorageGetHotplugInfo = 0x002D0C14;
    private const uint IoctlStorageCheckVerify2 = 0x002D0800;
    private const uint IoctlStoragePredictFailure = 0x002D1100;

    private const uint StorageDeviceProperty = 0;
    private const uint StorageAccessAlignmentProperty = 6;
    private const uint PropertyStandardQuery = 0;

    private const int StoragePropertyQuerySize = 12;
    private const int StorageDescriptorHeaderSize = 8;
    private const int StorageDeviceDescriptorMinimumSize = 33;
    private const int StorageAccessAlignmentDescriptorSize = 28;
    private const int StorageHotplugInfoSize = 8;
    private const int StoragePredictFailureMinimumSize = 4;
    private const int DefaultPropertyBufferSize = 4096;
    private const int MaximumPropertyBufferSize = 64 * 1024;
    private const int MaximumDescriptorStringBytes = 4096;
    private const int MaximumSectorSize = 1024 * 1024;

    private static readonly Encoding DescriptorEncoding = Encoding.ASCII;

    public static DeviceInformation Read(SafeFileHandle handle, Action<string>? diagnostic = null)
    {
        DeviceInformation? descriptor = null;
        DeviceInformation? alignment = null;
        DeviceInformation? hotplug = null;
        DeviceInformation? media = null;
        DeviceInformation? health = null;

        try { descriptor = ReadDeviceDescriptor(handle, diagnostic); }
        catch (Exception exception) when (IsBestEffortFailure(exception))
        {
            diagnostic?.Invoke($"device descriptor query was unavailable: {exception.GetType().Name}.");
        }

        try { alignment = ReadAlignmentDescriptor(handle, diagnostic); }
        catch (Exception exception) when (IsBestEffortFailure(exception))
        {
            diagnostic?.Invoke($"alignment descriptor query was unavailable: {exception.GetType().Name}.");
        }

        try { hotplug = ReadHotplugInfo(handle, diagnostic); }
        catch (Exception exception) when (IsBestEffortFailure(exception))
        {
            diagnostic?.Invoke($"hotplug query was unavailable: {exception.GetType().Name}.");
        }

        try { media = ReadMediaState(handle, diagnostic); }
        catch (Exception exception) when (IsBestEffortFailure(exception))
        {
            diagnostic?.Invoke($"media-state query was unavailable: {exception.GetType().Name}.");
        }

        try { health = ReadHealth(handle, diagnostic); }
        catch (Exception exception) when (IsBestEffortFailure(exception))
        {
            diagnostic?.Invoke($"health query was unavailable: {exception.GetType().Name}.");
        }

        return new DeviceInformation(
            Model: descriptor?.Model,
            SerialNumber: descriptor?.SerialNumber,
            FirmwareRevision: descriptor?.FirmwareRevision,
            BusType: descriptor?.BusType,
            LogicalSectorSize: alignment?.LogicalSectorSize,
            PhysicalSectorSize: alignment?.PhysicalSectorSize,
            IsRemovable: hotplug?.IsRemovable ?? descriptor?.IsRemovable,
            IsHotPlug: hotplug?.IsHotPlug,
            MediaState: media?.MediaState ?? DeviceMediaState.Unknown,
            HealthStatus: health?.HealthStatus ?? DeviceHealthStatus.Unknown,
            SmartSupported: health?.SmartSupported);
    }

    private static DeviceInformation? ReadDeviceDescriptor(SafeFileHandle handle, Action<string>? diagnostic)
    {
        StoragePropertyResult? result = QueryStorageProperty(handle, StorageDeviceProperty, diagnostic);
        if (result is null) return null;
        byte[] buffer = result.Buffer;

        int available = result.BytesReturned;
        if (available < StorageDeviceDescriptorMinimumSize)
        {
            diagnostic?.Invoke("storage device descriptor was shorter than its documented header.");
            return null;
        }

        int descriptorSize = GetBoundedDescriptorLength(buffer, available);
        if (descriptorSize < StorageDeviceDescriptorMinimumSize)
        {
            diagnostic?.Invoke("storage device descriptor reported an invalid size.");
            return null;
        }

        string? vendor = ReadDescriptorString(buffer, descriptorSize, ReadUInt32(buffer, 12));
        string? product = ReadDescriptorString(buffer, descriptorSize, ReadUInt32(buffer, 16));
        string? revision = ReadDescriptorString(buffer, descriptorSize, ReadUInt32(buffer, 20));
        string? serial = ReadDescriptorString(buffer, descriptorSize, ReadUInt32(buffer, 24));
        byte busType = buffer[28];

        string? model = JoinModel(vendor, product);
        return new DeviceInformation(
            Model: model,
            SerialNumber: serial,
            FirmwareRevision: revision,
            BusType: BusTypeName(busType),
            IsRemovable: buffer[10] != 0);
    }

    private static DeviceInformation? ReadAlignmentDescriptor(SafeFileHandle handle, Action<string>? diagnostic)
    {
        StoragePropertyResult? result = QueryStorageProperty(handle, StorageAccessAlignmentProperty, diagnostic);
        if (result is null) return null;
        byte[] buffer = result.Buffer;

        int available = result.BytesReturned;
        if (available < StorageAccessAlignmentDescriptorSize)
        {
            diagnostic?.Invoke("storage alignment descriptor was shorter than its documented size.");
            return null;
        }

        int descriptorSize = GetBoundedDescriptorLength(buffer, available);
        if (descriptorSize < StorageAccessAlignmentDescriptorSize)
        {
            diagnostic?.Invoke("storage alignment descriptor reported an invalid size.");
            return null;
        }

        int? logical = ReadSectorSize(buffer, descriptorSize, 16);
        int? physical = ReadSectorSize(buffer, descriptorSize, 20);
        if (logical is null && physical is null) return null;

        return new DeviceInformation(LogicalSectorSize: logical, PhysicalSectorSize: physical);
    }

    private static DeviceInformation? ReadHotplugInfo(SafeFileHandle handle, Action<string>? diagnostic)
    {
        var buffer = new byte[StorageHotplugInfoSize];
        if (!DeviceIoControl(handle, IoctlStorageGetHotplugInfo, null, 0,
                buffer, (uint)buffer.Length, out uint bytesReturned, IntPtr.Zero))
        {
            LogIoctlFailure(diagnostic, "hotplug info", Marshal.GetLastWin32Error());
            return null;
        }

        int available = Math.Min(buffer.Length, ToInt32(bytesReturned));
        if (available < StorageHotplugInfoSize)
        {
            diagnostic?.Invoke("hotplug info returned a truncated structure.");
            return null;
        }

        uint structureSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4));
        if (structureSize != 0 && (structureSize < StorageHotplugInfoSize || structureSize > available))
        {
            diagnostic?.Invoke("hotplug info reported an invalid size.");
            return null;
        }

        return new DeviceInformation(IsRemovable: buffer[4] != 0, IsHotPlug: buffer[6] != 0);
    }

    private static DeviceInformation? ReadMediaState(SafeFileHandle handle, Action<string>? diagnostic)
    {
        if (DeviceIoControl(handle, IoctlStorageCheckVerify2, null, 0, null, 0,
                out _, IntPtr.Zero))
            return new DeviceInformation(MediaState: DeviceMediaState.Present);

        int error = Marshal.GetLastWin32Error();
        if (error is 21 or 1112)
            return new DeviceInformation(MediaState: DeviceMediaState.NoMedia);

        LogIoctlFailure(diagnostic, "media verification", error);
        return null;
    }

    private static DeviceInformation? ReadHealth(SafeFileHandle handle, Action<string>? diagnostic)
    {
        // STORAGE_PREDICT_FAILURE is the Windows storage-class driver's
        // read-only SMART/failure-prediction result. VendorSpecific is not
        // retained or logged, both to avoid leaking opaque identifiers and
        // because FATX detection does not depend on it.
        var buffer = new byte[StoragePredictFailureMinimumSize + 512];
        if (!DeviceIoControl(handle, IoctlStoragePredictFailure, null, 0,
                buffer, (uint)buffer.Length, out uint bytesReturned, IntPtr.Zero))
        {
            LogIoctlFailure(diagnostic, "health prediction", Marshal.GetLastWin32Error());
            return null;
        }

        int available = Math.Min(buffer.Length, ToInt32(bytesReturned));
        if (available < StoragePredictFailureMinimumSize)
        {
            diagnostic?.Invoke("health prediction returned a truncated structure.");
            return null;
        }

        uint predictsFailure = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(0, 4));
        return new DeviceInformation(
            HealthStatus: predictsFailure == 0
                ? DeviceHealthStatus.Healthy
                : DeviceHealthStatus.PredictingFailure,
            SmartSupported: true);
    }

    private static StoragePropertyResult? QueryStorageProperty(
        SafeFileHandle handle, uint propertyId, Action<string>? diagnostic)
    {
        var query = new byte[StoragePropertyQuerySize];
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(0, 4), propertyId);
        BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(4, 4), PropertyStandardQuery);

        // Microsoft documents a STORAGE_DESCRIPTOR_HEADER first query so a
        // variable-length descriptor can be sized from its returned Size.
        var header = new byte[StorageDescriptorHeaderSize];
        bool headerSucceeded = DeviceIoControl(handle, IoctlStorageQueryProperty,
            query, (uint)query.Length, header, (uint)header.Length,
            out uint headerBytesReturned, IntPtr.Zero);
        uint reportedSize = headerBytesReturned >= StorageDescriptorHeaderSize
            ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4))
            : 0;

        int outputSize = GetQueryBufferSize(reportedSize);
        if (!headerSucceeded && reportedSize == 0)
            LogIoctlFailure(diagnostic, propertyId == StorageDeviceProperty
                ? "device descriptor header" : "alignment descriptor header", Marshal.GetLastWin32Error());

        var output = new byte[outputSize];
        if (DeviceIoControl(handle, IoctlStorageQueryProperty,
                query, (uint)query.Length, output, (uint)output.Length,
                out uint bytesReturned, IntPtr.Zero))
        {
            int available = Math.Min(output.Length, ToInt32(bytesReturned));
            if (available < StorageDescriptorHeaderSize)
            {
                diagnostic?.Invoke(propertyId == StorageDeviceProperty
                    ? "device descriptor query returned no usable bytes."
                    : "alignment descriptor query returned no usable bytes.");
                return null;
            }

            return new StoragePropertyResult(output, available);
        }

        int error = Marshal.GetLastWin32Error();
        LogIoctlFailure(diagnostic, propertyId == StorageDeviceProperty
            ? "device descriptor" : "alignment descriptor", error);

        // A bridge can report a size smaller than the descriptor it actually
        // emits. One bounded retry avoids rejecting such a device while still
        // preventing an untrusted driver from requesting an unbounded buffer.
        if (outputSize < MaximumPropertyBufferSize && error == 122)
        {
            output = new byte[MaximumPropertyBufferSize];
            if (DeviceIoControl(handle, IoctlStorageQueryProperty,
                    query, (uint)query.Length, output, (uint)output.Length,
                    out uint retryBytesReturned, IntPtr.Zero))
            {
                int available = Math.Min(output.Length, ToInt32(retryBytesReturned));
                return available >= StorageDescriptorHeaderSize
                    ? new StoragePropertyResult(output, available)
                    : null;
            }
        }

        return null;
    }

    private static int GetBoundedDescriptorLength(byte[] buffer, int available)
    {
        if (available < StorageDescriptorHeaderSize) return 0;
        uint reportedSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4, 4));
        if (reportedSize < StorageDescriptorHeaderSize) return 0;
        return (int)Math.Min(reportedSize, (uint)available);
    }

    private static int? ReadSectorSize(byte[] buffer, int available, int offset)
    {
        if (offset < 0 || offset > available - 4) return null;
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
        return value is > 0 and <= MaximumSectorSize ? (int)value : null;
    }

    private static string? ReadDescriptorString(byte[] buffer, int available, uint offset)
    {
        if (offset == 0 || offset >= available) return null;
        int start = checked((int)offset);
        int maximum = Math.Min(available - start, MaximumDescriptorStringBytes);
        int length = 0;
        while (length < maximum && buffer[start + length] != 0) length++;
        if (length == 0) return null;

        string value = DescriptorEncoding.GetString(buffer, start, length).Trim();
        if (value.Any(char.IsControl))
            value = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return value.Length == 0 ? null : value;
    }

    private static string? JoinModel(string? vendor, string? product)
    {
        if (string.IsNullOrWhiteSpace(vendor)) return product;
        if (string.IsNullOrWhiteSpace(product)) return vendor;
        return $"{vendor} {product}";
    }

    private static string? BusTypeName(byte value) => value switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "virtual",
        15 => "file-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => null,
    };

    private static int GetQueryBufferSize(uint reportedSize)
    {
        if (reportedSize < StorageDescriptorHeaderSize || reportedSize > MaximumPropertyBufferSize)
            return DefaultPropertyBufferSize;
        return (int)reportedSize;
    }

    private static int ToInt32(uint value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static uint ReadUInt32(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));

    private static void LogIoctlFailure(Action<string>? diagnostic, string operation, int error)
    {
        if (diagnostic is not null)
            diagnostic($"{operation} query was unavailable (Win32 error {error}).");
    }

    private static bool IsBestEffortFailure(Exception exception) => exception is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private sealed record StoragePropertyResult(byte[] Buffer, int BytesReturned);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        [In] byte[]? inputBuffer,
        uint inputBufferSize,
        [Out] byte[]? outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
