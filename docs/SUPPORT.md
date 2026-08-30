# Support package

FATX Bridge can create a diagnostic ZIP for a support request. The export is a
local file operation: it performs no upload, telemetry, or network request.
Choose a local destination and inspect the completed ZIP before sharing it.

## Included

The package always contains `manifest.json`. It may contain the following exact
log files under `diagnostics/`, when they exist and can be read:

- `broker-probe.log`
- `drive-scan.log`
- `elevated-drive-host-steps.log`
- `elevated-drive-host.log`
- `fatx-callback.log`
- `mount-broker.log`
- `mount-probe.log`
- `winfsp-debug.log`
- `write-benchmark.log`
- `write-performance.log`

The manifest records application/runtime and Windows metadata, the fixed size
limits, every included diagnostic, every skipped allowlisted diagnostic, and
redaction counts. It does not record the export destination path. ZIP entries
use stable names and timestamps so repeated exports of unchanged diagnostics
are comparable.

## Excluded and bounded

The exporter checks the ten names above directly and never enumerates or
recurses through the diagnostics directory. It never includes FATX user files,
mounted-volume contents, arbitrary directories, image contents, raw sectors,
security-sector backups, or application state such as `volume-labels.json`.
Files named `*.img`, `*.bin`, `*.fatx`, and arbitrary crash dumps are not on the
allowlist. Reparse-point files and files that do not look like text diagnostics
are skipped.

Each individual diagnostic contributes at most 1 MiB. The combined
uncompressed diagnostic content contributes at most 8 MiB. An oversized log is
represented by a redacted prefix and marked `truncated` in the manifest; logs
that arrive after the total limit are recorded as skipped. A failure to read one
log does not make the other allowlisted logs eligible.

The exporter redacts recognized local application-data, application-data,
temporary-directory, and user-profile paths; mount paths; physical-drive
numbers; pipe IDs; GUIDs; and obvious serial-number fields. This improves
privacy but cannot identify every sensitive value. Diagnostic text may still
contain FATX filenames, labels, error details, or other information you do not
want to disclose.

## Before sending

1. Open the ZIP and confirm that `manifest.json` lists only the diagnostics you
   expect.
2. Search the included logs for personal information, FATX filenames, drive
   labels, or identifiers that the redaction rules did not recognize.
3. Remove the ZIP if you no longer need it. Exporting does not delete the local
   source logs.
