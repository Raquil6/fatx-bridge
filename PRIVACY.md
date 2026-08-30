# Privacy

FATX Bridge does not include analytics, advertising, update checks, crash uploads, or network telemetry. It does not send drive names, file names, device identifiers, performance measurements, or application usage anywhere.

The application can create local diagnostic files under `%LOCALAPPDATA%\FatxBridge`:

- `drive-scan.log` records read-only physical-device discovery.
- `broker-probe.log`, `mount-probe.log`, and `write-benchmark.log` record explicit diagnostic probes.
- `mount-broker.log` and `elevated-drive-host.log` / `elevated-drive-host-steps.log` record broker diagnostics.
- `fatx-callback.log` and `winfsp-debug.log` require `FATXBRIDGE_WINFSP_DEBUG=1`.
- `write-performance.log` requires `FATXBRIDGE_PERFORMANCE_LOG=1`.
- `volume-labels.json` stores local label preferences and is not a support diagnostic.

Best-effort device information shown in the app excludes the device serial number. When a structurally validated Xbox 360 HDD security-sector payload is present, FATX Bridge stores a hash-named local backup under `%LOCALAPPDATA%\FatxBridge\SecuritySectors`. Its JSON manifest contains a SHA-256 fingerprint, byte length, format name, and archive time; it does not contain the source drive path or detected model. Security-sector backups are never included in support ZIPs.

These files remain on the local computer until the user deletes them. A user should review a diagnostic file before sharing it because it may contain physical-drive paths, mounted paths, or FATX file names.

## Support package export

The optional support-package exporter creates a ZIP locally. It does not upload
anything, make a network request, or include the selected destination path in
the package. The ZIP contains a manifest and only the ten exact allowlisted log
filenames listed above. It does not include FATX user files, mounted-volume
contents, image files (`*.img`, `*.bin`, or `*.fatx`), raw sectors, security
sector backups, `volume-labels.json`, arbitrary files, or recursively scanned
directories.

Each log is limited to a 1 MiB prefix and the combined uncompressed diagnostic
content is limited to 8 MiB. Reparse-point files and non-text payloads are
skipped. The manifest records every included and skipped allowlisted log, any
truncation, the size limits, and redaction counts. Common local/profile/temp
paths, mount paths, physical-drive numbers, pipe IDs/GUIDs, and obvious serial
number fields are redacted where recognized. A redaction can remove context,
and log lines can still contain FATX filenames or other sensitive details.

Review the ZIP and its manifest before sharing it. Exporting a package does not
grant anyone access to the source drive and does not delete the local logs.
