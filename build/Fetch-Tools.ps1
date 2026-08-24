# =============================================================================
# Fetch-Tools.ps1
# Downloads the command-line tool binaries required by IPA Studio into the
# "tools" folder next to the application (or into src/IPAStudio.App/tools for
# development).
#
# Sources:
#   - ipatool v2.3.2                -> official majd/ipatool GitHub release
#   - legacy ipatool v3 + anisette  -> kda2495/IPA_Downloader (optional mode)
#   - libimobiledevice suite        -> imobiledevice-net GitHub releases
#     (ideviceinstaller.exe, idevice_id.exe, ideviceinfo.exe,
#      idevicediagnostics.exe + DLLs)
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File build/Fetch-Tools.ps1 [-OutDir <path>]
# =============================================================================

param(
    [string]$OutDir = (Join-Path $PSScriptRoot "..\src\IPAStudio.App\tools")
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$RepoRaw = "https://raw.githubusercontent.com/kda2495/IPA_Downloader/main/MainApp"
$IpatoolVersion = "2.3.2"
$IpatoolRelease = "https://github.com/majd/ipatool/releases/download/v$($IpatoolVersion)/ipatool-$($IpatoolVersion)-windows-amd64.tar.gz"
$IpatoolSha256 = "6352441f6f91df7947aaa203b19cb7d3c9d77920fc466dd784ff9cae88db5c92"
$ImobiledeviceRelease = "https://github.com/libimobiledevice-win32/imobiledevice-net/releases/download/v1.3.17/libimobiledevice.1.2.1-r1122-win-x64.zip"

function Download-File {
    param([string]$Url, [string]$Destination)
    $dir = Split-Path -Parent $Destination
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Write-Host "  -> $Url"
    Invoke-WebRequest -Uri $Url -OutFile $Destination -UseBasicParsing
}

$OutDir = [System.IO.Path]::GetFullPath($OutDir)
Write-Host "Tools output folder: $OutDir"

# --- ipatool v2 (no iCloud/anisette requirement) -----------------------------
# Apple changed the native authentication endpoint in 2026. The old binary from
# IPA_Downloader now receives HTTP 403; official ipatool 2.3.2 contains the fix.
Write-Host "`n[1/3] ipatool v$IpatoolVersion ..."
$ipatoolArchive = Join-Path $env:TEMP "ipatool-$IpatoolVersion-windows-amd64.tar.gz"
$ipatoolExtract = Join-Path $env:TEMP "ipatool-$IpatoolVersion-windows-amd64"
Download-File $IpatoolRelease $ipatoolArchive

$actualHash = (Get-FileHash -Path $ipatoolArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $IpatoolSha256) {
    throw "ipatool archive checksum mismatch: expected $IpatoolSha256, got $actualHash"
}

if (Test-Path $ipatoolExtract) { Remove-Item $ipatoolExtract -Recurse -Force }
New-Item -ItemType Directory -Path $ipatoolExtract -Force | Out-Null
tar -xzf $ipatoolArchive -C $ipatoolExtract
$ipatoolBinary = Get-ChildItem -Path $ipatoolExtract -Recurse -File |
    Where-Object { $_.Name -eq "ipatool-$IpatoolVersion-windows-amd64.exe" } |
    Select-Object -First 1
if (-not $ipatoolBinary) { throw "ipatool.exe was not found in the official release archive." }
$ipatoolDestination = Join-Path $OutDir "windows_amd64_v2\ipatool.exe"
New-Item -ItemType Directory -Path (Split-Path -Parent $ipatoolDestination) -Force | Out-Null
Copy-Item $ipatoolBinary.FullName -Destination $ipatoolDestination -Force
Remove-Item $ipatoolArchive -Force -ErrorAction SilentlyContinue
Remove-Item $ipatoolExtract -Recurse -Force -ErrorAction SilentlyContinue

# --- ipatool v3 + anisette ----------------------------------------------------
Write-Host "`n[2/3] ipatool v3 + anisette ..."
Download-File "$RepoRaw/windows_amd64_v3/ipatool.exe"  (Join-Path $OutDir "windows_amd64_v3\ipatool.exe")
Download-File "$RepoRaw/windows_amd64_v3/anisette.exe" (Join-Path $OutDir "windows_amd64_v3\anisette.exe")

# --- libimobiledevice suite ----------------------------------------------------
Write-Host "`n[3/3] libimobiledevice suite (ideviceinstaller, idevice_id, ideviceinfo) ..."
$zipPath = Join-Path $env:TEMP "imobiledevice-net.zip"
$extractPath = Join-Path $env:TEMP "imobiledevice-net"
Download-File $ImobiledeviceRelease $zipPath

if (Test-Path $extractPath) { Remove-Item $extractPath -Recurse -Force }
Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

$imobileDir = Join-Path $OutDir "imobiledevice"
if (-not (Test-Path $imobileDir)) { New-Item -ItemType Directory -Path $imobileDir -Force | Out-Null }

# Copy the tools we need plus every DLL they depend on.
#
# idevicediagnostics.exe is what reads battery capacity and cycle count (via the
# AppleSmartBattery IORegistry entry). It was missing from this list, so the file
# never shipped and the battery row could only ever say "недоступно" — the code
# that reads it was fine, the executable simply was not there.
$needed = @(
    "ideviceinstaller.exe",
    "idevice_id.exe",
    "ideviceinfo.exe",
    "idevicepair.exe",
    "idevicediagnostics.exe"
)
Get-ChildItem -Path $extractPath -Recurse -File | Where-Object {
    $needed -contains $_.Name -or $_.Extension -eq ".dll"
} | ForEach-Object {
    Copy-Item $_.FullName -Destination (Join-Path $imobileDir $_.Name) -Force
}

Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Remove-Item $extractPath -Recurse -Force -ErrorAction SilentlyContinue

# --- Verify -------------------------------------------------------------------
Write-Host "`nVerifying ..."
$required = @(
    (Join-Path $OutDir "windows_amd64_v2\ipatool.exe"),
    (Join-Path $OutDir "windows_amd64_v3\ipatool.exe"),
    (Join-Path $OutDir "windows_amd64_v3\anisette.exe"),
    (Join-Path $imobileDir "ideviceinstaller.exe"),
    (Join-Path $imobileDir "idevice_id.exe"),
    (Join-Path $imobileDir "ideviceinfo.exe"),
    # Listed here too so that dropping it again fails the build loudly instead of
    # silently shipping a version where battery capacity never works.
    (Join-Path $imobileDir "idevicediagnostics.exe")
)
$missing = $required | Where-Object { -not (Test-Path $_) }
if ($missing) {
    Write-Error "Missing files:`n$($missing -join "`n")"
    exit 1
}
Write-Host "All tools downloaded successfully." -ForegroundColor Green
