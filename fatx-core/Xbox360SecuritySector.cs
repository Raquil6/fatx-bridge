using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FatxBridge.Core;

/// <summary>Classification returned while inspecting the optional Xbox 360 HDD security-sector payload.</summary>
public enum Xbox360SecuritySectorState
{
    /// <summary>A bounded payload with the documented structure and populated authenticity fields was found.</summary>
    Present,

    /// <summary>The security-sector slot is blank, which is supported for securityless media.</summary>
    Absent,

    /// <summary>Bytes were available, but they did not satisfy the documented structure.</summary>
    Invalid,

    /// <summary>The payload could not be read completely, or the source could not be safely accessed.</summary>
    Unavailable,
}

/// <summary>A read-only inspection result for the optional Xbox 360 HDDSS payload.</summary>
public sealed record Xbox360SecuritySectorResult(
    Xbox360SecuritySectorState State,
    long Offset,
    int Length,
    byte[]? Bytes,
    string? Sha256,
    string? Reason)
{
    /// <summary>Compatibility alias for callers that use status terminology.</summary>
    public Xbox360SecuritySectorState Status => State;

    /// <summary>Whether this result contains a structurally validated HDDSS payload.</summary>
    public bool IsPresent => State == Xbox360SecuritySectorState.Present;

    /// <summary>Stable SHA-256 fingerprint of <see cref="Bytes"/> when the payload is present.</summary>
    public string? Fingerprint => Sha256;

    /// <summary>Declared MS logo length when the result contains a complete payload.</summary>
    public int? LogoSize
    {
        get
        {
            if (Bytes is null || Bytes.Length < Xbox360SecuritySector.LogoSizeOffset + Xbox360SecuritySector.LogoSizeLength)
                return null;
            return BinaryPrimitives.ReadInt32BigEndian(
                Bytes.AsSpan(Xbox360SecuritySector.LogoSizeOffset, Xbox360SecuritySector.LogoSizeLength));
        }
    }

    /// <summary>A defensive copy of the payload bytes, or an empty array when no payload was accepted.</summary>
    public byte[] GetBytes() => Bytes is null ? [] : (byte[])Bytes.Clone();
}

/// <summary>
/// Bounded, read-only inspection of the Xbox 360 HDD security-sector (HDDSS) payload.
///
/// The detector intentionally does not own or dispose the caller's stream. It reads the complete
/// seven-sector HDDSS payload and restores the stream position when the stream permits it.
/// </summary>
public static class Xbox360SecuritySector
{
    // Free60's FATX layout documents the retail HDD security sector at byte offset 0x2000
    // (logical sector 16), and identifies the sector-16 identity, digest, count, and signature:
    // https://free60.org/System-Software/Systems/FATX/
    // Eaton's kernel analysis independently describes SataDiskAuthenticateDevice reading sector 16:
    // https://eaton-works.com/2023/01/24/how-the-xbox-360-knows-if-your-hard-drive-is-genuine/
    // The open-source Hddhackr utility treats the conventional HDDSS backup as sectors 16-22,
    // i.e. seven 512-byte sectors (3584 / 0xE00 bytes): https://github.com/ErikAndren/Hddhackr
    public const long SectorOffset = 0x2000;

    /// <summary>Size of the sector-16 header containing the identity, digest, count, and signature.</summary>
    public const int SectorSize = 0x200;

    /// <summary>Size of the complete conventional HDDSS sectors-16-through-22 payload.</summary>
    public const int PayloadSize = 0xE00;

    /// <summary>Alias for <see cref="PayloadSize"/> using the HDDSS terminology.</summary>
    public const int HddssPayloadSize = PayloadSize;

    /// <summary>Alias for <see cref="PayloadSize"/> for callers that need the complete size.</summary>
    public const int CompletePayloadSize = PayloadSize;

    /// <summary>Number of 512-byte sectors in the conventional HDDSS payload.</summary>
    public const int PayloadSectorCount = PayloadSize / SectorSize;

    // Documented offsets inside sector 16 (Free60 FATX table). The 0x15C..0x1FF tail is
    // intentionally left uninterpreted: public format references do not assign it a field.
    public const int SerialNumberOffset = 0x00;
    public const int SerialNumberLength = 0x14;
    public const int FirmwareRevisionOffset = 0x14;
    public const int FirmwareRevisionLength = 0x08;
    public const int ModelNumberOffset = 0x1C;
    public const int ModelNumberLength = 0x28;
    public const int LogoDigestOffset = 0x44;
    public const int LogoDigestLength = 0x14;
    public const int UserAddressableSectorsOffset = 0x58;
    public const int UserAddressableSectorsLength = 0x04;
    public const int RsaSignatureOffset = 0x5C;
    public const int RsaSignatureLength = 0x100;

