# Changelog

## 0.2.0 Beta 3

- Added detection and mounting for unlocked original Xbox hard drives.
- Added fixed C/E/X/Y/Z layouts, bounded XBPartitioner table parsing, and legacy F/G probing.
- Added whole-device original Xbox FATX memory-unit detection with the correct 4 KiB logical-sector geometry.
- Enabled explicitly confirmed experimental read/write mounting on validated original Xbox partitions and memory units.
- Corrected original Xbox FATX timestamps to use their 2000-based year encoding while preserving Xbox 360's 1980-based encoding.
- Added malformed and overlapping XBPartitioner safety tests.

## 0.2.0 Beta 2

- Added a one-click Windows installer with Start menu and optional desktop shortcuts.
- Changed release packages to self-contained .NET builds.
- Added automatic installation of the stable signed WinFsp 2.1 runtime when missing.
- Added beginner-friendly installation, first-use, and troubleshooting documentation.
- Documented the optional third-party BadStorage fork and its verified limits and caveats.

## 0.2.0 Beta

- Introduced the independent FATX Bridge product identity and public MIT-licensed project structure.
- Added Xbox 360 FATX physical-disk detection through compatible direct and USB-attached devices.
- Added nested FATX browsing and WinFsp-backed File Explorer mounts.
- Added explicit read-only and experimental read/write mounting for the Content partition.
- Added create, write, resize, rename, and authoritative File Explorer deletion support.
- Added bounded, asynchronous 4 MiB write buffering for substantially faster large transfers.
- Added synthetic mount, persistence, deletion, deferred-length, and bulk-write verification probes.
- Made detailed callback and performance diagnostics opt-in and local-only.
