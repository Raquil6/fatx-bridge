# FATX write safety

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
