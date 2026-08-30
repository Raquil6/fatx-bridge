# FATX Bridge 0.3.0 Beta 1

**A free and open-source way to use Xbox FATX drives on Windows.**

[![Build and test](https://github.com/Lomzlomz/fatx-bridge/actions/workflows/build.yml/badge.svg)](https://github.com/Lomzlomz/fatx-bridge/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/Lomzlomz/fatx-bridge?include_prereleases)](https://github.com/Lomzlomz/fatx-bridge/releases)
[![License](https://img.shields.io/github/license/Lomzlomz/fatx-bridge)](LICENSE)

FATX Bridge lets you connect a supported original Xbox or Xbox 360 drive to your PC, browse it, and open it in File Explorer. It is free, contains no ads or telemetry, and does not need a paid license.

## Download and install

1. Open the [0.3.0 Beta 1 release](https://github.com/Lomzlomz/fatx-bridge/releases/tag/v0.3.0-beta.1).
2. Download and run `FATXBridge-0.3.0-beta.1-Setup.exe`.
3. Open **FATX Bridge** from the Start menu.

That is all most people need. The installer includes the app and installs the required WinFsp driver automatically. You do not need to install .NET, Visual Studio, or any developer tools.

Windows may show an **Unknown publisher** warning because this beta is not code-signed yet. Only install builds downloaded from this repository, and compare the file with the included SHA-256 checksum if you are unsure.

Prefer not to use an installer? The release also includes a portable ZIP. Portable users must install [WinFsp 2.1](https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi) separately. See the [full installation guide](docs/INSTALL.md) if you need help.

## Using a drive

1. Shut down the Xbox and connect its drive or memory unit to the PC.
2. Start FATX Bridge and approve the administrator prompt.
3. Select the detected storage device and open it.
4. Choose a partition, then mount it in File Explorer.

Start with a **read-only** mount. Make a backup before enabling read/write mode, restoring an image, or experimenting with important data.

## What it can do

- Browse and mount Xbox 360 hard drives.
- Browse and mount unlocked original Xbox hard drives and memory units.
- Work with many compatible USB-to-SATA adapters and memory-unit readers.
- Copy, create, rename, and delete files through File Explorer in read/write mode.
- Back up a complete physical drive and verify the copy.
- Restore an exact-size backup with clear confirmation and verification.
- Open disk images and create new FATX image files.
- Browse old Xbox 360 `Data0000` USB storage read-only.
- Show game/package information, inspect disk sectors read-only, and recover some recently deleted files.
- Remember custom names for mounted volumes.
- Work with Xbox 360 disks that do not have a security sector.

The app keeps destructive tools away from the startup screen and explains their effect before use. It never formats a physical drive.

## Original Xbox note

Original Xbox hard drives must already be unlocked before Windows can read them. FATX Bridge does not unlock drives or use `eeprom.bin`. Memory units need a compatible reader that makes them appear as a normal Windows storage device.

## Larger Xbox 360 drives

Users running the Bad Update exploit can optionally use the independent [BadStorage fork](https://github.com/Angelpro09xd/BadStorage) to let an Xbox 360 format larger drives without normal security-sector metadata. After the console formats the drive, shut it down and connect the drive to FATX Bridge.

BadStorage is a separate project with its own requirements and risks. FATX Bridge does not install or support Bad Update, XeUnshackle, or BadStorage. Back up existing data first.

## Help and safety

- [Installation and troubleshooting](docs/INSTALL.md)
- [Safe-use guide](docs/SAFETY.md)
- [Privacy](PRIVACY.md)
- [All documentation](docs/)
- [Report a bug](https://github.com/Lomzlomz/fatx-bridge/issues)

FATX is an old filesystem without modern crash protection. Always unmount cleanly and never disconnect a drive while files are being written.

## Building from source

Developers need the .NET 10 SDK:

```powershell
dotnet build .\FatxBridge.sln -c Release
dotnet run --project .\tests\FatxBridge.Tests\FatxBridge.Tests.csproj -c Release
```

Run `build\Build-Release.ps1` with Inno Setup installed to create the self-contained portable ZIP and Windows installer.

## License

FATX Bridge is available under the [MIT License](LICENSE). Third-party notices are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

FATX Bridge is an independent community project. It is not affiliated with or endorsed by Microsoft, Xbox, FATXplorer, Eaton Works, or WinFsp.
