# =============================================================================
# Fetch-Tools.ps1
# Downloads the command-line tool binaries required by IPA Studio into the
# "tools" folder next to the application (or into src/IPAStudio.App/tools for
# development).
#
# Sources:
#   - ipatool v2                    -> official majd/ipatool v2.5.0 release
#   - ipatool v3 + anisette.exe     -> kda2495/IPA_Downloader, pinned to a commit SHA
#     because the upstream default branch no longer carries these legacy binaries
#   - ipatool-rs v0.1.7             -> Kosthi/ipatool-rs (SAP-signed BETA auth)
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
$IpatoolVersion = "2.5.0-ipa-studio.1"
$IpatoolSourceRevision = "3aa4a86febe9ee056b04b4d90ee5f62afaa31cc8"
$IpatoolSource = "https://api.github.com/repos/majd/ipatool/tarball/$IpatoolSourceRevision"
$IpatoolSourceSha256 = "43970e4b18cd2cdd91b0e947e1d8496f62136ddaa83d84274a03202d2d0a8644"
$IpatoolBinarySha256 = "12ffaf59186f1e203f7adffdf3f523b9d61b7da63c15cc4043c11505248ea286"
$IpatoolRsVersion = "0.1.7"
$IpatoolRsRelease = "https://github.com/Kosthi/ipatool-rs/releases/download/v$IpatoolRsVersion/ipatool-rs-x86_64-pc-windows-msvc.zip"
$IpatoolRsSha256 = "77f6dd43eaa17d8ef2e9bda1c2240c59b9f8a755f8cd6d0b3f60e5d171888f77"
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

# --- ipatool v2 with Apple 5002/redownload fallback --------------------------
Write-Host "`n[1/4] patched ipatool v$IpatoolVersion ..."
$ipatoolArchive = Join-Path $env:TEMP "ipatool-$IpatoolSourceRevision-src.tar.gz"
$ipatoolExtract = Join-Path $env:TEMP "ipatool-$IpatoolSourceRevision-src"
Download-File $IpatoolSource $ipatoolArchive
$ipatoolActualHash = (Get-FileHash -Path $ipatoolArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ipatoolActualHash -ne $IpatoolSourceSha256) {
    throw "ipatool source checksum mismatch: expected $IpatoolSourceSha256, got $ipatoolActualHash"
}
if (Test-Path $ipatoolExtract) { Remove-Item $ipatoolExtract -Recurse -Force }
New-Item -ItemType Directory -Path $ipatoolExtract -Force | Out-Null
& tar.exe -xzf $ipatoolArchive -C $ipatoolExtract --strip-components=1
if ($LASTEXITCODE -ne 0) { throw "Failed to extract the pinned ipatool source archive." }
$ipatoolDestination = Join-Path $OutDir "windows_amd64_v2\ipatool.exe"
New-Item -ItemType Directory -Path (Split-Path -Parent $ipatoolDestination) -Force | Out-Null
Push-Location $ipatoolExtract
try {
    $env:GOOS = "windows"
    $env:GOARCH = "amd64"
    $env:CGO_ENABLED = "0"
    & go build -trimpath -buildvcs=false "-ldflags=-s -w -X github.com/majd/ipatool/v2/cmd.version=$IpatoolVersion" -o $ipatoolDestination .
    if ($LASTEXITCODE -ne 0) { throw "Failed to build the patched ipatool backend." }
}
finally {
    Pop-Location
}
$ipatoolBinaryHash = (Get-FileHash -Path $ipatoolDestination -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ipatoolBinaryHash -ne $IpatoolBinarySha256) {
    throw "ipatool binary checksum mismatch: expected $IpatoolBinarySha256, got $ipatoolBinaryHash"
}
Write-Host "  -> patched backend SHA-256: $ipatoolBinaryHash"
Remove-Item $ipatoolArchive -Force -ErrorAction SilentlyContinue
Remove-Item $ipatoolExtract -Recurse -Force -ErrorAction SilentlyContinue

# --- ipatool v3 + anisette ----------------------------------------------------
Write-Host "`n[2/4] ipatool v3 + anisette ..."
Download-File "$RepoRaw/windows_amd64_v3/ipatool.exe"  (Join-Path $OutDir "windows_amd64_v3\ipatool.exe")
Download-File "$RepoRaw/windows_amd64_v3/anisette.exe" (Join-Path $OutDir "windows_amd64_v3\anisette.exe")

# --- ipatool-rs SAP BETA ------------------------------------------------------
Write-Host "`n[3/4] ipatool-rs v$IpatoolRsVersion (SAP BETA) ..."
$ipatoolRsZip = Join-Path $env:TEMP "ipatool-rs-$IpatoolRsVersion-windows-x64.zip"
$ipatoolRsExtract = Join-Path $env:TEMP "ipatool-rs-$IpatoolRsVersion-windows-x64"
Download-File $IpatoolRsRelease $ipatoolRsZip
$ipatoolRsActualHash = (Get-FileHash -Path $ipatoolRsZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ipatoolRsActualHash -ne $IpatoolRsSha256) {
    throw "ipatool-rs archive checksum mismatch: expected $IpatoolRsSha256, got $ipatoolRsActualHash"
}
if (Test-Path $ipatoolRsExtract) { Remove-Item $ipatoolRsExtract -Recurse -Force }
Expand-Archive -Path $ipatoolRsZip -DestinationPath $ipatoolRsExtract -Force
$ipatoolRsBinary = Join-Path $ipatoolRsExtract "ipatool.exe"
if (-not (Test-Path $ipatoolRsBinary)) { throw "ipatool.exe was not found in the ipatool-rs archive." }
$ipatoolRsDestination = Join-Path $OutDir "windows_amd64_sap_beta\ipatool.exe"
New-Item -ItemType Directory -Path (Split-Path -Parent $ipatoolRsDestination) -Force | Out-Null
Copy-Item $ipatoolRsBinary -Destination $ipatoolRsDestination -Force
Remove-Item $ipatoolRsZip -Force -ErrorAction SilentlyContinue
Remove-Item $ipatoolRsExtract -Recurse -Force -ErrorAction SilentlyContinue

# --- libimobiledevice suite ----------------------------------------------------
Write-Host "`n[4/4] libimobiledevice suite (ideviceinstaller, idevice_id, ideviceinfo) ..."
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
    (Join-Path $OutDir "windows_amd64_sap_beta\ipatool.exe"),
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
