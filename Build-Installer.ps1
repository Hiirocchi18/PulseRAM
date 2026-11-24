<#
.SYNOPSIS
    Builds PulseRAM: the release EXE, the Inno Setup installer and the portable zip.
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
$Version = (Select-String -Path (Join-Path $ScriptDir "Cargo.toml") -Pattern '^version\s*=\s*"([^"]+)"' | Select-Object -First 1).Matches[0].Groups[1].Value
$target = if ($Arch -eq "arm64") { "aarch64-pc-windows-msvc" } else { "x86_64-pc-windows-msvc" }
$publishDir = Join-Path $ScriptDir "publish_$Arch"
$suffix = if ($Arch -eq "arm64") { "-arm64" } else { "" }

Write-Host "PulseRAM v$Version ($Arch)" -ForegroundColor Cyan

# 1. Stop running instances (they lock the EXE)
Stop-Process -Name "PulseRAM" -Force -ErrorAction SilentlyContinue

# 2. Build. Needs Rust (https://rustup.rs) and the Visual Studio C++ build tools.
Write-Host "[1/4] Building..." -ForegroundColor Yellow
rustup target add $target
cargo build --release --locked --target $target
if ($LASTEXITCODE -ne 0) { Write-Error "Build failed with exit code $LASTEXITCODE" }

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
Copy-Item (Join-Path $ScriptDir "target\$target\release\PulseRAM.exe") $publishDir
Copy-Item (Join-Path $ScriptDir "pulseram_defaults.json") $publishDir
Copy-Item (Join-Path $ScriptDir "PulseRAM.ico") $publishDir

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
& $iscc "/DMyArch=$Arch" "/DMyAppVersion=$Version" "/DPublishDir=$(Split-Path $publishDir -Leaf)" (Join-Path $ScriptDir "PulseRAM.iss")
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

Write-Host ""
Write-Host "Done:" -ForegroundColor Green
Get-ChildItem $distDir -Filter "PulseRAM-*v$Version*" | ForEach-Object { "  {0,8:N1} MB  {1}" -f ($_.Length / 1MB), $_.FullName }
