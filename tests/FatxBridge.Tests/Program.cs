using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FatxBridge.Core;

var tests = new (string Name, Action Run)[]
{
    ("valid FATX header and root listing", ValidHeaderAndRootListing),
    ("FAT16 root chain traversal", Fat16ChainTraversal),
    ("FAT32 root chain traversal", Fat32ChainTraversal),
    ("invalid signature/header rejection", InvalidHeaderRejected),
    ("out-of-range and cyclic chain rejection", BadChainsRejected),
    ("standard offset sparse partition detection", StandardOffsetDetection),
    ("Xbox 360 Content detection does not require a security sector", SecuritylessXbox360Detection),
    ("Xbox 360 security sector valid present classification", SecuritySectorPresent),
    ("Xbox 360 security sector blank classification", SecuritySectorAbsent),
    ("Xbox 360 security sector malformed classification", SecuritySectorInvalid),
    ("Xbox 360 security sector logo digest mismatch", SecuritySectorDigestMismatch),
    ("Xbox 360 security sector logo bounds rejection", SecuritySectorLogoBoundsRejected),
    ("Xbox 360 security sector truncated logo is unavailable", SecuritySectorLogoTruncated),
    ("Xbox 360 security sector truncated classification", SecuritySectorUnavailable),
    ("Xbox 360 security sector fingerprint is stable", SecuritySectorFingerprintStable),
    ("STFS CON metadata parses with big-endian fields", StfsConMetadata),
    ("STFS LIVE and PIRS signatures are recognized", StfsLiveAndPirsMetadata),
    ("STFS metadata version 2 localized fields parse", StfsMetadataVersion2),
    ("STFS metadata truncation and invalid magic are bounded", StfsMetadataBoundaries),
    ("STFS metadata absurd sizes are rejected", StfsMetadataAbsurdSizes),
    ("original Xbox fixed HDD partition detection", OriginalXboxFixedPartitionDetection),
    ("original Xbox XBPartitioner extended partition detection", OriginalXboxPartitionTableDetection),
    ("original Xbox legacy F-takes-all detection", OriginalXboxLegacyExtendedDetection),
    ("original Xbox whole-device memory unit detection", OriginalXboxMemoryUnitDetection),
    ("standalone FATX partition image detection", StandaloneFatxImageDetection),
    ("invalid XBPartitioner entries are never advertised", InvalidOriginalXboxPartitionTableRejected),
    ("corrupt XBPartitioner tables block guessed extended bounds", CorruptOriginalXboxPartitionTableBlocksFallback),
    ("deleted directory entry filtering", DeletedEntriesIgnored),
    ("truncated stream handling", TruncatedStreamRejected),
    ("canonical 2 GiB FAT32 geometry", CanonicalLargeGeometry),
    ("allocation-table media marker semantics", MediaMarkerSemantics),
    ("invalid FAT chain marker rejection", InvalidChainMarkersRejected),
    ("retail probe endian and root validation", RetailProbeValidation),
    ("directory entry limit tail validation", DirectoryEntryLimitTailValidation),
    ("explicit raw-device length bypass", ExplicitRawDeviceLengthBypass),
    ("4 KiB-aligned USB bridge reads", AlignedUsbBridgeReads),
    ("nested FATX traversal and ranged file reads", NestedTraversalAndReads),
    ("FAT16 create overwrite truncate rename and reuse", Fat16Mutations),
    ("FAT32 mutations and name validation", Fat32MutationsAndNames),
    ("non-empty directory deletion refusal", NonEmptyDirectoryRefusal),
    ("4 KiB-aligned FATX writes", AlignedWrites),
    ("empty files retain a valid cluster and survive reopen", EmptyFileAndReopen),
    ("directory growth spans FATX clusters", DirectoryGrowth),
    ("FATX packed timestamps", PackedTimestamps),
    ("original Xbox FATX timestamps use the 2000 epoch", OriginalXboxPackedTimestamps),
    ("directory descendant moves are refused", DescendantMoveRefusal),
    ("disk-full writes preserve existing files", DiskFullPreservesExistingFiles),
    ("failed create preserves unrelated data", FailedCreatePreservesSentinel),
    ("free-space scan uses FAT pages", FreeSpaceUsesFatPages),
    ("bulk preallocation and aligned writes are batched", BulkWritesAreBatched),
    ("cached open-file writes avoid directory seek thrashing", CachedOpenFileWritesAvoidMetadataThrashing),
    ("FATX formatter publishes a bounded validated image", FormatterCreatesValidatedImage),
    ("deleted-file recovery exports only free contiguous candidates", DeletedFileRecoveryIsConservative),
    ("segmented read-only stream crosses Data file boundaries", SegmentedReadOnlyStreamCrossesBoundaries),
    ("Xbox 360 USB configuration parsing is bounded", Xbox360UsbConfigurationIsBounded),
    ("raw storage inspection is capped and position preserving", RawStorageInspectionIsBounded),
    ("raw image copy is verified by SHA-256", RawImageCopyIsVerified),
    ("raw image copy and verification work with real files", RawImageFileCopyIsVerified),
    ("raw image copy honors cancellation before writing", RawImageCopyCancellation),
};

int failures = 0;
foreach ((string name, Action run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {name}: {exception.Message}"); }
}
return failures == 0 ? 0 : 1;

static void ValidHeaderAndRootListing()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    fixture.WriteEntry(1, 0, "CONTENT", 0x10, 1, 0);
    FatxVolume volume = fixture.Open();
    Assert(volume.Metadata.AllocationTable == FatxAllocationTable.Fat16, "expected FAT16");
    Assert(volume.ListRootDirectory().Single().Name == "CONTENT", "root name mismatch");
}

static void Fat16ChainTraversal()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    fixture.SetFat(1, 2); fixture.SetFat(2, fixture.EndOfChain);
    fixture.FillDeleted(1);
    fixture.WriteEntry(2, 0, "SECOND", 0, 3, 44);
    FatxVolume volume = fixture.Open();
    Assert(volume.ListRootDirectory().Single().Name == "SECOND", "FAT16 continuation was not read");
}

static void Fat32ChainTraversal()
{
    using var fixture = FatxFixture.Create(80 * 1024 * 1024);
    Assert(fixture.Table == FatxAllocationTable.Fat32, "fixture did not select FAT32");
    fixture.SetFat(1, 2); fixture.SetFat(2, fixture.EndOfChain);
    fixture.FillDeleted(1);
    fixture.WriteEntry(2, 0, "FAT32", 0, 7, 1234);
    FatxVolume volume = fixture.Open();
    Assert(volume.ListRootDirectory().Single().FileSize == 1234, "FAT32 continuation was not read");
}

static void InvalidHeaderRejected()
{
    using var stream = new SparseStream(0x4000);
    AssertThrows<FatxFormatException>(() => FatxVolume.Open(stream, 0, 0x4000));
}

static void BadChainsRejected()
{
    using (var outside = FatxFixture.Create(2 * 1024 * 1024))
    {
        outside.SetFat(1, (uint)(outside.ClusterCount + 1));
        outside.FillDeleted(1);
        FatxVolume volume = outside.Open();
        AssertThrows<FatxFormatException>(() => volume.ListRootDirectory());
    }
    using (var cycle = FatxFixture.Create(2 * 1024 * 1024))
    {
        cycle.SetFat(1, 2); cycle.SetFat(2, 1);
        cycle.FillDeleted(1); cycle.FillDeleted(2);
        FatxVolume volume = cycle.Open();
        AssertThrows<FatxFormatException>(() => volume.ListRootDirectory());
    }
}

static void StandardOffsetDetection()
{
    const long offset = 0x80000;
    const long length = 0x80000000;
    using var disk = new SparseStream(offset + length);
    FatxFixture.WriteVolume(disk, offset, length, FatxByteOrder.BigEndian, 32);
    IReadOnlyList<FatxPartitionCandidate> found = FatxPartitionProbe.DetectStandardRetailPartitions(disk);
    Assert(found.Count == 1 && found[0].Name == "Cache 0", "standard Cache 0 was not detected");
}

static void OriginalXboxFixedPartitionDetection()
{
    const long offset = 0xABE80000;
    const long length = 0x1312D6000;
    using var disk = new SparseStream(offset + length);
    FatxFixture.WriteVolume(disk, offset, length, FatxByteOrder.LittleEndian, 32);
    FatxStorageDetection detection = FatxPartitionProbe.DetectSupportedStorage(disk)
        ?? throw new InvalidOperationException("original Xbox HDD was not classified");
    Assert(detection.Kind == FatxStorageKind.OriginalXboxHardDrive, "original Xbox HDD was not classified");
    FatxPartitionCandidate partition = detection.Partitions.Single();
    Assert(partition.Name == "E (Data)" && partition.Offset == offset, "original Xbox E partition was not detected");
    Assert(partition.SupportsWrite, "validated original Xbox partitions should expose experimental write mounting");
}

