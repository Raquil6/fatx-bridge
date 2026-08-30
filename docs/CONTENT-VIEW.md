# Xbox 360 content metadata

`FatxBridge.Core.StfsMetadata` provides a bounded, read-only view of the metadata prefix of an
Xbox 360 STFS/XContent package. It recognizes only the exact four-byte signatures `CON `, `LIVE`,
and `PIRS`; it does not extract files, open the package's block tables, edit licenses, or write
back to a package.

The parser reads at most `0xA000` bytes from a stream. It returns explicit `Present`, `Invalid`,
`Unsupported`, or `Unavailable` results and restores the caller's stream position when possible.
Package content size is metadata only; no allocation proportional to that value is performed.

## Documented fields

The offsets are absolute package offsets from the Free60 STFS table:

- `0x340`: big-endian header size.
- `0x344` through `0x367`: big-endian content type, metadata version, content size, media ID,
  version/base version, title ID, platform, executable type, and disc fields.
- `0x411`: nine `0x100`-byte UTF-16BE display-name slots.
- `0xD11`: nine `0x100`-byte UTF-16BE description slots.
- `0x1611` and `0x1691`: UTF-16BE publisher and title names.
- `0x1711`: transfer flags.

Metadata version 2 adds series/season/episode fields at `0x3B1` and three additional localized
name/description slots at `0x541A` and `0x941A`. Xenia's maintained packed `XContentMetadata`
struct confirms these 16-bit big-endian arrays. The older Free60 table labels the localized
strings as UTF-8, so this implementation follows the maintained struct and observed XContent
representation required for safe display decoding.

## Trust boundary

The parser does not verify RSA signatures, header SHA-1 values, block hashes, licenses, or any
other cryptographic property. `CryptographicVerificationPerformed` is always `false`; parsed
values are informational metadata only and must not be treated as proof that a package is genuine
or authorized.

Sources:

- [Free60 STFS format reference](https://free60.org/System-Software/Formats/STFS/)
- [Free60 STFS source document](https://github.com/Free60Project/wiki/blob/master/docs/System-Software/Formats/STFS.md)
- [Xenia maintained STFS/XContent structures](https://github.com/xenia-project/xenia/blob/master/src/xenia/vfs/devices/stfs_xbox.h)
- [Xenia STFS container reader](https://github.com/xenia-project/xenia/blob/master/src/xenia/vfs/devices/stfs_container_device.cc)
