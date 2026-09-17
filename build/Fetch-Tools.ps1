# =============================================================================
# Fetch-Tools.ps1
# Downloads the command-line tool binaries required by IPA Studio into the
# "tools" folder next to the application (or into src/IPAStudio.App/tools for
# development).
#
# Sources:
#   - ipatool v2                    -> official majd/ipatool v2.6.0 release
#   - ipatool v3 + anisette.exe     -> kda2495/IPA_Downloader, pinned to a commit SHA
#     because the upstream default branch no longer carries these legacy binaries
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

# The upstream project deleted these binaries from its default branch, so "main" now
# returns 404 and any build pointing at it fails before it can compile anything. Git
# history is immutable, so a commit SHA still serves the exact files that shipped in
# 1.7.1 - never a moved or replaced one. This is the last revision where all three
# were present; do not "helpfully" change it back to a branch name.
$LegacyToolsRevision = "e3f14e64d7070d33481919269d4c5929612b1131"
$RepoRaw = "https://raw.githubusercontent.com/kda2495/IPA_Downloader/$LegacyToolsRevision/MainApp"

# Verified against the pinned revision. Raw GitHub answers a missing file with a 200-ish
# looking 404 body, so without these a deleted binary would ship as a 14-byte text file
# and only fail once a user tried to log in.
$LegacyToolHashes = @{
    "windows_amd64_v3\ipatool.exe"  = "be7e2ca296c7ae96c530d1262bfb85892bc11094df6fe5303bbad8235f9f4f11"
    "windows_amd64_v3\anisette.exe" = "b1151e3fc1b550b1dfe07dd81f922203413ae45b3a05a2c592b875451f864712"
}
$IpatoolVersion = "2.6.0"
$IpatoolRelease = "https://github.com/majd/ipatool/releases/download/v$IpatoolVersion/ipatool-$IpatoolVersion-windows-amd64.tar.gz"
$IpatoolArchiveSha256 = "3ee48adc7c4aa84a8cc8ff9399d387c25f9b8593b2c212da29e966047a08ad21"
$IpatoolBinarySha256 = "79993976658be95f1c0a7d30e2bdc806b4764dca0619815bfc6c96630f1103ec"
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

# --- official ipatool v2 ------------------------------------------------------
Write-Host "`n[1/3] ipatool v$IpatoolVersion ..."
$ipatoolArchive = Join-Path $env:TEMP "ipatool-$IpatoolVersion-windows-amd64.tar.gz"
$ipatoolExtract = Join-Path $env:TEMP "ipatool-$IpatoolVersion-windows-amd64"
$ipatoolDestination = Join-Path $OutDir "windows_amd64_v2\ipatool.exe"
$ipatoolArchiveEntry = "bin/ipatool-$IpatoolVersion-windows-amd64.exe"
Download-File $IpatoolRelease $ipatoolArchive
$ipatoolActualHash = (Get-FileHash -Path $ipatoolArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ipatoolActualHash -ne $IpatoolArchiveSha256) {
    throw "ipatool archive checksum mismatch: expected $IpatoolArchiveSha256, got $ipatoolActualHash"
}
if (Test-Path $ipatoolExtract) { Remove-Item $ipatoolExtract -Recurse -Force }
New-Item -ItemType Directory -Path $ipatoolExtract -Force | Out-Null
& tar.exe -xzf $ipatoolArchive -C $ipatoolExtract $ipatoolArchiveEntry
$ipatoolExtracted = Join-Path $ipatoolExtract $ipatoolArchiveEntry
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $ipatoolExtracted)) {
    throw "Failed to extract $ipatoolArchiveEntry from the official release archive."
}
New-Item -ItemType Directory -Path (Split-Path -Parent $ipatoolDestination) -Force | Out-Null
Copy-Item $ipatoolExtracted -Destination $ipatoolDestination -Force
$ipatoolBinaryHash = (Get-FileHash -Path $ipatoolDestination -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ipatoolBinaryHash -ne $IpatoolBinarySha256) {
    throw "ipatool binary checksum mismatch: expected $IpatoolBinarySha256, got $ipatoolBinaryHash"
}
Write-Host "  -> official backend SHA-256: $ipatoolBinaryHash"
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

# Confirm the pinned binaries are the real thing. Raw GitHub serves a deleted path as a
# short text body with a 404, and Invoke-WebRequest is happy to write it to disk under an
# .exe name; checking the content is the only way to catch that at build time.
foreach ($entry in $LegacyToolHashes.GetEnumerator()) {
    $path = Join-Path $OutDir $entry.Key
    $actual = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $entry.Value) {
        Write-Error "Checksum mismatch for $($entry.Key): expected $($entry.Value), got $actual"
        exit 1
    }
}

Write-Host "All tools downloaded successfully." -ForegroundColor Green