static void SecuritylessXbox360Detection()
{
    const long contentOffset = 0x130EB0000;
    const long contentLength = 64 * 1024 * 1024;
    using var disk = new SparseStream(contentOffset + contentLength);
    FatxFixture.WriteVolume(disk, contentOffset, contentLength, FatxByteOrder.BigEndian, 8);
    FatxStorageDetection detection = FatxPartitionProbe.DetectSupportedStorage(disk)
        ?? throw new InvalidOperationException("securityless Xbox 360 disk was not detected");
    Assert(detection.Kind == FatxStorageKind.Xbox360HardDrive,
        "securityless Xbox 360 layout was misclassified");
    Assert(detection.Partitions.Any(partition => partition.Name == "Content" &&
        partition.Offset == contentOffset && partition.SupportsWrite),
        "securityless Xbox 360 Content partition was not exposed for mounting");
}

static void SecuritySectorPresent()
{
    byte[] payload = SyntheticSecuritySector();
    using var source = new MemoryStream(new byte[checked((int)(Xbox360SecuritySector.SectorOffset + Xbox360SecuritySector.PayloadSize))], writable: true);
    source.Position = Xbox360SecuritySector.SectorOffset;
    source.Write(payload);
    source.Position = 123;

    Xbox360SecuritySectorResult result = Xbox360SecuritySector.Detect(source, source.Length);
    Assert(result.State == Xbox360SecuritySectorState.Present, "structured HDDSS payload was not classified present");
    Assert(result.Length == Xbox360SecuritySector.PayloadSize, "present result length is not the complete HDDSS payload size");
    Assert(result.Bytes is not null && result.Bytes.SequenceEqual(payload), "present result bytes differ from source");
    Assert(source.Position == 123, "security-sector inspection did not restore stream position");
    Assert(result.LogoSize == 2754, "present result did not expose the declared logo size");
    Assert(result.Sha256 == Xbox360SecuritySector.ComputeSha256(payload), "HDDSS payload fingerprint mismatch");
}

static void SecuritySectorAbsent()
{
    byte[] image = new byte[checked((int)(Xbox360SecuritySector.SectorOffset + Xbox360SecuritySector.PayloadSize))];
    image.AsSpan(checked((int)Xbox360SecuritySector.SectorOffset + Xbox360SecuritySector.SectorSize),
        Xbox360SecuritySector.PayloadSize - Xbox360SecuritySector.SectorSize).Fill(0xA5);
    using var source = new MemoryStream(image, writable: false);
    Xbox360SecuritySectorResult result = Xbox360SecuritySector.Detect(source, source.Length);
    Assert(result.State == Xbox360SecuritySectorState.Absent, "blank sector-16 header was not classified absent");
    Assert(result.Length == Xbox360SecuritySector.PayloadSize, "absent result length is not the complete HDDSS payload size");
    Assert(result.Bytes is null && result.Sha256 is null, "absent HDDSS payload exposed bytes");
}

static void SecuritySectorInvalid()
{
    byte[] payload = SyntheticSecuritySector();
    payload[Xbox360SecuritySector.FirmwareRevisionOffset] = 0xFF;
    Xbox360SecuritySectorResult result = Xbox360SecuritySector.Validate(payload);
    Assert(result.State == Xbox360SecuritySectorState.Invalid, "malformed HDDSS payload was not classified invalid");
    Assert(result.Bytes is null && result.Sha256 is null, "invalid HDDSS payload exposed bytes");
}

static void SecuritySectorDigestMismatch()
{
    byte[] payload = SyntheticSecuritySector();
    payload[Xbox360SecuritySector.LogoDataOffset] ^= 0x01;
    Xbox360SecuritySectorResult result = Xbox360SecuritySector.Validate(payload);
    Assert(result.State == Xbox360SecuritySectorState.Invalid, "logo digest mismatch was not classified invalid");
    Assert(result.Bytes is null && result.Sha256 is null, "digest-mismatched HDDSS payload exposed bytes");
}

static void SecuritySectorLogoBoundsRejected()
{
    byte[] oversized = SyntheticSecuritySector();
    BinaryPrimitives.WriteInt32BigEndian(
        oversized.AsSpan(Xbox360SecuritySector.LogoSizeOffset, Xbox360SecuritySector.LogoSizeLength),
        Xbox360SecuritySector.PayloadSize - Xbox360SecuritySector.LogoDataOffset + 1);
    Xbox360SecuritySectorResult oversizedResult = Xbox360SecuritySector.Validate(oversized);
    Assert(oversizedResult.State == Xbox360SecuritySectorState.Invalid,
        "oversized logo declaration was not classified invalid");

    byte[] negative = SyntheticSecuritySector();
    BinaryPrimitives.WriteInt32BigEndian(
        negative.AsSpan(Xbox360SecuritySector.LogoSizeOffset, Xbox360SecuritySector.LogoSizeLength), -1);
    Xbox360SecuritySectorResult negativeResult = Xbox360SecuritySector.Validate(negative);
    Assert(negativeResult.State == Xbox360SecuritySectorState.Invalid,
        "negative logo declaration was not classified invalid");
}

static void SecuritySectorUnavailable()
{
    byte[] payload = SyntheticSecuritySector();
    long truncatedLength = Xbox360SecuritySector.SectorOffset + Xbox360SecuritySector.PayloadSize - 1;
    byte[] image = new byte[checked((int)truncatedLength)];
    payload.AsSpan(0, image.Length - checked((int)Xbox360SecuritySector.SectorOffset)).CopyTo(
        image.AsSpan(checked((int)Xbox360SecuritySector.SectorOffset)));
    using var source = new MemoryStream(image, writable: false);
    Xbox360SecuritySectorResult result = Xbox360SecuritySector.Detect(source, truncatedLength);
    Assert(result.State == Xbox360SecuritySectorState.Unavailable, "truncated HDDSS payload was not classified unavailable");
    Assert(result.Length == Xbox360SecuritySector.PayloadSize, "unavailable result did not report the complete payload length");
    Assert(result.Bytes is null, "unavailable HDDSS payload exposed bytes");
}

static void SecuritySectorLogoTruncated()
{
    byte[] payload = SyntheticSecuritySector();
    long truncatedLength = Xbox360SecuritySector.SectorOffset + Xbox360SecuritySector.LogoDataOffset + 2754 - 1;
    byte[] image = new byte[checked((int)truncatedLength)];
    payload.AsSpan(0, image.Length - checked((int)Xbox360SecuritySector.SectorOffset)).CopyTo(
        image.AsSpan(checked((int)Xbox360SecuritySector.SectorOffset)));
    using var source = new MemoryStream(image, writable: false);
    Xbox360SecuritySectorResult result = Xbox360SecuritySector.Detect(source, truncatedLength);
    Assert(result.State == Xbox360SecuritySectorState.Unavailable,
        "payload truncated inside the declared logo was not classified unavailable");
    Assert(result.Bytes is null, "truncated logo exposed partial HDDSS bytes");
}

static void SecuritySectorFingerprintStable()
{
    byte[] payload = SyntheticSecuritySector();
    byte[] firstImage = new byte[checked((int)(Xbox360SecuritySector.SectorOffset + Xbox360SecuritySector.PayloadSize))];
    byte[] secondImage = new byte[firstImage.Length];
    Array.Fill(secondImage, (byte)0xA5);
    payload.CopyTo(firstImage, Xbox360SecuritySector.SectorOffset);
    payload.CopyTo(secondImage, Xbox360SecuritySector.SectorOffset);

    Xbox360SecuritySectorResult first = Xbox360SecuritySector.Detect(
        new MemoryStream(firstImage, writable: false), firstImage.LongLength);
    Xbox360SecuritySectorResult second = Xbox360SecuritySector.Detect(
        new MemoryStream(secondImage, writable: false), secondImage.LongLength);
    Assert(first.State == Xbox360SecuritySectorState.Present && second.State == Xbox360SecuritySectorState.Present,
        "stable-fingerprint fixtures were not present");
    Assert(first.Fingerprint == second.Fingerprint, "same security sector produced different fingerprints");
}

