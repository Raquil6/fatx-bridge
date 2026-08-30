# FATX mount attribute fidelity

`FatxWinFspFileSystem` exposes the FATX directory-entry attribute byte through WinFsp's
`FileInfo.FileAttributes` and `GetSecurityByName` results. The mapping is centralized in the
pure `MapFatxAttributes` helper so an entry has the same attributes during open, metadata
refresh, directory enumeration, and security probing.

| FATX bit | Meaning | Windows attribute |
| ---: | --- | --- |
| `0x01` | Read-only | `FILE_ATTRIBUTE_READONLY` (`0x01`) |
| `0x02` | Hidden | `FILE_ATTRIBUTE_HIDDEN` (`0x02`) |
| `0x04` | System | `FILE_ATTRIBUTE_SYSTEM` (`0x04`) |
| `0x10` | Directory | `FILE_ATTRIBUTE_DIRECTORY` (`0x10`) |
| `0x20` | Archive | `FILE_ATTRIBUTE_ARCHIVE` (`0x20`) |

Unknown FATX bits are not invented into Windows attributes. `FILE_ATTRIBUTE_NORMAL` (`0x80`)
is returned only for a non-directory entry with no mapped attribute bits. A read-only mount may
add the Windows read-only bit to every returned entry as a mount policy; this does not alter the
FATX directory entry on disk.

The existing FATX core has no safe attribute-update primitive and the WinFsp provider does not
implement a `SetBasicInfo` write path. Consequently, create-time Windows attributes other than
directory type are informational only, and attempts to change attributes are not persisted. The
mount never fabricates a FATX metadata write. File contents, volume serial number, filesystem
name, and the existing callback performance behavior are unchanged.

The Windows flag meanings follow Microsoft's [file attribute constants](https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants).
