# Installing FATX Bridge

## Recommended installer

1. Download `FATXBridge-0.2.0-beta.3-Setup.exe` from the official [FATX Bridge releases page](https://github.com/Lomzlomz/fatx-bridge/releases/tag/v0.2.0-beta.3).
2. Run the installer and approve its administrator prompt.
3. Choose whether to add a desktop shortcut, then select **Install**.
4. Launch FATX Bridge from the final installer page or search for **FATX Bridge** in the Windows Start menu.

The installer places FATX Bridge under `C:\Program Files\FATX Bridge`, adds a Start menu shortcut, and installs the official signed WinFsp 2.1 runtime if WinFsp is missing. The application is self-contained, so a separate .NET runtime is not required.

FATX Bridge can be removed from **Settings > Apps > Installed apps**. Its uninstaller deliberately leaves WinFsp installed because other applications may depend on the shared filesystem driver. WinFsp can be removed separately from Installed apps when no other program needs it.

## Windows SmartScreen

The beta installer is not code-signed. Windows may display an **Unknown publisher** or SmartScreen warning even when the download is intact. Verify that the installer came from `https://github.com/Lomzlomz/fatx-bridge/releases` and compare its SHA-256 with the release notes before choosing **More info > Run anyway**.

You can calculate the checksum in PowerShell:

```powershell
Get-FileHash .\FATXBridge-0.2.0-beta.3-Setup.exe -Algorithm SHA256
```

## Portable installation

1. Install the stable [WinFsp 2.1 MSI](https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi).
2. Keep the default **Core** feature selected and complete the WinFsp setup.
3. Download and extract `FATXBridge-0.2.0-beta.3-win-x64-portable.zip`.
4. Run `FatxBridge.exe` from the extracted directory.

Do not run FATX Bridge from inside the ZIP preview. Extract the complete package first so its supporting files remain beside the executable.

## Troubleshooting

### Windows asks for the .NET Desktop Runtime

Use Beta 2 or newer. Earlier framework-dependent packages required the [.NET 10 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/runtime-desktop-10.0.10-windows-x64-installer).

### The disk appears in another FATX tool but not FATX Bridge

- Start FATX Bridge through its Start menu shortcut and approve the administrator prompt.
- Try another USB port, cable, or powered USB-to-SATA adapter.
- Confirm that the device uses one of the supported Xbox 360 or original Xbox FATX layouts.
- Review `%LOCALAPPDATA%\FatxBridge\drive-scan.log`.

For an original Xbox HDD, unlock it with appropriate Xbox tooling before connecting it to FATX Bridge. The matching console EEPROM/HDD key may be required. FATX Bridge deliberately does not attempt ATA unlocking. Original Xbox memory units must appear as a physical disk in Windows through a compatible reader.

### Mounting reports that WinFsp is unavailable

- Confirm that **WinFsp 2025** appears under **Settings > Apps > Installed apps**.
- Install or repair WinFsp 2.1 using the official MSI linked above.
- Restart Windows once if the driver was installed but the mount still fails.

### File Explorer cannot open the mount

Unmount it in FATX Bridge, close any stale File Explorer window for that mount, and mount it again. If a transfer or application still holds an open file handle, close that application before unmounting.
