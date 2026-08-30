# Changelog

## 0.3.0 Beta 1 — 2026-08-30

- Rebuilt the desktop interface as Xbox 360 dashboard-inspired tabs—Storage, Browse, Tools, and About—so uncommon and destructive operations are no longer exposed on startup.
- Added contextual Browse-tab animation, keyboard tab navigation, distinct navigation/button sounds, and a persistent sound toggle.
- Added opening and mounting complete FATX disk images and standalone FATX partition images.
- Added cancellable full-physical-device backups written through a temporary file and verified with SHA-256 before publication.
- Added exact-capacity full-device restore with typed target confirmation and complete post-write SHA-256 verification.
- Added locally remembered, user-defined Explorer volume labels and FATX16/FATX32 mount names.
- Imaging copies every byte exactly and never requires an Xbox 360 security sector, preserving compatibility with BadStorage-style disks.
- Added best-effort device/bus/sector/health details without displaying device serial numbers.
- Added automatic local backup of structurally validated Xbox 360 security-sector payloads; missing security sectors remain supported.
- Added image-only FATX quick/full formatting with crash-safe header publication and reopen validation.
- Added conservative read-only deleted-file candidate scanning and export with cluster-reuse refusal.
- Added bounded read-only CON/LIVE/PIRS metadata inspection.
- Added read-only legacy Xbox 360 `Data0000` segmented-container browsing.
- Added a 64 KiB-capped read-only sector/cluster viewer with exact-byte export.
- Added redacted allowlist-only diagnostic support-package export.
- Added Explorer attribute fidelity for FATX read-only, hidden, system, directory, and archive bits.

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
