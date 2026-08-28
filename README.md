# FATX Bridge 0.2.0 Beta

**Free FATX drive access for Windows.**

FATX Bridge is a free Windows utility for accessing Xbox 360 FATX storage. It detects supported physical disks, validates their FATX partition layouts, browses files inside the app, and mounts the Content partition in File Explorer through WinFsp.

## Features

- Detects supported Xbox 360 hard drives, including drives connected through compatible USB-to-SATA adapters.
- Validates FATX structures before presenting a disk.
- Browses nested FATX directories without mounting.
- Mounts the Content partition read-only or read/write in File Explorer.
- Supports file and directory creation, reads, writes, truncation, rename, and deletion.
- Uses buffered, bounded writes designed for practical large-file transfer speeds.
- Makes no network requests and includes no telemetry.

## Requirements

- Windows 10 or later, 64-bit.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) to run a framework-dependent build, or the .NET 10 SDK to build from source.
- The signed [WinFsp](https://github.com/winfsp/winfsp) runtime. Install its Core feature before mounting a drive.
- Administrator approval when opening a protected physical disk. The mounted File Explorer view runs in the desktop user's session.

## Build and verify

```powershell
dotnet build .\FatxBridge.sln -c Release
dotnet run --project .\tests\FatxBridge.Tests\FatxBridge.Tests.csproj -c Release
Start-Process .\app\bin\Release\net10.0-windows\FatxBridge.exe -Verb RunAs
```

The automated tests use disposable synthetic streams. The broker and mount probes create temporary FATX images and do not open a physical disk for writing.

## Safety

Physical disks and mounts are read-only unless the user explicitly selects the experimental read/write mode and accepts its warning. A disk is shown only after FATX headers, media markers, allocation structures, partition boundaries, and the root directory chain pass validation.

FATX is not journaled. Although FATX Bridge orders allocation, data, FAT links, and directory metadata to reduce inconsistent states, no application can make an unexpected disconnect or power loss crash-atomic. Keep a backup, unmount cleanly, and never disconnect a disk during a write. See [docs/SAFETY.md](docs/SAFETY.md).

## Current scope

The physical-disk detector targets standard retail Xbox 360 FATX partition locations and big-endian `XTAF` volumes. The drive-neutral parser also understands little-endian FATX. Formatting, security-sector unlocking, disk imaging, data recovery, and STFS package management are not part of this beta.

## Privacy and diagnostics

FATX Bridge has no network telemetry. Diagnostic files are stored locally under `%LOCALAPPDATA%\FatxBridge`. Detailed callback logging is enabled only when `FATXBRIDGE_WINFSP_DEBUG=1`; local write-performance logging is enabled only when `FATXBRIDGE_PERFORMANCE_LOG=1`. See [PRIVACY.md](PRIVACY.md).

## License and acknowledgements

FATX Bridge is released under the [MIT License](LICENSE). It uses WinFsp through the `winfsp.net` package; WinFsp has its own GPLv3 terms and Free/Libre and Open Source Software exception. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) before redistributing binaries.

The standard partition offsets and FATX behavior were independently implemented using the MIT-licensed [emoose/xbox-winfsp](https://github.com/emoose/xbox-winfsp) and [aerosoul94/FATXTools](https://github.com/aerosoul94/FATXTools) as design references. Their source was not copied.

FATX Bridge is an independent community project. It is not affiliated with, endorsed by, or sponsored by Microsoft, Xbox, FATXplorer, Eaton Works, WinFsp, or their respective owners. Product names and trademarks belong to their owners.
