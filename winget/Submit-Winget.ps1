<#
.SYNOPSIS
    Creates the winget manifests for a published PulseRAM release, validates them and (optionally) submits them
    to microsoft/winget-pkgs. Needed once, for the first version; release.yml handles later versions.
.EXAMPLE
    .\winget\Submit-Winget.ps1 -Version 1.4.0              # write + validate only
    .\winget\Submit-Winget.ps1 -Version 1.4.0 -Submit      # also open the pull request (asks for a GitHub token)
#>
param(
    [Parameter(Mandatory)] [string]$Version,
    [switch]$Submit,
    [string]$Token = $env:WINGET_TOKEN
)

$ErrorActionPreference = "Stop"
$id = "Hiirocchi18.PulseRAM"
$base = "https://github.com/Hiirocchi18/PulseRAM/releases/download/v$Version"
$out = Join-Path $PSScriptRoot "manifests\h\Hiirocchi18\PulseRAM\$Version"
New-Item -ItemType Directory -Force $out | Out-Null

function Get-RemoteSha256([string]$url) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile $tmp -UseBasicParsing
    try { (Get-FileHash $tmp -Algorithm SHA256).Hash } finally { Remove-Item $tmp -Force }
}

$x64Url = "$base/PulseRAM-Setup-v$Version.exe"
$armUrl = "$base/PulseRAM-Setup-v$Version-arm64.exe"
$x64Sha = Get-RemoteSha256 $x64Url
$armSha = $null
try { $armSha = Get-RemoteSha256 $armUrl } catch { Write-Warning "No ARM64 installer on the release; listing x64 only." }

$productCode = "{D3E8C41A-714F-4822-B129-C59FA6E1E49F}_is1"
$schema = "1.9.0"

$installers = @"
  - Architecture: x64
    InstallerUrl: $x64Url
    InstallerSha256: $x64Sha
"@
if ($armSha) {
    $installers += @"

  - Architecture: arm64
    InstallerUrl: $armUrl
    InstallerSha256: $armSha
"@
}

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@ | Set-Content (Join-Path $out "$id.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
MinimumOSVersion: 10.0.17763.0
InstallerType: inno
Scope: user
InstallModes:
  - interactive
  - silent
  - silentWithProgress
InstallerSwitches:
  Custom: /CURRENTUSER
UpgradeBehavior: install
ProductCode: '$productCode'
AppsAndFeaturesEntries:
  - DisplayName: PulseRAM (WinUI 3 Memory Optimizer)
    Publisher: Hiirocchi18
    ProductCode: '$productCode'
Installers:
$installers
ManifestType: installer
ManifestVersion: $schema
"@ | Set-Content (Join-Path $out "$id.installer.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Hiirocchi18
PublisherUrl: https://github.com/Hiirocchi18
PublisherSupportUrl: https://github.com/Hiirocchi18/PulseRAM/issues
PackageName: PulseRAM
PackageUrl: https://github.com/Hiirocchi18/PulseRAM
License: MIT
LicenseUrl: https://github.com/Hiirocchi18/PulseRAM/blob/main/LICENSE
ShortDescription: Lightweight RAM optimizer for Windows 10/11 that adapts to low- and high-end PCs.
Description: |-
  PulseRAM trims the memory of apps you aren't using when RAM runs low, purges the standby cache only when it helps,
  warns before the commit limit is reached, and can freeze, throttle or cap individual apps.
  It tunes itself to your hardware, lives in the tray, and makes no network requests except an optional daily update check.
Moniker: pulseram
Tags:
  - memory
  - ram
  - ram-optimizer
  - memory-cleaner
  - performance
  - system-tray
  - winui3
ReleaseNotesUrl: https://github.com/Hiirocchi18/PulseRAM/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: $schema
"@ | Set-Content (Join-Path $out "$id.locale.en-US.yaml") -Encoding utf8

Write-Host "Manifests written to $out" -ForegroundColor Green
winget validate --manifest $out
if ($LASTEXITCODE -ne 0) { Write-Error "winget validate failed." }

Write-Host "Optional local test (installs it):  winget settings --enable LocalManifestFiles; winget install --manifest `"$out`"" -ForegroundColor Cyan

if ($Submit) {
    if (-not (Get-Command wingetcreate -ErrorAction SilentlyContinue)) {
        winget install --id Microsoft.WingetCreate -e --accept-source-agreements --accept-package-agreements
        $env:Path = [Environment]::GetEnvironmentVariable("Path", "User") + ";" + [Environment]::GetEnvironmentVariable("Path", "Machine")
    }
    if ($Token) { wingetcreate submit $out --token $Token }
    else { wingetcreate submit $out }   # opens a browser to sign in to GitHub
}
