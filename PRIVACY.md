# Privacy

FATX Bridge does not include analytics, advertising, update checks, crash uploads, or network telemetry. It does not send drive names, file names, device identifiers, performance measurements, or application usage anywhere.

The application can create local diagnostic files under `%LOCALAPPDATA%\FatxBridge`:

- Basic scan and explicit probe logs support troubleshooting.
- WinFsp callback diagnostics require `FATXBRIDGE_WINFSP_DEBUG=1`.
- Write-performance measurements require `FATXBRIDGE_PERFORMANCE_LOG=1`.

These files remain on the local computer until the user deletes them. A user should review a diagnostic file before sharing it because it may contain physical-drive paths, mounted paths, or FATX file names.
