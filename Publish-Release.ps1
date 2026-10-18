$ErrorActionPreference = "Stop"

$token = $env:GITHUB_TOKEN
if (-not $token) {
    try {
        $credOut = "protocol=https`nhost=github.com`n" | git credential fill 2>$null
        foreach ($line in ($credOut -split "`r?`n")) {
            if ($line -match "^password=(.+)$") {
                $token = $Matches[1]
                break
            }
        }
    } catch { }
}

if (-not $token) {
    throw "No GitHub token found in `$env:GITHUB_TOKEN or Git Credential Manager."
}

$repo = "Hiirocchi18/PulseRAM"
$version = "1.3.1"
$installerPath = "c:\Users\Hiirocchi\Documents\Joyboy\PulseRAM\dist\PulseRAM-Setup-v$version.exe"

if (-not (Test-Path $installerPath)) {
    throw "Installer binary not found at $installerPath"
}

$hash = (Get-FileHash $installerPath -Algorithm SHA256).Hash

$bodyLines = @(
    "## PulseRAM v$version",
    "",
    "**Native WinUI 3 + NT Kernel Intelligent Memory Optimizer & Process Governor for Windows 10 & 11.**",
    "",
    "### Standalone Installer",
    "Download and run **`PulseRAM-Setup-v$version.exe`** below for a full, self-contained installation. No external .NET Desktop Runtime or Windows App SDK installation required.",
    "",
    "### Highlights",
    "- **100% Pure Native C# + WinUI 3**: Zero web engines, zero Electron, zero Python.",
    "- **Direct NT Kernel Memory Operations**: Working set trimming, plus (as administrator) Standby Cache purge, Modified Page flush, and physical page combining via ntdll.dll and psapi.dll.",
    "- **Memory Reactor Radial Gauge**: 60 FPS smooth LERP interpolation, ambient pulsing halo, and 360-degree rotating orbital ring.",
    "- **Intelligent Auto-Pilot**: Threshold & floor auto-trimming with adaptive back-off, Runaway Leak Guardian (sustained private-memory growth), Foreground App Shield, and scheduled maintenance sweeps.",
    "- **Process Governor**: Instant Working Set Trim, EcoQoS efficiency throttling, Cryo-Freeze/Thaw (NtSuspendProcess), and hard Per-App Memory Caps.",
    "- **Global Hotkeys & System Tray**: Ctrl+Alt+Shift+M (Smart Trim), Ctrl+Alt+Shift+P (Deep Sweep), configurable, and interactive tray menu.",
    "",
    "---",
    ("**Installer SHA-256 Checksum:** " + $hash)
)

$bodyText = $bodyLines -join "`n"

$payloadObj = @{
    tag_name = "v$version"
    target_commitish = "main"
    name = "PulseRAM v$version - Pure WinUI 3 Release"
    body = $bodyText
    draft = $false
    prerelease = $false
}

$payloadJson = $payloadObj | ConvertTo-Json -Depth 5

Write-Host "Creating GitHub Release v$version on $repo..." -ForegroundColor Cyan

$utf8Bytes = [System.Text.Encoding]::UTF8.GetBytes($payloadJson)

$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases" `
    -Method Post `
    -Headers @{
        "Authorization" = "token $token"
        "User-Agent" = "PulseRAM-Deploy"
        "Accept" = "application/vnd.github.v3+json"
    } `
    -Body $utf8Bytes `
    -ContentType "application/json; charset=utf-8"

Write-Host "Release created successfully!" -ForegroundColor Green
Write-Host "Release ID:  $($release.id)"
Write-Host "Release URL: $($release.html_url)"

Write-Host "Uploading installer asset PulseRAM-Setup-v$version.exe..." -ForegroundColor Cyan

$uploadUrl = "https://uploads.github.com/repos/$repo/releases/$($release.id)/assets?name=PulseRAM-Setup-v$version.exe"

$uploadResult = & curl.exe -s -X POST `
    -H "Authorization: token $token" `
    -H "User-Agent: PulseRAM-Deploy" `
    -H "Content-Type: application/octet-stream" `
    --data-binary "@$installerPath" `
    $uploadUrl

$asset = $uploadResult | ConvertFrom-Json

Write-Host "Asset uploaded successfully!" -ForegroundColor Green
Write-Host "Asset Name:     $($asset.name)"
Write-Host "Asset Size:     $([Math]::Round($asset.size / 1MB, 2)) MB"
Write-Host "Download URL:   $($asset.browser_download_url)"
