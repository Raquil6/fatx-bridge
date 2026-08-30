# FATX write safety

## Whole-device imaging

- Backups always read the selected physical device and write to a uniquely named temporary file first. FATX Bridge re-reads that file, verifies its SHA-256 against the bytes copied from the device, and only then replaces the requested destination path.
- Cancelling a backup removes only FATX Bridge's incomplete temporary image. It does not modify the source device.
- Restores require the image byte length to exactly equal the selected physical-device capacity and require the user to type the target device name. Once raw writing begins, FATX Bridge prevents the window from closing and does not offer cancellation because an interrupted restore necessarily leaves an incomplete disk.
- Restore verification re-reads the complete target device and compares its SHA-256 with the bytes read from the image.
- Imaging is sector-agnostic and copies security-sector regions exactly as present. A security sector is not required; absent or bypassed security-sector layouts are neither rejected nor synthesized.

Read-only mounting is the safest default. Read/write mounting modifies the FATX volume directly and should be used only with a verified backup.

## Before writing

1. Back up irreplaceable data with a trusted imaging or backup tool.
2. Confirm that the selected physical disk and Content partition are the intended targets.
3. Use a stable, powered USB/SATA adapter and cable.
4. Avoid writing while another program has the same disk open.

## During and after writing

- Do not unplug the disk, suspend Windows, terminate FATX Bridge, or remove power during a transfer.
- Wait for File Explorer to finish and then unmount cleanly before disconnecting the disk.
- If Windows, the adapter, or the application stops unexpectedly during a write, treat the volume as potentially inconsistent and inspect it before writing again.

## Why this matters

FATX is not a journaled file system. FATX Bridge orders data and metadata updates conservatively, bounds all I/O to the validated partition, and flushes changes at lifecycle boundaries, but it cannot make a sudden disconnect or power loss atomic.

This beta does not format disks, repair damaged FATX volumes, unlock security-protected drives, or recover deleted data.