static void StfsConMetadata()
{
    byte[] header = SyntheticStfsHeader("CON ", metadataVersion: 1);
    using var source = new MemoryStream(header, writable: false);
    source.Position = 73;
    StfsMetadataResult result = StfsMetadata.Detect(source, source.Length);
    StfsMetadata metadata = result.Metadata ?? throw new InvalidOperationException("CON metadata was not returned");
    Assert(result.State == StfsMetadataState.Present && result.SignatureKind == StfsSignatureKind.Con,
        "CON metadata was not classified present");
    Assert(result.Magic == "CON ", "CON magic was not preserved");
    Assert(result.CryptographicVerificationPerformed == false && metadata.CryptographicVerificationPerformed == false,
        "STFS parser claimed cryptographic verification");
    Assert(source.Position == 73, "STFS metadata inspection did not restore stream position");
    Assert(metadata.HeaderSize == 0x971A && metadata.ContentType == 0x000D0000 && metadata.MetadataVersion == 1,
        "STFS header or metadata version was not parsed as big-endian");
    Assert(metadata.ContentSize == 0x123456789L && metadata.MediaId == 0x10203040 &&
        metadata.Version == 0x01020304 && metadata.BaseVersion == 0x05060708 &&
        metadata.TitleId == 0x5E2A1234,
        "STFS numeric metadata fields were not parsed correctly");
    Assert(metadata.Platform == 2 && metadata.ExecutableType == 1 && metadata.DiscNumber == 2 &&
        metadata.DiscInSet == 3 && metadata.TransferFlags == 0xA5,
        "STFS platform, disc, or transfer fields were not parsed correctly");
    Assert(metadata.DisplayName == "English Package" && metadata.DisplayDescription == "English Description" &&
        metadata.PublisherName == "Publisher" && metadata.TitleName == "Title Name",
        "UTF-16BE metadata text was not trimmed or selected correctly");
    Assert(metadata.DisplayNames.Count == StfsMetadata.Version1LanguageCount &&
        metadata.GetDisplayName(StfsLanguage.Japanese) == "日本語",
        "base localized display-name slots were not decoded");
}

static void StfsLiveAndPirsMetadata()
{
    foreach (string magic in new[] { "LIVE", "PIRS" })
    {
        StfsMetadataResult result = StfsMetadata.Parse(SyntheticStfsHeader(magic, metadataVersion: 1));
        Assert(result.State == StfsMetadataState.Present, $"{magic} metadata was not classified present");
        Assert(result.SignatureKind == (magic == "LIVE" ? StfsSignatureKind.Live : StfsSignatureKind.Pirs),
            $"{magic} signature kind was not recognized");
    }
}

static void StfsMetadataVersion2()
{
    byte[] header = SyntheticStfsHeader("PIRS", metadataVersion: 2);
    StfsMetadataResult result = StfsMetadata.Parse(header);
    StfsMetadata metadata = result.Metadata ?? throw new InvalidOperationException("version-2 metadata was not returned");
    Assert(result.State == StfsMetadataState.Present && metadata.MetadataVersion == 2,
        "version-2 metadata was not classified present");
    Assert(metadata.DisplayNames.Count == StfsMetadata.Version1LanguageCount + StfsMetadata.Version2AdditionalLanguageCount &&
        metadata.Descriptions.Count == StfsMetadata.Version1LanguageCount + StfsMetadata.Version2AdditionalLanguageCount,
        "version-2 localized slot counts were not honored");
    Assert(metadata.GetDisplayName(StfsLanguage.Polish) == "Polski" &&
        metadata.GetDescription(StfsLanguage.Russian) == "Описание",
        "version-2 extended localized slots were not decoded");
    Assert(metadata.SeriesId is not null && metadata.SeriesId.SequenceEqual(Enumerable.Range(1, 16).Select(value => (byte)value)) &&
        metadata.SeasonId is not null && metadata.SeasonId.SequenceEqual(Enumerable.Range(0xA0, 16).Select(value => (byte)value)) &&
        metadata.SeasonNumber == 4 && metadata.EpisodeNumber == 9,
        "version-2 series/season/episode fields were not parsed");
}

static void StfsMetadataBoundaries()
{
    byte[] version1 = SyntheticStfsHeader("CON ", metadataVersion: 1);
    StfsMetadataResult shortBase = StfsMetadata.Parse(version1.AsSpan(0, StfsMetadata.MinimumMetadataBytes - 1));
    Assert(shortBase.State == StfsMetadataState.Unavailable, "one-byte-short base metadata was not unavailable");
    StfsMetadataResult exactBase = StfsMetadata.Parse(version1.AsSpan(0, StfsMetadata.MinimumMetadataBytes));
    Assert(exactBase.State == StfsMetadataState.Present, "exact base metadata boundary was not accepted");

    byte[] version2 = SyntheticStfsHeader("CON ", metadataVersion: 2);
    StfsMetadataResult shortVersion2 = StfsMetadata.Parse(version2.AsSpan(0, StfsMetadata.Version2MetadataBytes - 1));
    Assert(shortVersion2.State == StfsMetadataState.Unavailable, "one-byte-short version-2 metadata was not unavailable");
    StfsMetadataResult exactVersion2 = StfsMetadata.Parse(version2.AsSpan(0, StfsMetadata.Version2MetadataBytes));
    Assert(exactVersion2.State == StfsMetadataState.Present, "exact version-2 metadata boundary was not accepted");

    byte[] invalidMagic = (byte[])version1.Clone();
    invalidMagic[0] = (byte)'X';
    StfsMetadataResult unsupported = StfsMetadata.Parse(invalidMagic);
    Assert(unsupported.State == StfsMetadataState.Unsupported, "invalid package magic was not unsupported");
}

static void StfsMetadataAbsurdSizes()
{
    byte[] absurdHeader = SyntheticStfsHeader("CON ", metadataVersion: 1);
    BinaryPrimitives.WriteUInt32BigEndian(
        absurdHeader.AsSpan(StfsMetadata.HeaderSizeOffset, StfsMetadata.HeaderSizeLength), uint.MaxValue);
    Assert(StfsMetadata.Parse(absurdHeader).State == StfsMetadataState.Invalid,
        "absurd STFS header size was not rejected");

    byte[] absurdContent = SyntheticStfsHeader("CON ", metadataVersion: 1);
    BinaryPrimitives.WriteInt64BigEndian(absurdContent.AsSpan(StfsMetadata.ContentSizeOffset, sizeof(long)), long.MaxValue);
    Assert(StfsMetadata.Parse(absurdContent).State == StfsMetadataState.Invalid,
        "absurd STFS content size was not rejected");
}

static byte[] SyntheticStfsHeader(string magic, uint metadataVersion)
{
    int length = metadataVersion == 2 ? StfsMetadata.Version2MetadataBytes : StfsMetadata.MaximumHeaderRead;
    byte[] header = new byte[length];
    Encoding.ASCII.GetBytes(magic, header.AsSpan(StfsMetadata.MagicOffset, StfsMetadata.MagicLength));
    BinaryPrimitives.WriteUInt32BigEndian(
        header.AsSpan(StfsMetadata.HeaderSizeOffset, StfsMetadata.HeaderSizeLength),
        (uint)(metadataVersion == 2 ? StfsMetadata.Version2MetadataBytes : 0x971A));
    BinaryPrimitives.WriteUInt32BigEndian(
        header.AsSpan(StfsMetadata.ContentTypeOffset, sizeof(uint)), 0x000D0000);
    BinaryPrimitives.WriteUInt32BigEndian(
        header.AsSpan(StfsMetadata.MetadataVersionOffset, sizeof(uint)), metadataVersion);
    BinaryPrimitives.WriteInt64BigEndian(
        header.AsSpan(StfsMetadata.ContentSizeOffset, sizeof(long)), 0x123456789L);
    BinaryPrimitives.WriteUInt32BigEndian(
        header.AsSpan(StfsMetadata.MediaIdOffset, sizeof(uint)), 0x10203040);
    BinaryPrimitives.WriteInt32BigEndian(
        header.AsSpan(StfsMetadata.VersionOffset, sizeof(int)), 0x01020304);
    BinaryPrimitives.WriteInt32BigEndian(
        header.AsSpan(StfsMetadata.BaseVersionOffset, sizeof(int)), 0x05060708);
    BinaryPrimitives.WriteUInt32BigEndian(
        header.AsSpan(StfsMetadata.TitleIdOffset, sizeof(uint)), 0x5E2A1234);
    header[StfsMetadata.PlatformOffset] = 2;
    header[StfsMetadata.ExecutableTypeOffset] = 1;
    header[StfsMetadata.DiscNumberOffset] = 2;
    header[StfsMetadata.DiscInSetOffset] = 3;
    BinaryPrimitives.WriteUInt32BigEndian(
        header.AsSpan(StfsMetadata.SaveGameIdOffset, sizeof(uint)), 0xCAFEBABE);
    header[StfsMetadata.TransferFlagsOffset] = 0xA5;

    WriteUtf16Be(header, StfsMetadata.DisplayNameOffset, StfsMetadata.LanguageSlotSize, "English Package ");
    WriteUtf16Be(header, StfsMetadata.DisplayNameOffset + StfsMetadata.LanguageSlotSize, StfsMetadata.LanguageSlotSize, "日本語");
    WriteUtf16Be(header, StfsMetadata.DescriptionOffset, StfsMetadata.LanguageSlotSize, "English Description ");
    WriteUtf16Be(header, StfsMetadata.PublisherNameOffset, 0x80, "Publisher ");
    WriteUtf16Be(header, StfsMetadata.TitleNameOffset, 0x80, "Title Name ");

    if (metadataVersion == 2)
    {
        for (int index = 0; index < StfsMetadata.SeriesIdLength; index++)
            header[StfsMetadata.SeriesIdOffset + index] = checked((byte)(index + 1));
        for (int index = 0; index < StfsMetadata.SeasonIdLength; index++)
            header[StfsMetadata.SeasonIdOffset + index] = checked((byte)(0xA0 + index));
        BinaryPrimitives.WriteInt16BigEndian(
            header.AsSpan(StfsMetadata.SeasonNumberOffset, sizeof(short)), 4);
        BinaryPrimitives.WriteInt16BigEndian(
            header.AsSpan(StfsMetadata.EpisodeNumberOffset, sizeof(short)), 9);
        WriteUtf16Be(header, StfsMetadata.Version2DisplayNameOffset + StfsMetadata.LanguageSlotSize,
            StfsMetadata.LanguageSlotSize, "Polski");
        WriteUtf16Be(header, StfsMetadata.Version2DescriptionOffset + 2 * StfsMetadata.LanguageSlotSize,
            StfsMetadata.LanguageSlotSize, "Описание");
    }

    return header;
}

