# FATX Bridge 0.2.0 Beta 3

**Free FATX drive access for Windows.**

[![Build and test](https://github.com/Lomzlomz/fatx-bridge/actions/workflows/build.yml/badge.svg)](https://github.com/Lomzlomz/fatx-bridge/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/Lomzlomz/fatx-bridge?include_prereleases)](https://github.com/Lomzlomz/fatx-bridge/releases)
[![License](https://img.shields.io/github/license/Lomzlomz/fatx-bridge)](LICENSE)

FATX Bridge is a free Windows utility for accessing original Xbox and Xbox 360 FATX storage. It detects supported physical disks and memory units, validates their FATX layouts, browses files inside the app, and mounts a selected partition in File Explorer through WinFsp.

## Install FATX Bridge

### Recommended: Windows installer

1. Open the [FATX Bridge 0.2.0 Beta 3 release](https://github.com/Lomzlomz/fatx-bridge/releases/tag/v0.2.0-beta.3).
2. Download `FATXBridge-0.2.0-beta.3-Setup.exe`.
3. Run the installer and approve the Windows administrator prompt.
4. Leave **Launch FATX Bridge** selected, or open it later by searching for **FATX Bridge** in the Start menu.

The installer contains the self-contained .NET application and installs the signed WinFsp 2.1 Core runtime when WinFsp is not already present. You do not need Visual Studio, the .NET SDK, or a separate .NET download. Uninstalling FATX Bridge does not remove WinFsp because other filesystem applications may use the same driver.

The beta installer is not code-signed yet, so Windows SmartScreen may identify it as an unknown publisher. Only continue if it came from this repository's Releases page and its SHA-256 matches the value published with the release.

### Portable installation

1. Install the stable [WinFsp 2.1 MSI](https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi) with its default **Core** feature.
2. Download `FATXBridge-0.2.0-beta.3-win-x64-portable.zip` from the [release](https://github.com/Lomzlomz/fatx-bridge/releases/tag/v0.2.0-beta.3).
3. Right-click the ZIP, select **Extract All**, and run `FatxBridge.exe` from the extracted folder.

The portable build also includes .NET, but it cannot mount a drive until the signed WinFsp driver is installed. See [docs/INSTALL.md](docs/INSTALL.md) for troubleshooting and manual verification.

### First use

1. Shut down the console and connect its FATX disk or memory unit to the PC through a compatible direct connection, USB-to-SATA adapter, or memory-unit reader.
2. Open FATX Bridge from the Start menu and approve the administrator prompt used by its narrowly scoped physical-drive helper.
3. Select the detected device and desired FATX partition.
4. Choose a read-only mount first. Use experimental read/write mode only after making a backup.

## Features

- Detects supported Xbox 360 hard drives, including drives connected through compatible USB-to-SATA adapters.
- Detects unlocked original Xbox hard drives with fixed retail layouts, XBPartitioner tables, or legacy extended partitions.
- Detects original Xbox memory units exposed to Windows as whole physical storage devices, including their 4 KiB FATX logical-sector geometry.
- Validates FATX structures before presenting a disk.
- Browses nested FATX directories without mounting.
- Mounts a validated writable partition read-only or read/write in File Explorer.
- Supports file and directory creation, reads, writes, truncation, rename, and deletion.
- Uses buffered, bounded writes designed for practical large-file transfer speeds.
- Makes no network requests and includes no telemetry.

## System requirements

- Windows 10 or later, 64-bit.
- The signed [WinFsp](https://github.com/winfsp/winfsp) Core runtime. The recommended installer handles this automatically.
- Administrator approval when opening a protected physical disk. The mounted File Explorer view runs in the desktop user's session.

Original Xbox hard drives must already be ATA-unlocked before Windows can read their FATX partitions. FATX Bridge does not derive HDD passwords, consume `eeprom.bin`, or issue ATA security commands. Original Xbox memory units require a compatible USB adapter or reader that exposes the card as a Windows physical disk.

The published installer and portable package are self-contained and do not require a separate .NET installation. Building from source requires the .NET 10 SDK.

## Large internal drives with BadStorage

Advanced users running the Xbox 360 **Bad Update** exploit may be interested in [Angelpro09xd/BadStorage](https://github.com/Angelpro09xd/BadStorage), a third-party fork that adds support for disks that never passed the console's security-sector check. Its project page advertises support for up to 2 TB and reports testing from a 240 GB SSD through a 1 TB hard disk.

The fork requires a retail Xbox 360 on kernel 17559 with Bad Update and XeUnshackle. Follow its instructions exactly. Its README says holding LT can reformat the disk from the console; after the console creates the FATX layout, shut it down fully, connect the disk to the PC, and open it with FATX Bridge. A normally structured Xbox 360 FATX disk should then be detectable without HDD Maker or SSD Maker metadata.

Important limitations from the BadStorage project:

- Its in-memory changes must be applied again after a cold reboot or shutdown.
- An unauthenticated internal disk cannot itself provide an exploit entry point that needs to read from that disk before the bypass runs; USB-based entry points are unaffected.
- FATX Bridge does not install, modify, or provide support for Bad Update, XeUnshackle, or BadStorage.
- Back up existing data before formatting or experimenting with an internal disk.

BadStorage is an independent project and is not maintained or endorsed by FATX Bridge.

## Build and verify

```powershell
dotnet build .\FatxBridge.sln -c Release
dotnet run --project .\tests\FatxBridge.Tests\FatxBridge.Tests.csproj -c Release
Start-Process .\app\bin\Release\net10.0-windows\FatxBridge.exe -Verb RunAs
```

To build the self-contained portable package and installer, install Inno Setup 6 or 7 and run:

```powershell
.\build\Build-Release.ps1
```

The automated tests use disposable synthetic streams. The broker and mount probes create temporary FATX images and do not open a physical disk for writing.

## Safety

Physical disks and mounts are read-only unless the user explicitly selects the experimental read/write mode and accepts its warning. A disk is shown only after FATX headers, media markers, allocation structures, partition boundaries, and the root directory chain pass validation.

FATX is not journaled. Although FATX Bridge orders allocation, data, FAT links, and directory metadata to reduce inconsistent states, no application can make an unexpected disconnect or power loss crash-atomic. Keep a backup, unmount cleanly, and never disconnect a disk during a write. See [docs/SAFETY.md](docs/SAFETY.md).

## Current scope

The detector supports standard retail Xbox 360 HDD layouts, unlocked original Xbox fixed HDD layouts, bounded active entries in the 14-slot XBPartitioner table, legacy F/G locations, and whole-device original Xbox memory units. Formatting, ATA/security-sector unlocking, disk imaging, data recovery, Xbox 360 `Data0000` USB containers, and STFS package management are not part of this beta.

## Privacy and diagnostics

FATX Bridge has no network telemetry. Diagnostic files are stored locally under `%LOCALAPPDATA%\FatxBridge`. Detailed callback logging is enabled only when `FATXBRIDGE_WINFSP_DEBUG=1`; local write-performance logging is enabled only when `FATXBRIDGE_PERFORMANCE_LOG=1`. See [PRIVACY.md](PRIVACY.md).

## License and acknowledgements

FATX Bridge is released under the [MIT License](LICENSE). It uses WinFsp through the `winfsp.net` package; WinFsp has its own GPLv3 terms and Free/Libre and Open Source Software exception. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) before redistributing binaries.

The FATX behavior and device layouts were independently implemented using public Xbox format documentation plus [emoose/xbox-winfsp](https://github.com/emoose/xbox-winfsp), [aerosoul94/FATXTools](https://github.com/aerosoul94/FATXTools), and [mborgerson/fatx](https://github.com/mborgerson/fatx) as design references. Their source was not copied; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for licenses and links.

FATX Bridge is an independent community project. It is not affiliated with, endorsed by, or sponsored by Microsoft, Xbox, FATXplorer, Eaton Works, WinFsp, or their respective owners. Product names and trademarks belong to their owners.
