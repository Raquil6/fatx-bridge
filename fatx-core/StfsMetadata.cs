using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace FatxBridge.Core;

/// <summary>Outcome of a bounded STFS/XContent metadata inspection.</summary>
public enum StfsMetadataState
{
    /// <summary>The recognized package header and supported metadata fields were parsed.</summary>
    Present,

    /// <summary>The package magic was recognized, but the header fields were malformed.</summary>
    Invalid,

    /// <summary>The input is not a supported package magic or metadata version/layout.</summary>
    Unsupported,

    /// <summary>The required header bytes were unavailable or could not be read safely.</summary>
    Unavailable,
}

/// <summary>The exact four-byte STFS/XContent package signature.</summary>
public enum StfsSignatureKind
{
    Con,
    Live,
    Pirs,
}

/// <summary>Xbox 360 language-slot identifiers used by STFS metadata.</summary>
public enum StfsLanguage
{
    Unknown = 0,
    English = 1,
    Japanese = 2,
    German = 3,
    French = 4,
    Spanish = 5,
    Italian = 6,
    Korean = 7,
    TraditionalChinese = 8,
    Portuguese = 9,
    SimplifiedChinese = 10,
    Polish = 11,
    Russian = 12,
}

/// <summary>A decoded, trimmed localized STFS text slot.</summary>
public sealed record StfsLocalizedText(StfsLanguage Language, string Value);

/// <summary>Structured result returned by <see cref="StfsMetadata"/>.</summary>
public sealed record StfsMetadataResult(
    StfsMetadataState State,
    StfsMetadata? Metadata,
    int BytesRead,
    string? Reason)
{
    /// <summary>Compatibility alias for callers that use status terminology.</summary>
    public StfsMetadataState Status => State;

    /// <summary>Whether the package metadata was parsed successfully.</summary>
    public bool IsPresent => State == StfsMetadataState.Present;

    /// <summary>Alias for <see cref="IsPresent"/> using success terminology.</summary>
    public bool IsSuccess => IsPresent;

    /// <summary>The parser never verifies package signatures or hash tables.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822",
        Justification = "This constant status is part of the public result contract.")]
    public bool CryptographicVerificationPerformed => false;

    /// <summary>Signature kind when a recognized package header was parsed far enough to identify it.</summary>
    public StfsSignatureKind? SignatureKind => Metadata?.SignatureKind;

    /// <summary>The exact package magic when metadata is available.</summary>
    public string? Magic => Metadata?.Magic;

    /// <summary>Alias for <see cref="Reason"/>.</summary>
    public string? FailureReason => Reason;
}

/// <summary>
/// Read-only parser for the bounded metadata prefix of Xbox 360 STFS/XContent packages.
/// It recognizes only CON , LIVE, and PIRS signatures and never extracts package files.
/// </summary>
public sealed class StfsMetadata
{
    // Free60's STFS table gives the absolute offsets below. Xenia's packed XContentMetadata
    // independently confirms the version-1/2 layout and the 16-bit big-endian string arrays:
    // https://free60.org/System-Software/Formats/STFS/
    // https://github.com/xenia-project/xenia/blob/master/src/xenia/vfs/devices/stfs_xbox.h
    public const int MagicOffset = 0x0000;
    public const int MagicLength = 0x0004;
    public const int HeaderSizeOffset = 0x0340;
    public const int HeaderSizeLength = 0x0004;
    public const int MetadataOffset = 0x0344;

    public const int ContentTypeOffset = 0x0344;
    public const int MetadataVersionOffset = 0x0348;
    public const int ContentSizeOffset = 0x034C;
    public const int MediaIdOffset = 0x0354;
    public const int VersionOffset = 0x0358;
    public const int BaseVersionOffset = 0x035C;
    public const int TitleIdOffset = 0x0360;
    public const int PlatformOffset = 0x0364;
    public const int ExecutableTypeOffset = 0x0365;
    public const int DiscNumberOffset = 0x0366;
    public const int DiscInSetOffset = 0x0367;
    public const int SaveGameIdOffset = 0x0368;
    public const int VolumeDescriptorTypeOffset = 0x03A9;