static void WriteUtf16Be(byte[] target, int offset, int capacity, string value)
{
    target.AsSpan(offset, capacity).Clear();
    int characterCount = Math.Min(value.Length, capacity / sizeof(ushort));
    for (int index = 0; index < characterCount; index++)
        BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(offset + index * sizeof(ushort), sizeof(ushort)), value[index]);
}

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Security",
    "CA5350",
    Justification = "The synthetic fixture must populate the legacy SHA-1 logo digest field defined by the Xbox 360 HDDSS format.")]
static byte[] SyntheticSecuritySector()
{
    byte[] payload = new byte[Xbox360SecuritySector.PayloadSize];
    WriteAscii(payload, Xbox360SecuritySector.SerialNumberOffset, Xbox360SecuritySector.SerialNumberLength, "SNTEST000000000001");
    WriteAscii(payload, Xbox360SecuritySector.FirmwareRevisionOffset, Xbox360SecuritySector.FirmwareRevisionLength, "FW000001");
    WriteAscii(payload, Xbox360SecuritySector.ModelNumberOffset, Xbox360SecuritySector.ModelNumberLength, "XBOX360 TEST HDD");
    BinaryPrimitives.WriteUInt32LittleEndian(
        payload.AsSpan(Xbox360SecuritySector.UserAddressableSectorsOffset, Xbox360SecuritySector.UserAddressableSectorsLength),
        0x01234567);
    for (int index = 0; index < Xbox360SecuritySector.RsaSignatureLength; index++)
        payload[Xbox360SecuritySector.RsaSignatureOffset + index] = unchecked((byte)(index * 13 + 7));

    const int logoSize = 2754;
    ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    payload.AsSpan(Xbox360SecuritySector.LogoDataOffset, logoSize).Fill(0x4D);
    pngSignature.CopyTo(payload.AsSpan(Xbox360SecuritySector.LogoDataOffset, pngSignature.Length));
    for (int index = pngSignature.Length; index < logoSize; index++)
        payload[Xbox360SecuritySector.LogoDataOffset + index] = unchecked((byte)(index * 19 + 5));
    payload.AsSpan(Xbox360SecuritySector.LogoDataOffset + logoSize).Fill(0xCC);
    BinaryPrimitives.WriteInt32BigEndian(
        payload.AsSpan(Xbox360SecuritySector.LogoSizeOffset, Xbox360SecuritySector.LogoSizeLength), logoSize);
    SHA1.HashData(payload.AsSpan(Xbox360SecuritySector.LogoDataOffset, logoSize)).CopyTo(
        payload.AsSpan(Xbox360SecuritySector.LogoDigestOffset, Xbox360SecuritySector.LogoDigestLength));
    return payload;
}

static void WriteAscii(byte[] target, int offset, int length, string value)
{
    if (value.Length > length) throw new InvalidOperationException("synthetic field is too long");
    target.AsSpan(offset, length).Fill((byte)' ');
    System.Text.Encoding.ASCII.GetBytes(value, target.AsSpan(offset, value.Length));
}

static void OriginalXboxPartitionTableDetection()
{
    const long offset = 0x200000;
    const long length = 0x2000000;
    using var disk = new SparseStream(0x4000000);
    WriteXbPartitionEntry(disk, 5, "XBOX F", offset, length, active: true);
    FatxFixture.WriteVolume(disk, offset, length, FatxByteOrder.LittleEndian, 8);

    FatxStorageDetection detection = FatxPartitionProbe.DetectSupportedStorage(disk)
        ?? throw new InvalidOperationException("XBPartitioner disk was not classified");
    Assert(detection.Kind == FatxStorageKind.OriginalXboxHardDrive, "XBPartitioner disk was not classified");
    FatxPartitionCandidate partition = detection.Partitions.Single();
    Assert(partition.Name == "F (Extended)" && partition.Offset == offset && partition.Length == length,
        "XBPartitioner F bounds were not honored");
}

static void OriginalXboxLegacyExtendedDetection()
{
    const long offset = 0x1DD156000;
    const long length = 0x4000000;
    using var disk = new SparseStream(offset + length);
    FatxFixture.WriteVolume(disk, offset, length, FatxByteOrder.LittleEndian, 8);
    IReadOnlyList<FatxPartitionCandidate> found = FatxPartitionProbe.DetectOriginalXboxPartitions(disk);
    Assert(found.Count == 1 && found[0].Name == "F (Extended)" && found[0].Length == length,
        "legacy F-takes-all partition was not detected");
}

static void OriginalXboxMemoryUnitDetection()
{
    const long length = 8 * 1024 * 1024;
    using var memoryUnit = new SparseStream(length);
    FatxFixture.WriteVolume(memoryUnit, 0, length, FatxByteOrder.LittleEndian, 4, 4096);
    FatxStorageDetection detection = FatxPartitionProbe.DetectSupportedStorage(memoryUnit)
        ?? throw new InvalidOperationException("whole-device FATX memory unit was not classified");
    Assert(detection.Kind == FatxStorageKind.OriginalXboxMemoryUnit, "whole-device FATX memory unit was not classified");
    FatxPartitionCandidate partition = detection.Partitions.Single();
    Assert(partition.Offset == 0 && partition.Length == length && partition.SupportsWrite,
        "memory-unit bounds or write capability are incorrect");
    Assert(partition.Metadata.SectorSize == 4096 && partition.Metadata.DataOffset == 0x2000 &&
        partition.Metadata.SectorsPerCluster == 4,
        "memory-unit geometry did not use 4 KiB logical sectors");
}

static void InvalidOriginalXboxPartitionTableRejected()
{
    using var disk = new SparseStream(0x1000000);
    WriteXbPartitionEntry(disk, 5, "XBOX F", 0x800000, 0x1000000, active: true);
    IReadOnlyList<FatxPartitionCandidate> found = FatxPartitionProbe.DetectOriginalXboxPartitions(disk);
    Assert(found.Count == 0, "out-of-range XBPartitioner entry was advertised");
}

static void StandaloneFatxImageDetection()
{
    const long length = 32 * 1024 * 1024;
    using var image = new SparseStream(length);
    FatxFixture.WriteVolume(image, 0, length, FatxByteOrder.BigEndian, 8);
    FatxStorageDetection detection = FatxPartitionProbe.DetectImage(image)
        ?? throw new InvalidOperationException("standalone FATX image was not detected");
    Assert(detection.Kind == FatxStorageKind.StandaloneFatxImage,
        "standalone FATX partition image was misclassified");
    Assert(detection.Partitions.Count == 1 && detection.Partitions[0].Offset == 0 &&
        detection.Partitions[0].Length == length, "standalone image bounds are incorrect");
}

static void CorruptOriginalXboxPartitionTableBlocksFallback()
{
    const long fallbackOffset = 0x1DD156000;
    const long fallbackLength = 0x4000000;
    using var disk = new SparseStream(fallbackOffset + fallbackLength);
    WriteXbPartitionEntry(disk, 5, "XBOX F", 0x100000, 0x200000, active: true);
    WriteXbPartitionEntry(disk, 6, "XBOX G", 0x180000, 0x200000, active: true);
    FatxFixture.WriteVolume(disk, fallbackOffset, fallbackLength, FatxByteOrder.LittleEndian, 8);
    IReadOnlyList<FatxPartitionCandidate> found = FatxPartitionProbe.DetectOriginalXboxPartitions(disk);
    Assert(found.Count == 0, "a corrupt XBPartitioner table allowed guessed overlapping extended bounds");
}

