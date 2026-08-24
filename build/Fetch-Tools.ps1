# =============================================================================
# Fetch-Tools.ps1
# Downloads the command-line tool binaries required by IPA Studio into the
# "tools" folder next to the application (or into src/IPAStudio.App/tools for
# development).
#
# Sources:
#   - ipatool-rs v0.1.6             -> Kosthi/ipatool-rs (current Apple auth)
#   - legacy ipatool v3 + anisette  -> kda2495/IPA_Downloader (fallback mode)
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

# The upstream project removed its legacy v3 directory on 2026-08-16. Pin the
# last known revision so release builds remain reproducible instead of returning 404.
$LegacyToolsRevision = "9e799c58f04a6b47f6b81d261b179dcdc4cbf70f"
$RepoRaw = "https://raw.githubusercontent.com/kda2495/IPA_Downloader/$LegacyToolsRevision/MainApp"
$IpatoolVersion = "0.1.6"
$IpatoolRelease = "https://github.com/Kosthi/ipatool-rs/releases/download/v$($IpatoolVersion)/ipatool-rs-x86_64-pc-windows-msvc.zip"
$IpatoolSha256 = "bb618026f6026cd31d62497c330bb60d08267d7e2d5b23322da484a282cbed08"
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

# --- ipatool-rs (current Apple auth, no iCloud/anisette requirement) ----------
# The original Go ipatool receives HTTP 403 from Apple's changed auth service.
# ipatool-rs is adapted to the current bag-provided endpoint and supports 2FA.
Write-Host "`n[1/3] ipatool-rs v$IpatoolVersion ..."
$ipatoolArchive = Join-Path $env:TEMP "ipatool-rs-$IpatoolVersion-windows-x64.zip"
$ipatoolExtract = Join-Path $env:TEMP "ipatool-rs-$IpatoolVersion-windows-x64"
Download-File $IpatoolRelease $ipatoolArchive

$actualHash = (Get-FileHash -Path $ipatoolArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $IpatoolSha256) {
    throw "ipatool-rs archive checksum mismatch: expected $IpatoolSha256, got $actualHash"
}

if (Test-Path $ipatoolExtract) { Remove-Item $ipatoolExtract -Recurse -Force }
Expand-Archive -Path $ipatoolArchive -DestinationPath $ipatoolExtract -Force
$ipatoolBinary = Get-ChildItem -Path $ipatoolExtract -Recurse -File |
    Where-Object { $_.Name -eq "ipatool.exe" } |
    Select-Object -First 1
if (-not $ipatoolBinary) { throw "ipatool.exe was not found in the ipatool-rs release archive." }
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