    public const int SeriesIdOffset = 0x03B1;
    public const int SeriesIdLength = 0x0010;
    public const int SeasonIdOffset = 0x03C1;
    public const int SeasonIdLength = 0x0010;
    public const int SeasonNumberOffset = 0x03D1;
    public const int EpisodeNumberOffset = 0x03D3;

    public const int DisplayNameOffset = 0x0411;
    public const int DescriptionOffset = 0x0D11;
    public const int PublisherNameOffset = 0x1611;
    public const int TitleNameOffset = 0x1691;
    public const int TransferFlagsOffset = 0x1711;
    public const int ThumbnailSizeOffset = 0x1712;
    public const int TitleThumbnailSizeOffset = 0x1716;

    /// <summary>Each v1 localized name/description slot is 128 UTF-16BE code units.</summary>
    public const int LanguageSlotSize = 0x0100;

    /// <summary>Version 1 stores nine localized slots in each base table.</summary>
    public const int Version1LanguageCount = 9;

    /// <summary>Version 2 adds three slots in the extended name/description tables.</summary>
    public const int Version2AdditionalLanguageCount = 3;

    public const int Version2DisplayNameOffset = 0x541A;
    public const int Version2DescriptionOffset = 0x941A;

    /// <summary>Exclusive end of fields through the transfer and thumbnail-size metadata.</summary>
    public const int MinimumMetadataBytes = 0x171A;

    /// <summary>Exclusive end of the documented version-2 metadata prefix.</summary>
    public const int Version2MetadataBytes = 0x971A;

    /// <summary>
    /// Maximum bytes this parser reads from a package. It covers the documented v2 metadata
    /// prefix and deliberately excludes thumbnails and package data beyond that boundary.
    /// </summary>
    public const int MaximumHeaderRead = 0xA000;

    /// <summary>Known STFS header sizes are far below this bounded sanity limit.</summary>
    public const int MaximumDeclaredHeaderSize = 0x00100000;

    // Xenia's STFS hash-level table has 0x4AF768 data blocks of 0x1000 bytes at its largest
    // documented level; this rejects impossible/absurd signed content sizes without reading data.
    public const long MaximumDeclaredContentSize = 0x4AF768000L;

    private static readonly Encoding Utf16BigEndian = new UnicodeEncoding(
        bigEndian: true,
        byteOrderMark: false,
        throwOnInvalidBytes: true);

    private static readonly ReadOnlyCollection<StfsLocalizedText> EmptyLocalizedText =
        Array.AsReadOnly(Array.Empty<StfsLocalizedText>());

    private readonly byte[]? seriesId;
    private readonly byte[]? seasonId;

    private StfsMetadata(
        string magic,
        StfsSignatureKind signatureKind,
        int headerSize,
        uint contentType,
        uint metadataVersion,
        long contentSize,
        uint mediaId,
        int version,
        int baseVersion,
        uint titleId,
        byte platform,
        byte executableType,
        byte discNumber,
        byte discInSet,
        uint saveGameId,
        byte transferFlags,
        string publisherName,
        string titleName,
        IReadOnlyList<StfsLocalizedText> displayNames,
        IReadOnlyList<StfsLocalizedText> descriptions,
        byte[]? seriesId,
        byte[]? seasonId,
        short? seasonNumber,
        short? episodeNumber,
        uint thumbnailSize,
        uint titleThumbnailSize,
        uint volumeDescriptorType)
    {
        Magic = magic;
        SignatureKind = signatureKind;
        HeaderSize = headerSize;
        ContentType = contentType;
        MetadataVersion = metadataVersion;
        ContentSize = contentSize;
        MediaId = mediaId;
        Version = version;
        BaseVersion = baseVersion;
        TitleId = titleId;
        Platform = platform;
        ExecutableType = executableType;
        DiscNumber = discNumber;
        DiscInSet = discInSet;
        SaveGameId = saveGameId;
        TransferFlags = transferFlags;
        PublisherName = publisherName;
        TitleName = titleName;
        DisplayNames = displayNames;
        Descriptions = descriptions;
        this.seriesId = seriesId;
        this.seasonId = seasonId;
        SeasonNumber = seasonNumber;
        EpisodeNumber = episodeNumber;
        ThumbnailSize = thumbnailSize;
        TitleThumbnailSize = titleThumbnailSize;
        VolumeDescriptorType = volumeDescriptorType;
    }