static void WriteXbPartitionEntry(SparseStream disk, int index, string name, long offset, long length, bool active)
{
    if (offset % 512 != 0 || length % 512 != 0) throw new ArgumentException("XBPartitioner bounds must be sector aligned.");
    byte[] table = new byte[0x1000];
    disk.Position = 0;
    _ = disk.Read(table);
    "****PARTINFO****"u8.CopyTo(table);
    int entryOffset = 48 + index * 32;
    int nameLength = Math.Min(16, name.Length);
    System.Text.Encoding.ASCII.GetBytes(name.AsSpan(0, nameLength), table.AsSpan(entryOffset, nameLength));
    BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entryOffset + 16, 4), active ? 0x80000000u : 0);
    BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entryOffset + 20, 4), checked((uint)(offset / 512)));
    BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(entryOffset + 24, 4), checked((uint)(length / 512)));
    disk.Position = 0;
    disk.Write(table);
}

static void DeletedEntriesIgnored()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    fixture.WriteDeletedEntry(1, 0);
    fixture.WriteEntry(1, 0x40, "LIVE", 0, 1, 9);
    FatxVolume volume = fixture.Open();
    IReadOnlyList<FatxDirectoryEntry> items = volume.ListRootDirectory();
    Assert(items.Count == 1 && items[0].Name == "LIVE", "deleted entry appeared in listing");
}

static void TruncatedStreamRejected()
{
    using var source = new MemoryStream(new byte[32], writable: false);
    AssertThrows<FatxFormatException>(() => FatxVolume.Open(source, 0, 0x1000));
}

static void CanonicalLargeGeometry()
{
    const long length = 0x80000000;
    using var stream = new SparseStream(length);
    FatxFixture fixture = FatxFixture.WriteVolume(stream, 0, length, FatxByteOrder.BigEndian, 0x20);
    fixture.WriteEntry(1, 0, "CANONICAL", 0, 1, 42);
    FatxVolume volume = fixture.Open();
    Assert(volume.Metadata.AllocationTable == FatxAllocationTable.Fat32, "2 GiB volume must be FAT32");
    Assert(volume.Metadata.DataOffset == 0x82000, "canonical FATX data offset mismatch");
    Assert(volume.ListRootDirectory().Single().Name == "CANONICAL", "canonical data address was not read");
}

static void MediaMarkerSemantics()
{
    using (var accepted = FatxFixture.Create(2 * 1024 * 1024))
    {
        Assert(accepted.Open().ListRootDirectory().Count == 0, "media marker should be accepted");
    }
    using (var rejected = FatxFixture.Create(2 * 1024 * 1024))
    {
        rejected.SetFat(0, rejected.EndOfChain);
        AssertThrows<FatxFormatException>(() => rejected.Open());
    }
}

static void InvalidChainMarkersRejected()
{
    foreach (uint marker in new uint[] { 0, 0xFFF7, 0xFFF8, 0xFFFE })
    {
        using var fixture = FatxFixture.Create(2 * 1024 * 1024);
        fixture.SetFat(1, marker);
        AssertThrows<FatxFormatException>(() => fixture.Open().ListRootDirectory());
    }
    using var exactLast = FatxFixture.Create(2 * 1024 * 1024);
    exactLast.SetFat(1, exactLast.EndOfChain);
    Assert(exactLast.Open().ListRootDirectory().Count == 0, "only the exact LAST marker should terminate a chain");
}

static void RetailProbeValidation()
{
    const long offset = 0x80000;
    const long length = 0x80000000;
    using (var bigEndian = new SparseStream(offset + length))
    {
        FatxFixture.WriteVolume(bigEndian, offset, length, FatxByteOrder.BigEndian, 32);
        Assert(FatxPartitionProbe.DetectStandardRetailPartitions(bigEndian).Count == 1, "XTAF Cache 0 should be detected");
    }
    using (var littleEndian = new SparseStream(offset + length))
    {
        FatxFixture.WriteVolume(littleEndian, offset, length, FatxByteOrder.LittleEndian, 32);
        Assert(FatxPartitionProbe.DetectStandardRetailPartitions(littleEndian).Count == 0, "little-endian FATX must not be labeled retail Xbox 360");
    }
    foreach (uint rootValue in new uint[] { 0, 2, 0xFFFFFFFE })
    {
        using var invalid = new SparseStream(offset + length);
        FatxFixture fixture = FatxFixture.WriteVolume(invalid, offset, length, FatxByteOrder.BigEndian, 32);
        fixture.SetFat(1, rootValue == 2 ? 1 : rootValue);
        Assert(FatxPartitionProbe.DetectStandardRetailPartitions(invalid).Count == 0, "invalid root chain was advertised");
    }
}

static void DirectoryEntryLimitTailValidation()
{
    const long offset = 0x80000;
    const long length = 0x80000000;
    using (var malformed = new SparseStream(offset + length))
    {
        FatxFixture fixture = FatxFixture.WriteVolume(malformed, offset, length, FatxByteOrder.BigEndian, 0x20);
        WriteMaximumRootEntries(fixture, tail: 0);
        AssertThrows<FatxFormatException>(() => fixture.Open().ListRootDirectory());
        Assert(FatxPartitionProbe.DetectStandardRetailPartitions(malformed).Count == 0, "probe advertised a 4096-entry root with a free tail");
    }
    using (var valid = new SparseStream(offset + length))
    {
        FatxFixture fixture = FatxFixture.WriteVolume(valid, offset, length, FatxByteOrder.BigEndian, 0x20);
        WriteMaximumRootEntries(fixture, fixture.EndOfChain);
        Assert(fixture.Open().ListRootDirectory().Count == 4096, "exact LAST tail should accept exactly 4096 entries");
        Assert(FatxPartitionProbe.DetectStandardRetailPartitions(valid).Count == 1, "probe rejected a well-terminated 4096-entry root");
    }
}

static void ExplicitRawDeviceLengthBypass()
{
    const long offset = 0x80000;
    const long length = 0x80000000;
    using var backing = new SparseStream(offset + length);
    FatxFixture.WriteVolume(backing, offset, length, FatxByteOrder.BigEndian, 0x20);
    using var rawDevice = new LengthlessStream(backing);

    IReadOnlyList<FatxPartitionCandidate> found =
        FatxPartitionProbe.DetectStandardRetailPartitions(rawDevice, offset + length);
    Assert(found.Count == 1, "explicit device capacity should avoid querying Stream.Length");

    FatxVolume volume = FatxVolume.Open(rawDevice, offset, length, offset + length);
    Assert(volume.ListRootDirectory().Count == 0, "explicit source length should open the raw-device volume");
}

static void AlignedUsbBridgeReads()
{
    const long offset = 0x80000;
    const long length = 0x80000000;
    using var backing = new SparseStream(offset + length);
    FatxFixture.WriteVolume(backing, offset, length, FatxByteOrder.BigEndian, 0x20);
    using var bridge = new StrictAlignedReadStream(backing, 0x1000);

    IReadOnlyList<FatxPartitionCandidate> found =
        FatxPartitionProbe.DetectStandardRetailPartitions(bridge, offset + length);
    Assert(found.Count == 1, "aligned-only USB bridge should be probed successfully");
}

static void NestedTraversalAndReads()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    FatxVolume volume = fixture.Open();
    volume.CreateDirectory("/A"); volume.CreateDirectory("/A/B"); volume.CreateFile("/A/B/FILE");
    volume.WriteFile("/A/B/FILE", 0, "abcdefghijklmnop"u8);
    Assert(volume.EnumerateDirectory("/A").Single().Name == "B", "nested directory was not enumerated");
    Assert(System.Text.Encoding.ASCII.GetString(volume.ReadFile("/A/B/FILE", 3, 5)) == "defgh", "ranged read mismatch");
}

static void Fat16Mutations()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    FatxVolume volume = fixture.Open();
    volume.CreateDirectory("/ONE"); volume.CreateDirectory("/TWO"); volume.CreateFile("/ONE/FILE");
    byte[] first = Enumerable.Range(0, 2500).Select(i => (byte)(i % 251)).ToArray();
    volume.WriteFile("/ONE/FILE", 0, first); volume.WriteFile("/ONE/FILE", 1000, "SIDE"u8);
    Assert(volume.ReadFile("/ONE/FILE", 1000, 4).SequenceEqual("SIDE"u8.ToArray()), "overwrite was not persisted");
    volume.SetLength("/ONE/FILE", 600); Assert(volume.GetEntry("/ONE/FILE").Entry.FileSize == 600, "truncate failed");
    volume.Move("/ONE/FILE", "/TWO/MOVED"); Assert(volume.GetEntry("/TWO/MOVED").Entry.FileSize == 600, "cross-directory rename failed");
    long before = volume.FreeSpace; volume.DeleteFile("/TWO/MOVED"); Assert(volume.FreeSpace > before, "deleted chain was not released");
}

