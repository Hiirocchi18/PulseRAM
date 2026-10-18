<#
.SYNOPSIS
    Builds PulseRAM: self-contained WinUI 3 binaries, the Inno Setup installer and the portable zip.
.PARAMETER Arch
    x64 (default) or arm64.
.EXAMPLE
    .\Build-Installer.ps1
    .\Build-Installer.ps1 -Arch arm64
#>
param(
    [ValidateSet("x64", "arm64")]
    [string]$Arch = "x64"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $ScriptDir
[xml]$proj = Get-Content (Join-Path $ScriptDir "PulseRAM.csproj")
$Version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$Platform = if ($Arch -eq "arm64") { "ARM64" } else { "x64" }
$publishDir = Join-Path $ScriptDir $(if ($Arch -eq "arm64") { "publish_arm64" } else { "publish_selfcontained" })
$suffix = if ($Arch -eq "arm64") { "-arm64" } else { "" }

Write-Host "PulseRAM v$Version ($Arch)" -ForegroundColor Cyan

# 1. Stop running instances (they lock the files)
Stop-Process -Name "PulseRAM" -Force -ErrorAction SilentlyContinue

# 2. Publish. The WinUI resource (PRI) tools ship with Visual Studio, not the plain .NET SDK, so use VS's MSBuild.
Write-Host "[1/4] Publishing..." -ForegroundColor Yellow
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { Write-Error "Visual Studio 2022/2026 with the 'Windows application development' workload is required." }
$vsPath = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -property installationPath
$msbuild = @("MSBuild\Current\Bin\arm64\MSBuild.exe", "MSBuild\Current\Bin\amd64\MSBuild.exe", "MSBuild\Current\Bin\MSBuild.exe") |
    ForEach-Object { Join-Path $vsPath $_ } | Where-Object { Test-Path $_ } | Select-Object -First 1
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& $msbuild "PulseRAM.csproj" -restore -t:Publish -nologo -v:minimal `
    -p:Configuration=Release "-p:Platform=$Platform" "-p:RuntimeIdentifier=win-$Arch" -p:SelfContained=true `
    "-p:PublishDir=$publishDir\" `
    -p:EnableCoreMrtTooling=true -p:AppxGeneratePriEnabled=true -p:IncludeProjectPriFile=true
if ($LASTEXITCODE -ne 0) { Write-Error "Publish failed with exit code $LASTEXITCODE" }

foreach ($required in @("PulseRAM.exe", "resources.pri", "Microsoft.UI.Xaml.Controls.pri", "pulseram_defaults.json")) {
    if (-not (Test-Path (Join-Path $publishDir $required))) { Write-Error "Missing $required in the publish output." }
}
Copy-Item (Join-Path $ScriptDir "PulseRAM.ico") $publishDir -Force
Get-ChildItem $publishDir -Filter *.pdb -Recurse | Remove-Item -Force

# 3. Installer
Write-Host "[2/4] Locating Inno Setup 6..." -ForegroundColor Yellow
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Host "Inno Setup not found. Installing via winget..." -ForegroundColor Yellow
    winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements --silent
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { Write-Error "Could not find ISCC.exe. Please install Inno Setup 6." }

Write-Host "[3/4] Compiling installer..." -ForegroundColor Yellow
$distDir = Join-Path $ScriptDir "dist"
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
& $iscc "/DMyArch=$Arch" "/DPublishDir=$(Split-Path $publishDir -Leaf)" (Join-Path $ScriptDir "PulseRAM.iss")
if ($LASTEXITCODE -ne 0) { Write-Error "Inno Setup failed with exit code $LASTEXITCODE" }

# 4. Portable zip: same files plus portable.txt, which keeps settings in a "data" folder next to the EXE.
Write-Host "[4/4] Packing portable zip..." -ForegroundColor Yellow
$portableDir = Join-Path $ScriptDir "portable_$Arch"
if (Test-Path $portableDir) { Remove-Item $portableDir -Recurse -Force }
Copy-Item $publishDir $portableDir -Recurse
Copy-Item (Join-Path $ScriptDir "portable.txt") $portableDir -Force
$zip = Join-Path $distDir "PulseRAM-Portable-v$Version-$Arch.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $portableDir "*") -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $portableDir -Recurse -Force

$installer = Join-Path $distDir "PulseRAM-Setup-v$Version$suffix.exe"
foreach ($f in @($installer, $zip)) {
    $item = Get-Item $f
    Write-Host ("  {0}  {1:N1} MB  SHA256 {2}" -f $item.Name, ($item.Length / 1MB), (Get-FileHash $f -Algorithm SHA256).Hash) -ForegroundColor Green
}