    public string Magic { get; }
    public StfsSignatureKind SignatureKind { get; }
    public int HeaderSize { get; }
    public uint ContentType { get; }
    public uint MetadataVersion { get; }
    public long ContentSize { get; }
    public uint MediaId { get; }
    public int Version { get; }
    public int BaseVersion { get; }
    public uint TitleId { get; }
    public byte Platform { get; }
    public byte ExecutableType { get; }
    public byte DiscNumber { get; }
    public byte DiscInSet { get; }
    public uint SaveGameId { get; }
    public byte TransferFlags { get; }
    public string PublisherName { get; }
    public string TitleName { get; }
    public IReadOnlyList<StfsLocalizedText> DisplayNames { get; }
    public IReadOnlyList<StfsLocalizedText> Descriptions { get; }
    public short? SeasonNumber { get; }
    public short? EpisodeNumber { get; }
    public uint ThumbnailSize { get; }
    public uint TitleThumbnailSize { get; }
    public uint VolumeDescriptorType { get; }

    /// <summary>Version-2 series identifier, copied from the bounded header when available.</summary>
    public byte[]? SeriesId => seriesId is null ? null : (byte[])seriesId.Clone();

    /// <summary>Version-2 season identifier, copied from the bounded header when available.</summary>
    public byte[]? SeasonId => seasonId is null ? null : (byte[])seasonId.Clone();

    /// <summary>English display name, falling back to the first nonempty localized slot.</summary>
    public string? DisplayName => PreferredText(DisplayNames);

    /// <summary>English description, falling back to the first nonempty localized slot.</summary>
    public string? DisplayDescription => PreferredText(Descriptions);

