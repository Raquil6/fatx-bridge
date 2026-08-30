# Read-only sector and cluster viewer

FATX Bridge's raw-storage inspection surface is deliberately narrow. It is a
diagnostic reader, not a disk editor, repair tool, or file browser. The core
engine accepts a readable, seekable stream that the caller has already opened
and authorized; the viewer never receives a physical-drive path and never opens
one itself.

## Core API

`FatxBridge.Core.RawStorageInspector` is constructed with:

```csharp
var inspector = new RawStorageInspector(
    authorizedStream,
    authoritativeSourceLength,
    logicalSectorSize: 512);
```

The stream remains owned by the caller. The inspector does not dispose it,
change its length, flush it, or write to it. Each read restores the stream
position on a best-effort basis, including when a read fails. The supplied
length is authoritative: a range that crosses it is rejected even when the
underlying stream happens to be longer.

Available read helpers are:

- `ReadRange(absoluteOffset, count)` for an absolute byte range;
- `ReadSector(sectorNumber, sectorCount)` using the configured logical sector
  size;
- `ReadFatxHeader(metadata)` for the first 4096 bytes of a validated partition;
- `ReadFatxAllocationTable(metadata, relativeOffset, count)` for a bounded FAT
  region; and
- `ReadFatxCluster(metadata, clusterNumber)` for a one-based FATX data cluster.

`RawStorageView` reports the absolute offset, actual and requested lengths,
authoritative source length, optional partition-relative offset, sector/cluster
context, end-of-source status, and truncation status. Its `HexDump` uses fixed
16-byte lines, uppercase hexadecimal bytes, fixed-width uppercase addresses,
and an ASCII column. The view also exposes defensive-copy bytes for export.

Every view is capped at 64 KiB. A larger in-bounds request returns the first
64 KiB and sets `IsTruncated`; it does not allocate an unbounded buffer. A
negative value, checked-arithmetic overflow, or range outside the authoritative
source/validated partition is rejected. A sector size must be a power of two
from 512 through 65536 bytes; 512 and 4096 are the normal FATX defaults.

FATX cluster arithmetic validates the partition offset and length, FAT/data
offsets, sectors-per-cluster, cluster count, and the complete data-area range
before reading. Cluster numbers are one-based. The cluster helper returns a
partition-relative offset as well as the absolute source offset so callers do
not confuse the two coordinate systems.

## WPF window

`SectorViewerWindow` is available from the selected partition's **Sector viewer…** button:

```csharp
var window = new SectorViewerWindow(inspector, validatedVolumeMetadata);
window.Show();
```

The caller supplies the already-authorized inspector and, optionally, validated
`FatxVolumeMetadata`. The window provides absolute-offset, logical-sector, and
FATX-cluster navigation, previous/next navigation, refresh, and export of the
currently displayed bytes. Labels always distinguish absolute offsets from
partition-relative offsets. A persistent **READ-ONLY** banner states that the
source is not modified.

The window contains no source picker and no write, edit, patch, search, repair,
or restore controls. It only displays the bounded model returned by the core
reader. If metadata is not supplied, cluster navigation is unavailable rather
than inferred from untrusted input.

## Export behavior

“Export current bytes…” opens the normal Windows save dialog and writes only the
currently displayed view to the chosen file. The window first writes a uniquely
named temporary file adjacent to the destination, flushes it to disk, and then
publishes it with an atomic same-directory move. Cancellation or failure removes
the temporary file and does not claim that a completed export exists. The
destination is an ordinary user-selected file; no network access or upload is
performed. The source stream is never used as the export destination.

Users should still review an exported binary file before sharing it. The viewer
is privacy-conscious and bounded, but raw bytes can contain filesystem metadata,
identifiers, or user-chosen content at the exact range requested. Do not share
an export unless the selected offset and context are appropriate for the
troubleshooting request.

## Security boundary and limitations

This surface intentionally does not enumerate drives, resolve mount paths,
interpret arbitrary directories, expose image contents by filename, or provide
any mutation primitive. Authorization, stream lifetime, and the decision about
which range may be inspected remain with the caller. The viewer's export is a
copy of displayed diagnostic bytes, not a backup and not a recovery operation.

The engine validates metadata supplied by the caller, but it does not discover
or repair a corrupted FATX layout. A partial final raw range is valid when the
caller requests exactly the bytes remaining before the authoritative end; a
full sector or cluster that would extend beyond that end is rejected. The
64 KiB cap applies equally to large sector-count and large-cluster views and is
reported in the model/UI.

## Manual smoke scenario

For a patterned `MemoryStream` of 16,384 bytes and logical sector size 512:

1. Set the stream position to 77 and call `ReadSector(2)`. The returned
   absolute offset is 1024, the bytes are the pattern beginning at 1024, and
   the stream position is 77 afterward.
2. Call `ReadRange(16_380, 4)`. It succeeds as the last partial raw range,
   reports length 4 and `IsAtEnd == true`, and its exported bytes exactly equal
   the four patterned source bytes.
3. Call `ReadRange(16_381, 4)`. It is rejected before the stream is read because
   the requested range crosses the authoritative source length.
4. Supply validated metadata with `PartitionOffset = 512`,
   `PartitionLength = 8192`, `DataOffset = 1536`, `SectorsPerCluster = 2`,
   `SectorSize = 512`, and `ClusterCount = 6`. `ReadFatxCluster(metadata, 2)`
   resolves to absolute offset 2560 and reads exactly 1024 bytes (or the first
   64 KiB for a larger cluster), with partition-relative offset 2048.
5. Open the WPF window with that inspector and metadata. Sector and cluster
   navigation show the same offsets and bytes; exporting the current view
   produces exactly the displayed byte array and never changes the source
   position or contents.
