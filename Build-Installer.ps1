<#
.SYNOPSIS
    PulseRAM One-Click Full Package Installer Build Script
    Compiles self-contained WinUI 3 binaries and produces a single-file installer via Inno Setup.
#>

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $ScriptDir

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  PulseRAM — Compiling Full Package Installer (v1.0.0)" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Stop any running instances
Write-Host "[1/5] Stopping running PulseRAM instances..." -ForegroundColor Yellow
Stop-Process -Name "PulseRAM" -Force -ErrorAction SilentlyContinue

# 2. Publish self-contained Win-x64 binaries
Write-Host "[2/5] Publishing self-contained .NET 8 + WinUI 3 binaries..." -ForegroundColor Yellow
$publishDir = Join-Path $ScriptDir "publish_selfcontained"
& "C:\Program Files\dotnet\dotnet.exe" publish "PulseRAM.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
}

# 3. Inject XAML themes & PRI assets into publish folder
Write-Host "[3/5] Injecting PRI and XAML themes into publish directory..." -ForegroundColor Yellow
$binWinUi3 = Join-Path $ScriptDir "bin_winui3"
Copy-Item (Join-Path $binWinUi3 "resources*.pri") $publishDir -Force
Copy-Item (Join-Path $binWinUi3 "Microsoft.UI.Xaml") $publishDir -Recurse -Force
Copy-Item (Join-Path $binWinUi3 "PulseRAM.ico") $publishDir -Force

# 4. Locate Inno Setup Compiler
Write-Host "[4/5] Locating Inno Setup 6 compiler..." -ForegroundColor Yellow
$isccPaths = @(
    "C:\Users\Hiirocchi\AppData\Local\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $null
foreach ($path in $isccPaths) {
    if (Test-Path $path) {
        $iscc = $path
        break
    }
}

if (-not $iscc) {
    Write-Host "Inno Setup not found. Installing via winget..." -ForegroundColor Yellow
    winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements --silent
    foreach ($path in $isccPaths) {
        if (Test-Path $path) { $iscc = $path; break }
    }
}

if (-not $iscc) {
    Write-Error "Could not find ISCC.exe. Please install Inno Setup 6."
}

# 5. Compile Inno Setup Script
Write-Host "[5/5] Compiling setup installer via Inno Setup..." -ForegroundColor Yellow
$distDir = Join-Path $ScriptDir "dist"
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

& $iscc (Join-Path $ScriptDir "PulseRAM.iss")

if ($LASTEXITCODE -ne 0) {
    Write-Error "Inno Setup compilation failed with exit code $LASTEXITCODE"
}

$installer = Join-Path $distDir "PulseRAM-Setup-v1.0.0.exe"
if (Test-Path $installer) {
    $item = Get-Item $installer
    $sizeMb = [Math]::Round($item.Length / 1MB, 2)
    Write-Host "`nSUCCESS! Full package installer created:" -ForegroundColor Green
    Write-Host "  Path: $installer" -ForegroundColor White
    Write-Host "  Size: $sizeMb MB" -ForegroundColor White
} else {
    Write-Error "Installer executable not found in $distDir"
}