static void Fat32MutationsAndNames()
{
    using var fixture = FatxFixture.Create(80 * 1024 * 1024);
    FatxVolume volume = fixture.Open(); volume.CreateFile("/LOWER"); volume.WriteFile("/LOWER", 0, "fat32"u8);
    Assert(System.Text.Encoding.ASCII.GetString(volume.ReadFile("/lower", 0, 5)) == "fat32", "case-insensitive lookup failed");
    AssertThrows<IOException>(() => volume.CreateFile("/lower"));
    AssertThrows<ArgumentException>(() => volume.CreateFile("/bad:name"));
    AssertThrows<IOException>(() => volume.SetLength("/LOWER", (long)uint.MaxValue + 1));
}

static void NonEmptyDirectoryRefusal()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    FatxVolume volume = fixture.Open(); volume.CreateDirectory("/DIR"); volume.CreateFile("/DIR/CHILD");
    AssertThrows<IOException>(() => volume.DeleteDirectory("/DIR"));
}

static void AlignedWrites()
{
    using var backing = new SparseStream(2 * 1024 * 1024);
    FatxFixture.WriteVolume(backing, 0, 2 * 1024 * 1024, FatxByteOrder.LittleEndian, 2);
    using var bridge = new StrictAlignedWriteStream(backing, 0x1000);
    FatxVolume volume = FatxVolume.Open(bridge, 0, 2 * 1024 * 1024);
    volume.CreateFile("/WRITTEN"); volume.WriteFile("/WRITTEN", 0, "aligned"u8);
    Assert(System.Text.Encoding.ASCII.GetString(volume.ReadFile("/WRITTEN", 0, 7)) == "aligned", "aligned write failed");
}

static void EmptyFileAndReopen()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    FatxVolume volume = fixture.Open();
    volume.CreateFile("/EMPTY");
    Assert(volume.GetEntry("/EMPTY").Entry.FirstCluster != 0, "empty file did not receive a valid cluster");
    volume.SetLength("/EMPTY", 0);
    Assert(fixture.Open().GetEntry("/EMPTY").Entry.FirstCluster != 0, "truncated empty file lost its cluster after reopen");
}

static void DirectoryGrowth()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    FatxVolume volume = fixture.Open();
    volume.CreateDirectory("/DIR");
    for (int i = 0; i < 17; i++) volume.CreateFile($"/DIR/F{i:D2}");
    Assert(volume.EnumerateDirectory("/DIR").Count == 17, "directory did not expand into a second cluster");
    Assert(fixture.Open().EnumerateDirectory("/DIR").Count == 17, "expanded directory failed reopen validation");
}

static void PackedTimestamps()
{
    var source = new DateTimeOffset(2024, 2, 3, 4, 5, 7, TimeSpan.Zero);
    uint packed = FatxVolume.EncodeTimestamp(source);
    DateTimeOffset decoded = FatxVolume.DecodeTimestamp(packed) ?? throw new InvalidOperationException("timestamp did not decode");
    Assert(decoded == new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.Zero), "FATX timestamp packing is incorrect");
    Assert(FatxVolume.DecodeTimestamp(0) is null, "zero FATX timestamp must remain unset");
}

static void OriginalXboxPackedTimestamps()
{
    var source = new DateTimeOffset(2024, 2, 3, 4, 5, 7, TimeSpan.Zero);
    uint packed = FatxVolume.EncodeTimestamp(source, FatxByteOrder.LittleEndian);
    DateTimeOffset decoded = FatxVolume.DecodeTimestamp(packed, FatxByteOrder.LittleEndian)
        ?? throw new InvalidOperationException("original Xbox timestamp did not decode");
    Assert(decoded == new DateTimeOffset(2024, 2, 3, 4, 5, 6, TimeSpan.Zero),
        "original Xbox FATX timestamp epoch is incorrect");
    Assert(FatxVolume.DecodeTimestamp(packed, FatxByteOrder.BigEndian)?.Year == 2004,
        "timestamp byte order did not select a platform-specific epoch");
}

static void DescendantMoveRefusal()
{
    using var fixture = FatxFixture.Create(2 * 1024 * 1024);
    FatxVolume volume = fixture.Open(); volume.CreateDirectory("/A"); volume.CreateDirectory("/A/B");
    AssertThrows<IOException>(() => volume.Move("/A", "/A/B/A"));
    Assert(volume.EnumerateDirectory("/").Single().Name == "A", "failed descendant move changed the source directory");
}

static void DiskFullPreservesExistingFiles()
{
    using var fixture = FatxFixture.Create(20 * 1024);
    FatxVolume volume = fixture.Open(); volume.CreateFile("/KEEP"); volume.WriteFile("/KEEP", 0, "safe"u8);
    bool full = false;
    for (int i = 0; i < 32; i++)
    {
        try { volume.CreateFile($"/F{i:D2}"); }
        catch (IOException exception) when (exception.Message.Contains("full", StringComparison.OrdinalIgnoreCase)) { full = true; break; }
    }
    Assert(full, "small fixture did not reach FATX disk-full state");
    FatxVolume reopened = fixture.Open();
    Assert(System.Text.Encoding.ASCII.GetString(reopened.ReadFile("/KEEP", 0, 4)) == "safe", "disk-full attempt altered an existing file");
}

static void FailedCreatePreservesSentinel()
{
    // File creation now has two durable writes: allocate the FAT entry, then publish
    // the directory entry. Inject failure before each to preserve the crash-safety check.
    for (int successfulWrites = 0; successfulWrites < 2; successfulWrites++)
    {
        using var fixture = FatxFixture.Create(2 * 1024 * 1024);
        FatxVolume baseline = fixture.Open(); baseline.CreateFile("/KEEP"); baseline.WriteFile("/KEEP", 0, "sentinel"u8);
        using var failing = new FailingWriteStream(fixture.Stream, successfulWrites);
        FatxVolume attempted = FatxVolume.Open(failing, 0, fixture.Length);
        AssertThrows<IOException>(() => attempted.CreateFile("/NEW"));
        FatxVolume reopened = fixture.Open();
        Assert(System.Text.Encoding.ASCII.GetString(reopened.ReadFile("/KEEP", 0, 8)) == "sentinel", "failed create overwrote an unrelated file");
    }
}

static void FreeSpaceUsesFatPages()
{
    using var backing = new SparseStream(80 * 1024 * 1024);
    FatxFixture.WriteVolume(backing, 0, backing.Length, FatxByteOrder.LittleEndian, 2);
    using var counting = new CountingReadStream(backing);
    FatxVolume volume = FatxVolume.Open(counting, 0, backing.Length);
    _ = volume.FreeSpace;
    Assert(counting.ReadCalls < 100, "free-space scan issued one raw read per FAT cluster instead of using cached FAT pages");
}

static void BulkWritesAreBatched()
{
    var stream = new SparseStream(16 * 1024 * 1024);
    using var fixture = FatxFixture.WriteVolume(stream, 0, stream.Length, FatxByteOrder.LittleEndian, 8);
    FatxVolume volume = fixture.Open();
    volume.CreateFile("/bulk.bin");
    stream.ResetCounters();
    volume.Preallocate("/bulk.bin", 4 * 1024 * 1024);
    volume.Flush();
    Assert(volume.GetEntry("/bulk.bin").Entry.FileSize == 0, "preallocation changed logical file size");
    Assert(stream.BytesWritten <= 16 * 1024, $"preallocation wrote {stream.BytesWritten} bytes instead of batching FAT metadata");

    stream.ResetCounters();
    byte[] data = Enumerable.Repeat((byte)0x5A, 1024 * 1024).ToArray();
    volume.WriteFile("/bulk.bin", 0, data);
    volume.Flush();
    Assert(stream.WriteOperations <= 4, $"aligned 1 MiB write used {stream.WriteOperations} underlying writes");
    Assert(stream.ReadOperations <= 2, $"aligned 1 MiB write used {stream.ReadOperations} underlying reads");
    Assert(volume.ReadFile("/bulk.bin", 0, data.Length).SequenceEqual(data), "batched write data mismatch");
}