    /// <summary>Package signatures and hash tables are intentionally not cryptographically verified.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822",
        Justification = "This constant status is part of the public metadata contract.")]
    public bool CryptographicVerificationPerformed => false;

    /// <summary>Returns the localized display name for one language, if populated.</summary>
    public string? GetDisplayName(StfsLanguage language) => GetLocalized(DisplayNames, language);

    /// <summary>Returns the localized description for one language, if populated.</summary>
    public string? GetDescription(StfsLanguage language) => GetLocalized(Descriptions, language);

    /// <summary>
    /// Reads a bounded metadata prefix from a seekable stream. The stream remains owned by the
    /// caller and its position is restored on every path where the stream permits restoration.
    /// </summary>
    public static StfsMetadataResult Detect(Stream source, long sourceLength)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (sourceLength < MagicLength)
            return Unavailable(0, "The source ends before an STFS package magic can be read.");

        try
        {
            if (!source.CanRead || !source.CanSeek)
                return Unavailable(0, "The source is not a readable, seekable stream.");
        }
        catch (Exception exception) when (IsAccessFailure(exception))
        {
            return Unavailable(0, "The source capabilities could not be queried.");
        }

        long originalPosition;
        try
        {
            originalPosition = source.Position;
        }
        catch (Exception exception) when (IsAccessFailure(exception))
        {
            return Unavailable(0, "The source position could not be queried.");
        }

        int readLength = (int)Math.Min(sourceLength, MaximumHeaderRead);
        byte[] header = new byte[readLength];
        int total = 0;
        try
        {
            source.Position = 0;
            while (total < header.Length)
            {
                int read = source.Read(header.AsSpan(total));
                if (read <= 0)
                    return Unavailable(total, "The bounded STFS header prefix could not be read completely.");
                total += read;
            }
        }
        catch (Exception exception) when (IsAccessFailure(exception))
        {
            return Unavailable(total, "The bounded STFS header prefix could not be read.");
        }
        finally
        {
            try
            {
                source.Position = originalPosition;
            }
            catch (Exception exception) when (IsAccessFailure(exception))
            {
                // Restoration is best effort for a stream whose setter failed after reading.
            }
        }

        return ParseCore(header, sourceLength);
    }

    /// <summary>Alias for <see cref="Detect"/>.</summary>
    public static StfsMetadataResult Read(Stream source, long sourceLength) => Detect(source, sourceLength);

    /// <summary>Alias for <see cref="Detect"/>.</summary>
    public static StfsMetadataResult Inspect(Stream source, long sourceLength) => Detect(source, sourceLength);

    /// <summary>Parses a bounded header prefix already held by the caller.</summary>
    public static StfsMetadataResult Parse(ReadOnlySpan<byte> headerBytes) => ParseCore(headerBytes, null);

    /// <summary>Parses a bounded header prefix already held by the caller.</summary>
    public static StfsMetadataResult Parse(ReadOnlyMemory<byte> headerBytes) => Parse(headerBytes.Span);

    /// <summary>Alias for <see cref="Parse(ReadOnlySpan{byte})"/>.</summary>
    public static StfsMetadataResult Validate(ReadOnlySpan<byte> headerBytes) => Parse(headerBytes);

    private static StfsMetadataResult ParseCore(ReadOnlySpan<byte> headerBytes, long? sourceLength)
    {
        if (headerBytes.Length < MagicLength)
            return Unavailable(headerBytes.Length, "The header prefix is shorter than the four-byte package magic.");

        if (!TryGetSignature(headerBytes[..MagicLength], out StfsSignatureKind signatureKind, out string magic))
            return Unsupported(headerBytes.Length, "The package magic is not exactly CON , LIVE, or PIRS.");

        if (!HasRange(headerBytes, HeaderSizeOffset, HeaderSizeLength))
            return Unavailable(headerBytes.Length, "The header prefix ends before the documented HeaderSize field.");

        uint declaredHeaderSize = BinaryPrimitives.ReadUInt32BigEndian(
            headerBytes.Slice(HeaderSizeOffset, HeaderSizeLength));
        if (declaredHeaderSize < MinimumMetadataBytes || declaredHeaderSize > MaximumDeclaredHeaderSize)
            return Invalid(headerBytes.Length, "The declared STFS HeaderSize is outside the supported bounded range.");
        if (sourceLength is { } knownLength && declaredHeaderSize > knownLength)
            return Unavailable(headerBytes.Length, "The source ends before the declared STFS header.");

        if (!HasRange(headerBytes, MetadataVersionOffset, sizeof(uint)))
            return Unavailable(headerBytes.Length, "The header prefix ends before the metadata version field.");

        uint metadataVersion = BinaryPrimitives.ReadUInt32BigEndian(
            headerBytes.Slice(MetadataVersionOffset, sizeof(uint)));
        if (metadataVersion is not (1 or 2))
            return Unsupported(headerBytes.Length, "Only documented STFS metadata versions 1 and 2 are supported.");
        if (metadataVersion == 2 && declaredHeaderSize < Version2MetadataBytes)
            return Invalid(headerBytes.Length, "Metadata version 2 requires the documented extended metadata header.");

        int requiredBytes = metadataVersion == 2 ? Version2MetadataBytes : MinimumMetadataBytes;
        if (headerBytes.Length < requiredBytes)
            return Unavailable(headerBytes.Length, "The header prefix ends before the documented metadata fields.");

        long contentSize = BinaryPrimitives.ReadInt64BigEndian(
            headerBytes.Slice(ContentSizeOffset, sizeof(long)));
        if (contentSize < 0 || contentSize > MaximumDeclaredContentSize)
            return Invalid(headerBytes.Length, "The declared content size is outside the supported bounded range.");

        uint volumeDescriptorType = BinaryPrimitives.ReadUInt32BigEndian(
            headerBytes.Slice(VolumeDescriptorTypeOffset, sizeof(uint)));
        if (volumeDescriptorType != 0)
            return Unsupported(headerBytes.Length, "SVOD and other non-STFS descriptor types are outside this parser's scope.");

        string? displayNameError = null;
        string? descriptionError = null;
        bool displayNamesValid = TryDecodeLocalized(
                headerBytes,
                DisplayNameOffset,
                Version1LanguageCount,
                1,
                out ReadOnlyCollection<StfsLocalizedText> displayNames,
                out displayNameError);
        bool descriptionsValid = TryDecodeLocalized(
                headerBytes,
                DescriptionOffset,
                Version1LanguageCount,
                1,
                out ReadOnlyCollection<StfsLocalizedText> descriptions,
                out descriptionError);
        if (!displayNamesValid || !descriptionsValid)
        {
            return Invalid(headerBytes.Length, displayNameError ?? descriptionError ?? "A localized text field is malformed.");
        }

        if (!TryDecodeText(headerBytes.Slice(PublisherNameOffset, 0x80), out string publisherName) ||
            !TryDecodeText(headerBytes.Slice(TitleNameOffset, 0x80), out string titleName))
        {
            return Invalid(headerBytes.Length, "The publisher or title UTF-16BE field is malformed.");
        }

        if (metadataVersion == 2)
        {
            string? extraDisplayNameError = null;
            string? extraDescriptionError = null;
            bool extraDisplayNamesValid = TryDecodeLocalized(
                    headerBytes,
                    Version2DisplayNameOffset,
                    Version2AdditionalLanguageCount,
                    Version1LanguageCount + 1,
                    out ReadOnlyCollection<StfsLocalizedText> extraDisplayNames,
                    out extraDisplayNameError);
            bool extraDescriptionsValid = TryDecodeLocalized(
                    headerBytes,
                    Version2DescriptionOffset,
                    Version2AdditionalLanguageCount,
                    Version1LanguageCount + 1,
                    out ReadOnlyCollection<StfsLocalizedText> extraDescriptions,
                    out extraDescriptionError);
            if (!extraDisplayNamesValid || !extraDescriptionsValid)
            {
                return Invalid(headerBytes.Length, extraDisplayNameError ?? extraDescriptionError ?? "A version-2 localized text field is malformed.");
            }

            displayNames = Append(displayNames, extraDisplayNames);
            descriptions = Append(descriptions, extraDescriptions);
        }

        byte[]? seriesId = metadataVersion == 2
            ? headerBytes.Slice(SeriesIdOffset, SeriesIdLength).ToArray()
            : null;
        byte[]? seasonId = metadataVersion == 2
            ? headerBytes.Slice(SeasonIdOffset, SeasonIdLength).ToArray()
            : null;

        var metadata = new StfsMetadata(
            magic,
            signatureKind,
            checked((int)declaredHeaderSize),
            BinaryPrimitives.ReadUInt32BigEndian(headerBytes.Slice(ContentTypeOffset, sizeof(uint))),
            metadataVersion,
            contentSize,
            BinaryPrimitives.ReadUInt32BigEndian(headerBytes.Slice(MediaIdOffset, sizeof(uint))),
            BinaryPrimitives.ReadInt32BigEndian(headerBytes.Slice(VersionOffset, sizeof(int))),
            BinaryPrimitives.ReadInt32BigEndian(headerBytes.Slice(BaseVersionOffset, sizeof(int))),
            BinaryPrimitives.ReadUInt32BigEndian(headerBytes.Slice(TitleIdOffset, sizeof(uint))),
            headerBytes[PlatformOffset],
            headerBytes[ExecutableTypeOffset],
            headerBytes[DiscNumberOffset],
            headerBytes[DiscInSetOffset],
            BinaryPrimitives.ReadUInt32BigEndian(headerBytes.Slice(SaveGameIdOffset, sizeof(uint))),
            headerBytes[TransferFlagsOffset],
            publisherName,
            titleName,
            displayNames,
            descriptions,
            seriesId,
            seasonId,
            metadataVersion == 2
                ? BinaryPrimitives.ReadInt16BigEndian(headerBytes.Slice(SeasonNumberOffset, sizeof(short)))
                : null,
            metadataVersion == 2
                ? BinaryPrimitives.ReadInt16BigEndian(headerBytes.Slice(EpisodeNumberOffset, sizeof(short)))
                : null,
            BinaryPrimitives.ReadUInt32BigEndian(headerBytes.Slice(ThumbnailSizeOffset, sizeof(uint))),
            BinaryPrimitives.ReadUInt32BigEndian(headerBytes.Slice(TitleThumbnailSizeOffset, sizeof(uint))),
            volumeDescriptorType);

        return new StfsMetadataResult(StfsMetadataState.Present, metadata, headerBytes.Length, null);
    }

    private static bool TryGetSignature(ReadOnlySpan<byte> bytes, out StfsSignatureKind kind, out string magic)
    {
        if (bytes.SequenceEqual("CON "u8))
        {
            kind = StfsSignatureKind.Con;
            magic = "CON ";
            return true;
        }
        if (bytes.SequenceEqual("LIVE"u8))
        {
            kind = StfsSignatureKind.Live;
            magic = "LIVE";
            return true;
        }
        if (bytes.SequenceEqual("PIRS"u8))
        {
            kind = StfsSignatureKind.Pirs;
            magic = "PIRS";
            return true;
        }

        kind = default;
        magic = string.Empty;
        return false;
    }

    private static bool TryDecodeLocalized(
        ReadOnlySpan<byte> bytes,
        int offset,
        int count,
        int firstLanguage,
        out ReadOnlyCollection<StfsLocalizedText> values,
        out string? error)
    {
        var decoded = new StfsLocalizedText[count];
        for (int index = 0; index < count; index++)
        {
            int slotOffset = checked(offset + index * LanguageSlotSize);
            if (!HasRange(bytes, slotOffset, LanguageSlotSize))
            {
                values = EmptyLocalizedText;
                error = "The header prefix ends inside a localized text table.";
                return false;
            }
            if (!TryDecodeText(bytes.Slice(slotOffset, LanguageSlotSize), out string text))
            {
                values = EmptyLocalizedText;
                error = "A localized text slot is not valid UTF-16BE.";
                return false;
            }
            decoded[index] = new StfsLocalizedText((StfsLanguage)(firstLanguage + index), text);
        }

        values = Array.AsReadOnly(decoded);
        error = null;
        return true;
    }

    private static ReadOnlyCollection<StfsLocalizedText> Append(
        ReadOnlyCollection<StfsLocalizedText> first,
        ReadOnlyCollection<StfsLocalizedText> second)
    {
        var combined = new StfsLocalizedText[first.Count + second.Count];
        for (int index = 0; index < first.Count; index++) combined[index] = first[index];
        for (int index = 0; index < second.Count; index++) combined[first.Count + index] = second[index];
        return Array.AsReadOnly(combined);
    }

    private static bool TryDecodeText(ReadOnlySpan<byte> bytes, out string value)
    {
        if ((bytes.Length & 1) != 0)
        {
            value = string.Empty;
            return false;
        }

        try
        {
            value = TrimText(Utf16BigEndian.GetString(bytes));
            return true;
        }
        catch (DecoderFallbackException)
        {
            value = string.Empty;
            return false;
        }
    }

    private static string TrimText(string value) => value.TrimEnd('\0', ' ', '\t', '\r', '\n');

    private static string? PreferredText(IReadOnlyList<StfsLocalizedText> values)
    {
        string? first = null;
        foreach (StfsLocalizedText value in values)
        {
            if (value.Value.Length == 0) continue;
            first ??= value.Value;
            if (value.Language == StfsLanguage.English) return value.Value;
        }
        return first;
    }

    private static string? GetLocalized(IReadOnlyList<StfsLocalizedText> values, StfsLanguage language)
    {
        foreach (StfsLocalizedText value in values)
            if (value.Language == language) return value.Value;
        return null;
    }

    private static bool HasRange(ReadOnlySpan<byte> bytes, int offset, int length) =>
        offset >= 0 && length >= 0 && offset <= bytes.Length && length <= bytes.Length - offset;

    private static StfsMetadataResult Invalid(int bytesRead, string reason) =>
        new(StfsMetadataState.Invalid, null, bytesRead, reason);

    private static StfsMetadataResult Unsupported(int bytesRead, string reason) =>
        new(StfsMetadataState.Unsupported, null, bytesRead, reason);

    private static StfsMetadataResult Unavailable(int bytesRead, string reason) =>
        new(StfsMetadataState.Unavailable, null, bytesRead, reason);

    private static bool IsAccessFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        ObjectDisposedException or
        ArgumentException;
}
