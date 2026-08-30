# FATX formatting core

`FatxFormatter` is an image-first, partition-bounded formatter. It accepts a
caller-owned readable, writable, seekable `Stream` and a
`FatxFormatOptions` value containing an explicit byte offset and length. It
does not enumerate disks, choose a physical partition, write an Xbox retail
partition map, create a security sector, or issue ATA commands. A caller must
create and retain the disposable image (or otherwise explicitly selected
partition stream) before invoking it.

## Geometry and API

The supported logical-sector sizes are 512 and 4096 bytes. The supported
sectors-per-cluster values are 2, 4, 8, 16, 32, 64, and 128. FATX uses the
same layout calculation as `FatxVolume`:

```text
clusterBytes = sectorsPerCluster * sectorSize
mapEntries   = floor(partitionLength / clusterBytes) + 1
table        = FAT16 when mapEntries < 0xFFF0, otherwise FAT32
entryBytes   = 2 for FAT16, 4 for FAT32
fatBytes     = mapEntries * entryBytes
fatLength    = alignUp(fatBytes, 0x1000)
dataOffset   = partitionOffset + 0x1000 + fatLength
clusterCount = floor((partitionLength - 0x1000 - fatLength) / clusterBytes)
```

The formatter validates all arithmetic and all derived ranges before its first
write. The partition offset must be aligned to `0x1000` (and therefore to the
selected logical sector size), the length must be aligned to the selected
logical sector size, and the complete range must be contained in the supplied
stream. The `0x1000` offset requirement matches `FatxVolume`'s allocation-page
reader and ensures the result can be reopened. The root cluster is always
cluster 1. Header values use the selected byte order: `FATX` and
little-endian for original-Xbox-style metadata, or `XTAF` and big-endian for
Xbox 360-style metadata. A supplied serial is used verbatim; otherwise a
cryptographically random 32-bit serial is generated before writing.

For example, with offset `0`, length `0x04000000` (64 MiB), 512-byte sectors,
and 8 sectors per cluster:

```text
clusterBytes = 0x1000
mapEntries   = 0x4001
table        = FAT16
fatBytes     = 0x8002
fatLength    = 0x9000
```

The exact boundaries are header `[0x0000, 0x1000)`, FAT allocation table
`[0x1000, 0xA000)`, and data `[0xA000, 0x04000000)`. The root cluster is
`[0xA000, 0xB000)`. FAT entry 0 at `0x1000` receives media marker `0xFFF8`
and FAT entry 1 at `0x1002` receives root EOC `0xFFFF`, encoded in the
selected byte order.

For a FAT32 example, with offset `0`, length `0x80002000` (2 GiB plus 8 KiB),
512-byte sectors, and 32 sectors per cluster:

```text
clusterBytes = 0x4000
mapEntries   = 0x20001
table        = FAT32
fatBytes     = 0x80004
fatLength    = 0x81000
clusterCount = 0x1FFE0
```

The exact boundaries are header `[0x0000, 0x1000)`, FAT allocation table
`[0x1000, 0x82000)`, and data `[0x82000, 0x80002000)`. The root cluster is
`[0x82000, 0x86000)`. FAT entry 0 at `0x1000` receives media marker
`0xFFFFFFF8` and FAT entry 1 at `0x1004` receives root EOC `0xFFFFFFFF`, in
the selected byte order.

`FatxFormatResult.Metadata` exposes the reopened parser metadata, while
`FatLength`, `RootDirectoryOffset`, and `RootDirectoryLength` expose the
validated boundaries. `FatxFormatProgress` reports a bounded byte count and a
stage through an optional `IProgress<FatxFormatProgress>` callback.

## Quick and full format ordering

Quick format first zeroes and flushes the complete 0x1000-byte header block,
invalidating any old FATX signature before it destroys metadata. It then
zeroes the complete aligned FAT allocation-table region and the complete
root-directory cluster. Full format first zeroes the entire supplied
partition range, which also invalidates its header. Both modes then:

1. flush the erased regions;
2. write FAT entry 0's media marker and entry 1's root EOC, then flush;
3. write the 0x1000-byte valid FATX header last, then flush;
4. reopen the range through `FatxVolume.Open`, verify every geometry field and
   serial, and read the root directory to confirm it is empty.

Writing the valid header last avoids advertising a new volume before its
allocation markers have been persisted. If quick-format interruption occurs
after header invalidation, the range is unrecognized and incomplete rather
than deceptively presenting the old FATX volume with damaged metadata.
Formatting is not crash-atomic. Cancellation is checked before writes and
between bounded zeroing chunks; cancellation or an I/O error can leave the
selected image/partition incomplete and it must be discarded or repaired. The
formatter never attempts rollback and never writes outside the requested
`[partitionOffset, partitionOffset + partitionLength)` range.