    // Free60 documents a signed MS Logo Size at 0x200 and logo bytes at 0x204. Its table calls
    // the size a signed int; the open-source Velocity parser reads this field as big endian after
    // switching back from the little-endian sector-count field:
    // https://github.com/hetelek/Velocity/blob/master/XboxInternals/Fatx/FatxDrive.cpp
    public const int LogoSizeOffset = 0x200;
    public const int LogoSizeLength = 0x04;
    public const int LogoDataOffset = 0x204;

    /// <summary>
    /// Reads and classifies the complete HDDSS payload at <see cref="SectorOffset"/>.
    /// The supplied length is authoritative and is never obtained from <paramref name="source"/>.
    /// </summary>
    public static Xbox360SecuritySectorResult Detect(Stream source, long sourceLength)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (sourceLength < 0)
            return Unavailable("The supplied source length is negative.");
        if (sourceLength < SectorOffset + PayloadSize)
            return Unavailable("The source ends before the complete HDDSS payload.");
        try
        {
            if (!source.CanRead || !source.CanSeek)
                return Unavailable("The source is not a readable, seekable stream.");
        }
        catch (Exception exception) when (IsAccessFailure(exception))
        {
            return Unavailable("The source capabilities could not be queried.");
        }

        long originalPosition;
        try
        {
            originalPosition = source.Position;
        }
        catch (Exception exception) when (IsAccessFailure(exception))
        {
            return Unavailable("The source position could not be queried.");
        }

        byte[] payload = new byte[PayloadSize];
        try
        {
            source.Position = SectorOffset;
            int total = 0;
            while (total < payload.Length)
            {
                int read = source.Read(payload.AsSpan(total));
                if (read <= 0)
                    return Unavailable("The complete HDDSS payload could not be read.");
                total += read;
            }
        }
        catch (Exception exception) when (IsAccessFailure(exception))
        {
            return Unavailable("The HDDSS payload could not be read.");
        }
        finally
        {
            try
            {
                source.Position = originalPosition;
            }
            catch (Exception exception) when (IsAccessFailure(exception))
            {
                // Position restoration is best effort. The result remains useful even for a
                // stream whose position setter failed after the read.
            }
        }