static void CachedOpenFileWritesAvoidMetadataThrashing()
{
    var stream = new SparseStream(32 * 1024 * 1024);
    using var fixture = FatxFixture.WriteVolume(stream, 0, stream.Length, FatxByteOrder.LittleEndian, 8);
    FatxVolume volume = fixture.Open();
    volume.CreateFile("/stream.bin");
    FatxDirectoryEntry entry = volume.Preallocate("/stream.bin", 8 * 1024 * 1024);
    byte[] block = Enumerable.Repeat((byte)0xA7, 64 * 1024).ToArray();

    stream.ResetCounters();
    for (int offset = 0; offset < 8 * 1024 * 1024; offset += block.Length)
        entry = volume.WriteFile(entry, offset, block);
    Assert(stream.ReadOperations == 0, $"cached sequential writes performed {stream.ReadOperations} metadata reads before commit");
    Assert(stream.WriteOperations == 128, $"cached sequential writes performed {stream.WriteOperations} writes for 128 data blocks");
    byte[] readBack = new byte[block.Length];
    Assert(volume.ReadFile(entry, 0, readBack) == block.Length && readBack.SequenceEqual(block),
        "cached entry could not read initialized data");

    stream.ResetCounters();
    volume.CommitFile("/stream.bin", entry);
    volume.Flush();
    Assert(stream.ReadOperations <= 2, $"one metadata commit performed {stream.ReadOperations} underlying reads");
    Assert(stream.WriteOperations <= 1, $"one metadata commit performed {stream.WriteOperations} underlying writes");
    Assert(volume.GetEntry("/stream.bin").Entry.FileSize == 8 * 1024 * 1024, "cached file size was not published at commit");
}

static void WriteMaximumRootEntries(FatxFixture fixture, uint tail)
{
    const int entriesPerCluster = 256; // 0x20 sectors × 0x200 bytes / 0x40-byte entry.
    for (uint cluster = 1; cluster <= 16; cluster++)
    {
        fixture.SetFat(cluster, cluster == 16 ? tail : cluster + 1);
        for (int entry = 0; entry < entriesPerCluster; entry++)
            fixture.WriteEntry(cluster, entry * 0x40, $"E{cluster:D2}{entry:D3}", 0, 1, 0);
    }
}

static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

static void FormatterCreatesValidatedImage()
{
    const long length = 8 * 1024 * 1024;
    using var image = new SparseStream(length);
    image.Position = 73;
    FatxFormatResult result = FatxFormatter.Format(image,
        new FatxFormatOptions(0, length, FatxByteOrder.LittleEndian, 512, 8, FatxFormatMode.Quick, 0xA1B2C3D4));
    Assert(image.Position == 73, "formatter did not restore the caller's stream position");
    Assert(result.Metadata.SerialNumber == 0xA1B2C3D4 && result.Metadata.SectorsPerCluster == 8,
        "formatter result did not preserve the requested geometry");
    FatxVolume reopened = FatxVolume.Open(image, 0, length);
    Assert(reopened.ListRootDirectory().Count == 0, "formatted root directory was not empty");
    Assert(result.BytesWritten < length, "quick format unexpectedly rewrote the complete image");
}

static void DeletedFileRecoveryIsConservative()
{
    using var fixture = FatxFixture.Create(4 * 1024 * 1024);
    FatxVolume volume = fixture.Open();
    byte[] payload = Encoding.ASCII.GetBytes("recoverable FATX bytes");
    volume.CreateFile("/LOST.BIN");
    volume.WriteFile("/LOST.BIN", 0, payload);
    volume.DeleteFile("/LOST.BIN");

    FatxDeletedEntryCandidate candidate = volume.EnumerateDeletedEntries().Single();
    Assert(candidate.CanAttemptRead && candidate.Classification == FatxDeletedEntryClassification.ContiguousUnallocated,
        "freshly deleted file was not classified as a free contiguous candidate");
    using var destination = new MemoryStream();
    Assert(volume.ExportDeletedFile(candidate, destination) == payload.Length,
        "recovery export returned the wrong length");
    Assert(destination.ToArray().SequenceEqual(payload), "recovered bytes did not match the deleted payload");

    volume.CreateFile("/REUSED.BIN");
    volume.WriteFile("/REUSED.BIN", 0, "replacement"u8);
    AssertThrows<FatxRecoveryException>(() => volume.ReadDeletedFile(candidate, 0, payload.Length));
}

static void SegmentedReadOnlyStreamCrossesBoundaries()
{
    using var first = new MemoryStream("ABC"u8.ToArray(), writable: false);
    using var second = new MemoryStream("DEFG"u8.ToArray(), writable: false);
    using var third = new MemoryStream("HI"u8.ToArray(), writable: false);
    using var combined = new SegmentedReadOnlyStream(new Stream[] { first, second, third }, leaveOpen: true);
    combined.Position = 2;
    Span<byte> bytes = stackalloc byte[6];
    Assert(combined.Read(bytes) == 6 && bytes.SequenceEqual("CDEFGH"u8),
        "cross-segment read did not preserve byte order");
    Assert(!combined.CanWrite, "segmented container stream exposed write access");
    AssertThrows<NotSupportedException>(() => combined.WriteByte(0xFF));
}

static void Xbox360UsbConfigurationIsBounded()
{
    var configuration = new byte[Xbox360UsbContainer.ConfigurationSize];
    BinaryPrimitives.WriteUInt32BigEndian(configuration.AsSpan(0x23C, 4), 0x228);
    BinaryPrimitives.WriteUInt64BigEndian(configuration.AsSpan(0x240, 8), 16UL * 1024 * 1024 * 1024);
    BinaryPrimitives.WriteUInt16BigEndian(configuration.AsSpan(0x248, 2), 31_000);
    BinaryPrimitives.WriteUInt16BigEndian(configuration.AsSpan(0x24A, 2), 22_000);
    Xbox360UsbConfiguration parsed = Xbox360UsbContainer.ParseConfiguration(configuration);
    Assert(parsed.ValidationState == Xbox360UsbConfigurationValidationState.StructurallyValid,
        "complete type-1 configuration was not structurally valid");
    Assert(parsed.DeviceCapacityBytes == 16UL * 1024 * 1024 * 1024 && !parsed.SignatureWasVerified,
        "USB configuration fields or signature trust boundary were wrong");
    Xbox360UsbConfiguration truncated = Xbox360UsbContainer.ParseConfiguration(configuration.AsSpan(0, 0x100));
    Assert(truncated.ValidationState == Xbox360UsbConfigurationValidationState.Truncated && truncated.BytesRead == 0x100,
        "truncated configuration was not reported without over-reading");
}

static void RawStorageInspectionIsBounded()
{
    byte[] bytes = Enumerable.Range(0, 100_000).Select(value => unchecked((byte)value)).ToArray();
    using var source = new MemoryStream(bytes, writable: false);
    source.Position = 321;
    var inspector = new RawStorageInspector(source, source.Length, 512);
    RawStorageView view = inspector.ReadRange(17, 80_000);
    Assert(view.Length == RawStorageInspector.MaximumViewBytes && view.IsTruncated,
        "raw inspector did not cap its display view to 64 KiB");
    Assert(source.Position == 321, "raw inspector did not restore the caller's stream position");
    Assert(view.Bytes[0] == bytes[17] && view.HexDump.Contains("0000000000000011", StringComparison.Ordinal),
        "raw inspector returned the wrong byte range or address");
}

static void RawImageCopyIsVerified()
{
    byte[] bytes = new byte[9 * 1024 * 1024 + 317];
    for (int index = 0; index < bytes.Length; index++) bytes[index] = unchecked((byte)(index * 29 + 11));
    using var source = new MemoryStream(bytes, writable: false);
    using var destination = new MemoryStream(new byte[bytes.Length], writable: true);
    RawImageResult result = RawImageOperations.CopyAndVerifyAsync(source, destination, bytes.Length)
        .GetAwaiter().GetResult();
    Assert(result.BytesCopied == bytes.Length, "raw image byte count mismatch");
    Assert(destination.ToArray().SequenceEqual(bytes), "raw image destination differs from source");
    Assert(result.Sha256 == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
        "raw image SHA-256 mismatch");
}

static void RawImageFileCopyIsVerified()
{
    string directory = Path.Combine(Path.GetTempPath(), $"fatx-bridge-image-test-{Guid.NewGuid():N}");
    string sourcePath = Path.Combine(directory, "source.img");
    string destinationPath = Path.Combine(directory, "destination.img");
    Directory.CreateDirectory(directory);
    try
    {
        byte[] bytes = new byte[5 * 1024 * 1024 + 733];
        for (int index = 0; index < bytes.Length; index++) bytes[index] = unchecked((byte)(index * 17 + 3));
        File.WriteAllBytes(sourcePath, bytes);
        using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                   1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                   1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            destination.SetLength(bytes.Length);
            RawImageResult result = RawImageOperations.CopyAndVerifyAsync(source, destination, bytes.Length)
                .GetAwaiter().GetResult();
            Assert(result.Sha256 == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                "file-backed image SHA-256 mismatch");
        }
        Assert(File.ReadAllBytes(destinationPath).SequenceEqual(bytes),
            "file-backed image destination differs from source");
    }
    finally
    {
        if (File.Exists(sourcePath)) File.Delete(sourcePath);
        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
}

