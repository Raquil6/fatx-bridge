#ifndef MyAppVersion
  #define MyAppVersion "0.2.0-beta.2"
#endif

#ifndef MyAppNumericVersion
  #define MyAppNumericVersion "0.2.0.2"
#endif

#define MyAppName "FATX Bridge"
#define MyAppExeName "FatxBridge.exe"
#define WinFspMsiName "winfsp-2.1.25156.msi"

[Setup]
AppId={{B912A4F7-CC6D-4BF1-908B-3DEEF7E23E03}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=FATX Bridge contributors
AppPublisherURL=https://github.com/Lomzlomz/fatx-bridge
AppSupportURL=https://github.com/Lomzlomz/fatx-bridge/issues
AppUpdatesURL=https://github.com/Lomzlomz/fatx-bridge/releases
DefaultDirName={autopf}\FATX Bridge
DefaultGroupName=FATX Bridge
DisableProgramGroupPage=yes
AllowNoIcons=yes
LicenseFile=..\LICENSE
OutputDir=..\artifacts
OutputBaseFilename=FATXBridge-{#MyAppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
CloseApplicationsFilter=FatxBridge.exe
UninstallDisplayName=FATX Bridge
UninstallDisplayIcon={app}\{#MyAppExeName}
VersionInfoVersion={#MyAppNumericVersion}
VersionInfoProductVersion={#MyAppNumericVersion}
VersionInfoProductTextVersion={#MyAppVersion}
VersionInfoDescription=FATX Bridge Installer
VersionInfoCompany=FATX Bridge contributors
VersionInfoCopyright=Copyright (C) 2026 FATX Bridge contributors
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\installer-input\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\installer-input\prerequisites\{#WinFspMsiName}"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\FATX Bridge"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "Free FATX drive access for Windows"
Name: "{autodesktop}\FATX Bridge"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Comment: "Free FATX drive access for Windows"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\FatxBridge.exe"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\FatxBridge.exe"; ValueType: string; ValueName: "Path"; ValueData: "{app}"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch FATX Bridge"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
function WinFspIsMissing: Boolean;
var
  InstallDirectory: String;
begin
  Result := not (
    RegQueryStringValue(HKLM32, 'SOFTWARE\WinFsp', 'InstallDir', InstallDirectory) and
    DirExists(InstallDirectory)
  );
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  InstallerPath: String;
begin
  Result := '';
  if not WinFspIsMissing then
    Exit;

  ExtractTemporaryFile('{#WinFspMsiName}');
  InstallerPath := ExpandConstant('{tmp}\{#WinFspMsiName}');
  WizardForm.StatusLabel.Caption := 'Installing the signed WinFsp filesystem runtime...';
  Log('WinFsp is missing; starting the bundled official WinFsp 2.1 MSI.');

  if not Exec(
    ExpandConstant('{sys}\msiexec.exe'),
    '/i ' + AddQuotes(InstallerPath) + ' /passive /norestart',
    '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Windows could not start the WinFsp installer.';
    Exit;
  end;

  if (ResultCode = 1641) or (ResultCode = 3010) then
  begin
    NeedsRestart := True;
    Exit;
  end;

  if ResultCode <> 0 then
    Result := 'WinFsp installation failed with MSI exit code ' + IntToStr(ResultCode) +
      '. FATX Bridge was not installed.';
end;