        return ValidatePayload(payload);
    }

    /// <summary>Alias for <see cref="Detect"/> for callers that prefer read terminology.</summary>
    public static Xbox360SecuritySectorResult Read(Stream source, long sourceLength) =>
        Detect(source, sourceLength);

    /// <summary>Alias for <see cref="Detect"/> for callers that prefer inspect terminology.</summary>
    public static Xbox360SecuritySectorResult Inspect(Stream source, long sourceLength) =>
        Detect(source, sourceLength);

    /// <summary>
    /// Validates exactly one complete 0xE00-byte HDDSS payload already held by the caller. The
    /// input is copied and never retained, so a caller may safely reuse or mutate its buffer after
    /// this method returns.
    /// </summary>
    public static Xbox360SecuritySectorResult Validate(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadSize)
            return new Xbox360SecuritySectorResult(
                Xbox360SecuritySectorState.Invalid,
                SectorOffset,
                payload.Length,
                null,
                null,
                "The supplied HDDSS buffer is not exactly 0xE00 bytes.");

        return ValidatePayload(payload);
    }

    /// <summary>Returns a stable uppercase SHA-256 fingerprint without logging or exposing bytes.</summary>
    public static string ComputeSha256(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadSize)
            throw new ArgumentException("An Xbox 360 HDDSS payload must be exactly 0xE00 bytes.", nameof(payload));
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5350",
        Justification = "SHA-1 is the legacy digest explicitly stored in the Xbox 360 HDDSS format; it is used only to validate that declared logo bytes match that field.")]
    private static Xbox360SecuritySectorResult ValidatePayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadSize)
            return new Xbox360SecuritySectorResult(
                Xbox360SecuritySectorState.Invalid,
                SectorOffset,
                payload.Length,
                null,
                null,
                "The HDDSS buffer is not exactly 0xE00 bytes.");

        // BadStorage and other securityless Xbox 360 layouts commonly leave sector 16 blank.
        // Treat the first sector as the authoritative absence marker: nonzero bytes in the
        // following sectors must not turn a blank header into a malformed/present candidate.
        ReadOnlySpan<byte> header = payload[..SectorSize];
        if (IsAllZero(header))
            return new Xbox360SecuritySectorResult(
                Xbox360SecuritySectorState.Absent,
                SectorOffset,
                PayloadSize,
                null,
                null,
                "The HDDSS sector-16 header is blank.");

        // ATA IDENTIFY strings are fixed-width printable ASCII fields (space-padded in
        // published HDDSS examples). Requiring all three fields to contain text avoids
        // misclassifying an arbitrary nonzero sector or an overwritten partition table.
        if (!IsPrintableAsciiField(header[SerialNumberOffset..(SerialNumberOffset + SerialNumberLength)]) ||
            !IsPrintableAsciiField(header[FirmwareRevisionOffset..(FirmwareRevisionOffset + FirmwareRevisionLength)]) ||
            !IsPrintableAsciiField(header[ModelNumberOffset..(ModelNumberOffset + ModelNumberLength)]))
        {
            return Invalid("One or more ATA identity fields is not a populated printable ASCII field.");
        }

        // Free60 identifies the logo digest as a 20-byte field and the sector count as a
        // little-endian UINT32. Both are signed as part of the RSA-covered blob, so a zero
        // digest, zero capacity, or all-zero/all-FF signature is not a valid present sector.
        ReadOnlySpan<byte> logoDigest = header[LogoDigestOffset..(LogoDigestOffset + LogoDigestLength)];
        uint sectorCount = BinaryPrimitives.ReadUInt32LittleEndian(
            header[UserAddressableSectorsOffset..(UserAddressableSectorsOffset + UserAddressableSectorsLength)]);
        ReadOnlySpan<byte> signature = header[RsaSignatureOffset..(RsaSignatureOffset + RsaSignatureLength)];
        if (IsAllZero(logoDigest) || IsAllZero(signature) || IsAllByte(signature, 0xFF) || sectorCount == 0)
            return Invalid("The documented digest, sector count, or RSA signature field is empty.");

        int logoSize = BinaryPrimitives.ReadInt32BigEndian(
            payload.Slice(LogoSizeOffset, LogoSizeLength));
        if (logoSize <= 0 || logoSize > PayloadSize - LogoDataOffset)
            return Invalid("The signed MS Logo Size is not a positive value bounded by the HDDSS payload.");

        // Public samples describe the logo as a PNG, but the format's signed linkage is the
        // SHA-1 digest in sector 16. Validate that digest over exactly the declared byte range;
        // do not hard-code one logo or accept a merely nonzero image/padding region.
        ReadOnlySpan<byte> logo = payload.Slice(LogoDataOffset, logoSize);
        byte[] computedLogoDigest = SHA1.HashData(logo);
        if (!CryptographicOperations.FixedTimeEquals(logoDigest, computedLogoDigest))
            return Invalid("The declared MS Logo bytes do not match the sector-16 SHA-1 digest.");

        byte[] copy = payload.ToArray();
        string fingerprint = Convert.ToHexString(SHA256.HashData(copy));
        return new Xbox360SecuritySectorResult(
            Xbox360SecuritySectorState.Present,
            SectorOffset,
            PayloadSize,
            copy,
            fingerprint,
            // The public sources establish that the RSA signature is required, but this
            // standalone library intentionally has no Microsoft SATA public-key trust store.
            // Therefore Present means structurally validated/populated, never “signature proven”.
            "Documented structure and MS Logo digest validated; Microsoft RSA signature was not cryptographically verified.");
    }

    private static Xbox360SecuritySectorResult Invalid(string reason) =>
        new(Xbox360SecuritySectorState.Invalid, SectorOffset, PayloadSize, null, null, reason);

    private static Xbox360SecuritySectorResult Unavailable(string reason) =>
        new(Xbox360SecuritySectorState.Unavailable, SectorOffset, PayloadSize, null, null, reason);

    private static bool IsPrintableAsciiField(ReadOnlySpan<byte> field)
    {
        bool hasNonSpace = false;
        foreach (byte value in field)
        {
            if (value < 0x20 || value > 0x7E)
                return false;
            if (value != 0x20)
                hasNonSpace = true;
        }
        return hasNonSpace;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
            if (value != 0) return false;
        return true;
    }

    private static bool IsAllByte(ReadOnlySpan<byte> bytes, byte expected)
    {
        foreach (byte value in bytes)
            if (value != expected) return false;
        return true;
    }

    private static bool IsAccessFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        ObjectDisposedException or
        ArgumentException;
}