static void RawImageCopyCancellation()
{
    using var sameStream = new MemoryStream(new byte[4096], writable: true);
    AssertThrows<ArgumentException>(() => RawImageOperations.CopyAndVerifyAsync(
        sameStream, sameStream, sameStream.Length).GetAwaiter().GetResult());

    using var source = new MemoryStream(new byte[4096], writable: false);
    using var destination = new MemoryStream(new byte[4096], writable: true);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    AssertThrows<OperationCanceledException>(() => RawImageOperations.CopyAndVerifyAsync(
        source, destination, source.Length, cancellationToken: cancellation.Token).GetAwaiter().GetResult());
    Assert(destination.ToArray().All(value => value == 0), "cancelled image copy changed its destination");
}

static void AssertThrows<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class FatxFixture : IDisposable
{
    private const int Header = 0x1000;
    private readonly SparseStream stream;
    private readonly long offset;
    private readonly long length;
    private readonly long dataOffset;
    private readonly int clusterBytes;
    private readonly int entryBytes;
    private readonly FatxByteOrder order;
    private FatxFixture(SparseStream stream, long offset, long length, long dataOffset, long count, FatxAllocationTable table, FatxByteOrder order, uint sectorsPerCluster, int sectorSize)
    {
        this.stream = stream; this.offset = offset; this.length = length; this.dataOffset = dataOffset; ClusterCount = count; Table = table; this.order = order;
        clusterBytes = checked((int)sectorsPerCluster * sectorSize); entryBytes = table == FatxAllocationTable.Fat16 ? 2 : 4;
    }
    public long ClusterCount { get; }
    public Stream Stream => stream;
    public long Length => length;
    public FatxAllocationTable Table { get; }
    public uint EndOfChain => Table == FatxAllocationTable.Fat16 ? 0xFFFFu : 0xFFFFFFFFu;
    public uint MediaMarker => Table == FatxAllocationTable.Fat16 ? 0xFFF8u : 0xFFFFFFF8u;
    public static FatxFixture Create(long length)
    {
        var stream = new SparseStream(length);
        return WriteVolume(stream, 0, length, FatxByteOrder.LittleEndian, 2);
    }
    public static FatxFixture WriteVolume(SparseStream stream, long offset, long length, FatxByteOrder order, uint sectorsPerCluster, int sectorSize = 512)
    {
        (FatxAllocationTable table, long count, long data) = KnownGeometry(length, sectorsPerCluster, sectorSize);
        var fixture = new FatxFixture(stream, offset, length, data, count, table, order, sectorsPerCluster, sectorSize);
        Span<byte> header = stackalloc byte[Header];
        (order == FatxByteOrder.LittleEndian ? "FATX"u8 : "XTAF"u8).CopyTo(header);
        WriteUInt(header.Slice(4, 4), 0x11223344, order);
        WriteUInt(header.Slice(8, 4), sectorsPerCluster, order);
        WriteUInt(header.Slice(12, 4), 1, order);
        fixture.Write(offset, header);
        fixture.SetFat(0, fixture.MediaMarker); fixture.SetFat(1, fixture.EndOfChain);
        return fixture;
    }
    public FatxVolume Open() => FatxVolume.Open(stream, offset, length);
    public void SetFat(uint index, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (entryBytes == 2) WriteUShort(bytes, checked((ushort)value), order); else WriteUInt(bytes, value, order);
        Write(offset + Header + (long)index * entryBytes, bytes[..entryBytes]);
    }
    public void WriteEntry(uint cluster, int entryOffset, string name, byte attributes, uint firstCluster, uint fileSize)
    {
        var entry = new byte[0x40]; entry[0] = checked((byte)name.Length); entry[1] = attributes;
        System.Text.Encoding.ASCII.GetBytes(name, entry.AsSpan(2));
        WriteUInt(entry.AsSpan(0x2C, 4), firstCluster, order); WriteUInt(entry.AsSpan(0x30, 4), fileSize, order);
        Write(ClusterPosition(cluster) + entryOffset, entry);
    }
    public void WriteDeletedEntry(uint cluster, int entryOffset)
    {
        Span<byte> entry = stackalloc byte[0x40]; entry[0] = 0xE5; Write(ClusterPosition(cluster) + entryOffset, entry);
    }
    public void FillDeleted(uint cluster)
    {
        for (int offset = 0; offset < clusterBytes; offset += 0x40) WriteDeletedEntry(cluster, offset);
    }
    private long ClusterPosition(uint cluster) => offset + dataOffset + ((long)cluster - 1) * clusterBytes;
    private void Write(long at, ReadOnlySpan<byte> bytes) { stream.Position = at; stream.Write(bytes); }
    public void Dispose() => stream.Dispose();
    // Deliberately direct fixture math (not the parser's old fixed-point algorithm): these
    // values model the documented chain map, then golden tests assert known on-disk addresses.
    private static (FatxAllocationTable, long, long) KnownGeometry(long length, uint sectorsPerCluster, int sectorSize = 512)
    {
        long bytesPerCluster = checked((long)sectorsPerCluster * sectorSize);
        long mapEntries = length / bytesPerCluster + 1;
        FatxAllocationTable table = mapEntries < 0xFFF0 ? FatxAllocationTable.Fat16 : FatxAllocationTable.Fat32;
        long fatBytes = mapEntries * (table == FatxAllocationTable.Fat16 ? 2 : 4);
        long data = Header + Align(fatBytes, Header);
        return (table, (length - data) / bytesPerCluster, data);
    }
    private static long Align(long value, int alignment) => (value + alignment - 1) / alignment * alignment;
    private static void WriteUInt(Span<byte> target, uint value, FatxByteOrder order) { if (order == FatxByteOrder.LittleEndian) BinaryPrimitives.WriteUInt32LittleEndian(target, value); else BinaryPrimitives.WriteUInt32BigEndian(target, value); }
    private static void WriteUShort(Span<byte> target, ushort value, FatxByteOrder order) { if (order == FatxByteOrder.LittleEndian) BinaryPrimitives.WriteUInt16LittleEndian(target, value); else BinaryPrimitives.WriteUInt16BigEndian(target, value); }
}

sealed class SparseStream(long length) : Stream
{
    private const int BlockSize = 4096;
    private readonly Dictionary<long, byte[]> blocks = [];
    private long position;
    public long ReadOperations { get; private set; }
    public long WriteOperations { get; private set; }
    public long BytesRead { get; private set; }
    public long BytesWritten { get; private set; }
    public void ResetCounters() { ReadOperations = WriteOperations = BytesRead = BytesWritten = 0; }
    public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => true;
    public override long Length => length; public override long Position { get => position; set { if (value < 0 || value > length) throw new ArgumentOutOfRangeException(nameof(value)); position = value; } }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        int available = checked((int)Math.Min(buffer.Length, length - position));
        ReadOperations++; BytesRead += available;
        for (int i = 0; i < available; i++) { long block = (position + i) / BlockSize; int at = (int)((position + i) % BlockSize); buffer[i] = blocks.TryGetValue(block, out byte[]? data) ? data[at] : (byte)0; }
        position += available; return available;
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > length - position) throw new EndOfStreamException();
        WriteOperations++; BytesWritten += buffer.Length;
        for (int i = 0; i < buffer.Length; i++) { long block = (position + i) / BlockSize; int at = (int)((position + i) % BlockSize); if (!blocks.TryGetValue(block, out byte[]? data)) { data = new byte[BlockSize]; blocks.Add(block, data); } data[at] = buffer[i]; }
        position += buffer.Length;
    }
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(position + offset), SeekOrigin.End => checked(length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
    public override void SetLength(long value) => throw new NotSupportedException();
}

sealed class LengthlessStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => throw new IOException("The parameter is incorrect.");
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class StrictAlignedReadStream(Stream inner, int alignment) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => throw new IOException("The parameter is incorrect.");
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        if (Position % alignment != 0 || buffer.Length % alignment != 0)
            throw new IOException("Incorrect function.");
        return inner.Read(buffer);
    }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class StrictAlignedWriteStream(Stream inner, int alignment) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) { if (Position % alignment != 0 || buffer.Length % alignment != 0) throw new IOException("Incorrect function."); return inner.Read(buffer); }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) { if (Position % alignment != 0 || buffer.Length % alignment != 0) throw new IOException("Incorrect function."); inner.Write(buffer); }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
}

sealed class FailingWriteStream(Stream inner, int successfulWrites) : Stream
{
    private int writesRemaining = successfulWrites;
    public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => true;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (writesRemaining-- <= 0) throw new IOException("Injected write failure.");
        inner.Write(buffer);
    }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
}

sealed class CountingReadStream(Stream inner) : Stream
{
    public int ReadCalls { get; private set; }
    public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) { ReadCalls++; return inner.Read(buffer); }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
