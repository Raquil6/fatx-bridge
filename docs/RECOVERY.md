# Read-only deleted-file recovery

FATX Bridge exposes a conservative recovery API for forensic inspection of
deleted directory slots. It does not repair, undelete, or rewrite the FATX
volume. Existing directory enumeration continues to hide deleted entries.

## What is discovered

`FatxVolume.EnumerateDeletedEntries("/")` scans deleted `0xE5` slots in the
root directory. Pass a currently live directory path such as `/Content` to
scan that directory. A deleted directory is listed as a candidate, but this
first version does not recursively enumerate or extract its former contents.
The same 4096-entry safety bound used by normal FATX directory parsing limits
one recovery scan.

For each candidate, FATX Bridge parses the attribute byte, first cluster, file
size, and all three packed timestamps using the volume's byte order. Every
cluster and transfer range is checked against the validated partition. The
candidate also reports a best-effort `RecoveredName`: the remaining 42-byte
name field is accepted only when it is plausible printable ASCII up to the
first `0x00` or `0xFF` padding byte. Otherwise a stable `Deleted_<slot offset>`
label is used. FATX deletion overwrites the original name-length byte with
`0xE5`, so the original name length is never reported as known.

Candidates are classified explicitly:

- `InvalidMetadata`: attributes, timestamps, first cluster, or the candidate
  data range are invalid.
- `ZeroLength`: no file data needs to be recovered.
- `ContiguousUnallocated`: every cluster in the hypothetical contiguous range
  is currently free; this is the only nonempty-file class eligible for reads.
- `ClustersReusedOrOverwritten`: at least one candidate cluster is currently
  allocated, so extraction is refused.
- `Uncertain`: the allocation state cannot be established or the candidate is a
  deleted directory, which is not extracted by this version.

## Best-effort reads

`ReadDeletedFile(candidate, offset, target)` and
`ExportDeletedFile(candidate, destination)` are read-only operations. They
revalidate that the slot is still deleted, that its parent directory is still
the same live directory, and that every cluster in the complete contiguous
range is still free before reading. If any cluster is allocated, the operation
throws `FatxRecoveryException` rather than following another file's live FAT
chain. Reads are directly bounded to the candidate's partition range and the
32-bit FATX file-size field; exports use bounded chunks and support
cancellation.

This is necessarily best effort. FATX deletion clears the original FAT chain,
so the contiguous range is only an assumption. A free cluster can already have
been overwritten, and a former file may have used a noncontiguous chain. Even
when all candidate clusters are free, recovered bytes are not proof of an
intact original file. Do not write recovered bytes back to the source volume.

## Manual smoke scenario

An in-memory validation scenario is: create an in-memory FATX volume, create
`/RECOVER.ME`, write a short byte sequence, delete the file, then call
`EnumerateDeletedEntries("/")`. The live `EnumerateDirectory("/")` result still
omits the file; the recovery result contains one candidate with the retained
ASCII name, `OriginalNameLengthKnown == false`, the original size, and
`ContiguousUnallocated` while its former cluster range remains free. Calling
`ReadDeletedFile(candidate, 0, buffer)` reads no more than the requested
buffer length and no more than the recorded file size. Allocating another file
on that former cluster changes the candidate to
`ClustersReusedOrOverwritten` (or causes the read-time recheck to refuse it),
so recovery never reads the newly allocated file through the deleted entry.
