[CmdletBinding()]
param(
    [string]$Version = '0.2.0-beta.2',
    [string]$NumericVersion = '0.2.0.2'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
$installerInput = Join-Path $artifactsRoot 'installer-input'
$applicationOutput = Join-Path $installerInput 'app'
$prerequisiteOutput = Join-Path $installerInput 'prerequisites'
$portableDirectory = Join-Path $artifactsRoot "FATXBridge-$Version-win-x64-portable"
$portableArchive = Join-Path $artifactsRoot "FATXBridge-$Version-win-x64-portable.zip"
$installerPath = Join-Path $artifactsRoot "FATXBridge-$Version-Setup.exe"
$checksumsPath = Join-Path $artifactsRoot "FATXBridge-$Version-SHA256.txt"

function Remove-GeneratedPath([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if (-not $resolved.StartsWith($artifactsRoot + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove a path outside the artifacts directory: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
Remove-GeneratedPath $installerInput
Remove-GeneratedPath $portableDirectory
Remove-GeneratedPath $portableArchive
Remove-GeneratedPath $installerPath
Remove-GeneratedPath $checksumsPath
New-Item -ItemType Directory -Path $applicationOutput, $prerequisiteOutput -Force | Out-Null

$project = Join-Path $repositoryRoot 'app\FatxBridge.Windows.csproj'
& dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version `
    -p:InformationalVersion=$Version -o $applicationOutput
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$documentation = @(
    'README.md',
    'LICENSE',
    'THIRD_PARTY_NOTICES.md',
    'PRIVACY.md',
    'CHANGELOG.md'
)
foreach ($name in $documentation) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination $applicationOutput
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs') -Destination (Join-Path $applicationOutput 'docs') -Recurse

Copy-Item -LiteralPath $applicationOutput -Destination $portableDirectory -Recurse
Compress-Archive -Path (Join-Path $portableDirectory '*') -DestinationPath $portableArchive

$winFspName = 'winfsp-2.1.25156.msi'
$winFspPath = Join-Path $prerequisiteOutput $winFspName
$winFspUri = 'https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi'
$winFspSha256 = '073A70E00F77423E34BED98B86E600DEF93393BA5822204FAC57A29324DB9F7A'
Invoke-WebRequest -Uri $winFspUri -OutFile $winFspPath
$actualWinFspHash = (Get-FileHash -LiteralPath $winFspPath -Algorithm SHA256).Hash
if ($actualWinFspHash -ne $winFspSha256) {
    throw "WinFsp MSI checksum mismatch. Expected $winFspSha256 but received $actualWinFspHash."
}

$innoCandidates = @(
    (Join-Path $repositoryRoot '.tools\innosetup\ISCC.exe'),
    (Join-Path $repositoryRoot '.tools\innosetup\ISCC-x64.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 7\ISCC.exe',
    'C:\Program Files (x86)\Inno Setup 7\ISCC.exe'
)
$innoCompiler = $innoCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if ($null -eq $innoCompiler) {
    throw 'Inno Setup compiler not found. Install Inno Setup 6 or 7, or place ISCC.exe under .tools\innosetup.'
}

$installerScript = Join-Path $repositoryRoot 'installer\FatxBridge.iss'
& $innoCompiler "/DMyAppVersion=$Version" "/DMyAppNumericVersion=$NumericVersion" $installerScript
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath $installerPath)) { throw "Installer output was not created: $installerPath" }

$portableHash = (Get-FileHash -LiteralPath $portableArchive -Algorithm SHA256).Hash
$installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
$checksumLines = @(
    "$installerHash  $(Split-Path -Leaf $installerPath)",
    "$portableHash  $(Split-Path -Leaf $portableArchive)"
)
[System.IO.File]::WriteAllLines($checksumsPath, $checksumLines)

Write-Host "Installer: $installerPath"
Write-Host "Portable:  $portableArchive"
Write-Host "Checksums: $checksumsPath"
