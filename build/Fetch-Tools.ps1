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
# ipatool-rs is built from source instead of taken from the published release archive: the
# released binary cannot see what the signed in account owns. Its purchase call sends the
# full header set, but volumeStoreDownloadProduct - used by both `download` and
# `version list` - sends neither X-Apple-Store-Front nor X-Token, so Apple answers those two
# against the default (US) storefront and, with no password token, as if nobody were signed
# in. Both omissions produce the same answer: HTTP 200 with an empty "songList", which
# ipatool surfaces as "unexpected response: empty songList" - for region-limited apps and
# for apps the account demonstrably owns alike. The reference client (majd/ipatool) sends
# X-Dsid, X-Apple-Store-Front and X-Token on this request; the patch below restores the two
# that are missing. Everything else is upstream v0.1.8.
$IpatoolRsVersion = "0.1.8"
$IpatoolRsSource = "https://api.github.com/repos/Kosthi/ipatool-rs/tarball/v$IpatoolRsVersion"
$IpatoolRsSourceSha256 = "fc31035e95a22e27c06f0d05c3b81f97e4cd79ffa493c375d16f400e9499a4e6"
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

# --- ipatool-rs SAP BETA (patched: storefront + token headers) -----------------
Write-Host "`n[3/4] ipatool-rs v$IpatoolRsVersion (SAP BETA, built from source) ..."
$ipatoolRsArchive = Join-Path $env:TEMP "ipatool-rs-$IpatoolRsVersion-src.tar.gz"
$ipatoolRsExtract = Join-Path $env:TEMP "ipatool-rs-$IpatoolRsVersion-src"
Download-File $IpatoolRsSource $ipatoolRsArchive
$ipatoolRsActualHash = (Get-FileHash -Path $ipatoolRsArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($ipatoolRsActualHash -ne $IpatoolRsSourceSha256) {
    throw "ipatool-rs source checksum mismatch: expected $IpatoolRsSourceSha256, got $ipatoolRsActualHash"
}
if (Test-Path $ipatoolRsExtract) { Remove-Item $ipatoolRsExtract -Recurse -Force }
New-Item -ItemType Directory -Path $ipatoolRsExtract -Force | Out-Null
& tar.exe -xzf $ipatoolRsArchive -C $ipatoolRsExtract --strip-components=1
if ($LASTEXITCODE -ne 0) { throw "Failed to extract the pinned ipatool-rs source archive." }

# The storefront patch. Applied by exact-match replacement with a count check rather than a
# diff so that a source bump which moves these lines fails the build loudly instead of
# silently shipping a binary that cannot see a non-US catalog again.
$storefrontAnchor = @'
        .header("X-Dsid", &account.directory_services_id)
'@ -replace "`r`n", "`n"
$storefrontPatched = @'
        .header("X-Dsid", &account.directory_services_id)
        .header("X-Apple-Store-Front", &account.store_front)
        .header("X-Token", &account.password_token)
'@ -replace "`r`n", "`n"
foreach ($relative in @("crates\ipatool-core\src\api\download.rs", "crates\ipatool-core\src\api\versions.rs")) {
    $file = Join-Path $ipatoolRsExtract $relative
    if (-not (Test-Path $file)) { throw "ipatool-rs source is missing $relative; the storefront patch cannot be applied." }
    $text = (Get-Content -Path $file -Raw) -replace "`r`n", "`n"
    $occurrences = ([regex]::Matches($text, [regex]::Escape($storefrontAnchor))).Count
    if ($occurrences -ne 1) {
        throw "Expected exactly one X-Dsid request header in $relative, found $occurrences. Re-check the storefront patch against ipatool-rs v$IpatoolRsVersion."
    }
    if ($text.Contains('X-Apple-Store-Front')) { throw "$relative already sends X-Apple-Store-Front; drop the patch." }
    if ($text.Contains('X-Token')) { throw "$relative already sends X-Token; drop the patch." }
    $text = $text.Replace($storefrontAnchor, $storefrontPatched)
    [System.IO.File]::WriteAllText($file, $text)
    Write-Host "  -> patched $relative (storefront + token headers)"
}

# The purchase patch. Apple reports "this account does not have the app" in two different
# shapes: failureType 9610, which ipatool recognises, and - for an app the account has never
# obtained - plain HTTP 200 with an empty songList and no failureType at all. Only the first
# triggers --purchase, so every app missing from the library failed with "empty songList"
# instead of being acquired. Treat the second shape the same way, but only for an unpinned
# request: with an explicit version id an empty songList means that build is gone, not that a
# licence is missing, and buying the app would not bring it back.
$purchaseAnchor = @'
            Err(e) if version_id.is_some() && is_empty_song_list_error(&e) => {
'@ -replace "`r`n", "`n"
$purchasePatched = @'
            Err(e)
                if version_id.is_none()
                    && is_empty_song_list_error(&e)
                    && do_purchase
                    && !purchase_attempted =>
            {
                last_download_error = Some(e.to_string());
                eprintln!("License not found (empty songList), purchasing...");
                purchase_for_download(client, resolved_app_id, &mut account).await?;
                purchase_attempted = true;
                eprintln!("Purchase successful");
            }
            Err(e) if version_id.is_some() && is_empty_song_list_error(&e) => {
'@ -replace "`r`n", "`n"
$downloadCmdRelative = "crates\ipatool-cli\src\commands\download.rs"
$downloadCmd = Join-Path $ipatoolRsExtract $downloadCmdRelative
if (-not (Test-Path $downloadCmd)) { throw "ipatool-rs source is missing $downloadCmdRelative; the purchase patch cannot be applied." }
$downloadCmdText = (Get-Content -Path $downloadCmd -Raw) -replace "`r`n", "`n"
$purchaseOccurrences = ([regex]::Matches($downloadCmdText, [regex]::Escape($purchaseAnchor))).Count
if ($purchaseOccurrences -ne 1) {
    throw "Expected exactly one pinned-version empty-songList arm in $downloadCmdRelative, found $purchaseOccurrences. Re-check the purchase patch against ipatool-rs v$IpatoolRsVersion."
}
[System.IO.File]::WriteAllText($downloadCmd, $downloadCmdText.Replace($purchaseAnchor, $purchasePatched))
Write-Host "  -> patched $downloadCmdRelative (purchase on empty songList)"

# The pricing patch. Apple refuses a free "purchase" with a flat failure code when the
# pricing parameter does not describe how the item is sold: 2059 for an Arcade title, and
# 2040 ("Purchase of this item is not currently available") for apps the very same account
# can install from the App Store app moments later. Upstream only ever retries 2059, and only
# with GAME, so a 2040 ended the download outright. Try the other two parameters Apple's own
# clients send - GAME (Arcade) and PLUS (the reacquire price Configurator uses) - before
# giving up. A parameter that does not apply is simply refused again, so the extra attempts
# cannot obtain anything the account was not entitled to.
$pricingAnchor = @'
        let result = match try_purchase(client, app_id, account, "STDQ").await {
            Err(ClientError::Store(StoreError::TemporarilyUnavailable)) => {
                tracing::info!("STDQ unavailable, trying GAME pricing");
                tokio::time::sleep(std::time::Duration::from_secs(2)).await;
                try_purchase(client, app_id, account, "GAME").await
            }
            other => other,
        };
'@ -replace "`r`n", "`n"
$pricingPatched = @'
        let mut result = try_purchase(client, app_id, account, "STDQ").await;
        for pricing in ["GAME", "PLUS"] {
            match &result {
                Err(ClientError::Store(err)) if pricing_rejected(err) => {}
                _ => break,
            }
            tracing::info!("pricing rejected by the store, retrying as {}", pricing);
            tokio::time::sleep(std::time::Duration::from_secs(2)).await;
            result = try_purchase(client, app_id, account, pricing).await;
        }
'@ -replace "`r`n", "`n"
$pricingHelperAnchor = @'
fn buy_url(account: &Account) -> String {
'@ -replace "`r`n", "`n"
$pricingHelperPatched = @'
/// Whether Apple's refusal is about how the item is priced rather than about the account or
/// the item itself, and so is worth repeating with a different pricing parameter.
fn pricing_rejected(err: &StoreError) -> bool {
    match err {
        StoreError::TemporarilyUnavailable | StoreError::PurchaseFailed => true,
        StoreError::Unknown { code, .. } => code.as_str() == "2040" || code.as_str() == "2059",
        _ => false,
    }
}

fn buy_url(account: &Account) -> String {
'@ -replace "`r`n", "`n"
$purchaseApiRelative = "crates\ipatool-core\src\api\purchase.rs"
$purchaseApi = Join-Path $ipatoolRsExtract $purchaseApiRelative
if (-not (Test-Path $purchaseApi)) { throw "ipatool-rs source is missing $purchaseApiRelative; the pricing patch cannot be applied." }
$purchaseApiText = (Get-Content -Path $purchaseApi -Raw) -replace "`r`n", "`n"
$pricingCount = ([regex]::Matches($purchaseApiText, [regex]::Escape($pricingAnchor))).Count
if ($pricingCount -ne 1) {
    throw "Expected exactly one STDQ/GAME pricing block in $purchaseApiRelative, found $pricingCount. Re-check the pricing patch against ipatool-rs v$IpatoolRsVersion."
}
$purchaseApiText = $purchaseApiText.Replace($pricingAnchor, $pricingPatched)

$helperCount = ([regex]::Matches($purchaseApiText, [regex]::Escape($pricingHelperAnchor))).Count
if ($helperCount -ne 1) {
    throw "Expected exactly one buy_url definition in $purchaseApiRelative, found $helperCount. Re-check the pricing patch against ipatool-rs v$IpatoolRsVersion."
}
$purchaseApiText = $purchaseApiText.Replace($pricingHelperAnchor, $pricingHelperPatched)
[System.IO.File]::WriteAllText($purchaseApi, $purchaseApiText)
Write-Host "  -> patched $purchaseApiRelative (GAME/PLUS pricing fallback, incl. 2040)"

# The metadata patch. iTunesMetadata.plist carries the Apple ID the archive belongs to, and
# iOS matches it against the account signed in on the device character for character.
# Upstream writes back exactly what was typed at the login prompt, so an address entered with
# capitals produced an archive the phone could not match to any signed-in account - which is
# when it shows the "sign in to the iTunes Store" sheet that then does nothing at all. Apple
# keeps the address in lower case; normalising to it makes the two agree.
$metadataAnchor = @'
    meta_dict.insert("apple-id".into(), plist::Value::String(email.into()));
    meta_dict.insert("userName".into(), plist::Value::String(email.into()));
'@ -replace "`r`n", "`n"
$metadataPatched = @'
    let account_id = email.trim().to_ascii_lowercase();
    meta_dict.insert("apple-id".into(), plist::Value::String(account_id.clone()));
    meta_dict.insert("userName".into(), plist::Value::String(account_id));
'@ -replace "`r`n", "`n"
$ipaPatchRelative = "crates\ipatool-core\src\ipa\patch.rs"
$ipaPatch = Join-Path $ipatoolRsExtract $ipaPatchRelative
if (-not (Test-Path $ipaPatch)) { throw "ipatool-rs source is missing $ipaPatchRelative; the metadata patch cannot be applied." }
$ipaPatchText = (Get-Content -Path $ipaPatch -Raw) -replace "`r`n", "`n"
$metadataOccurrences = ([regex]::Matches($ipaPatchText, [regex]::Escape($metadataAnchor))).Count
if ($metadataOccurrences -ne 1) {
    throw "Expected exactly one apple-id/userName pair in $ipaPatchRelative, found $metadataOccurrences. Re-check the metadata patch against ipatool-rs v$IpatoolRsVersion."
}
[System.IO.File]::WriteAllText($ipaPatch, $ipaPatchText.Replace($metadataAnchor, $metadataPatched))
Write-Host "  -> patched $ipaPatchRelative (lower-case Apple ID in iTunesMetadata)"

$ipatoolRsDestination = Join-Path $OutDir "windows_amd64_sap_beta\ipatool.exe"
New-Item -ItemType Directory -Path (Split-Path -Parent $ipatoolRsDestination) -Force | Out-Null
Push-Location $ipatoolRsExtract
try {
    & cargo build --release --locked --bin ipatool
    if ($LASTEXITCODE -ne 0) { throw "Failed to build the patched ipatool-rs backend (is the Rust toolchain installed?)." }
}
finally {
    Pop-Location
}
$ipatoolRsBuilt = Join-Path $ipatoolRsExtract "target\release\ipatool.exe"
if (-not (Test-Path $ipatoolRsBuilt)) { throw "cargo reported success but ipatool.exe was not produced." }
Copy-Item $ipatoolRsBuilt -Destination $ipatoolRsDestination -Force
Write-Host ("  -> SAP backend SHA-256: " + (Get-FileHash -Path $ipatoolRsDestination -Algorithm SHA256).Hash.ToLowerInvariant())
Remove-Item $ipatoolRsArchive -Force -ErrorAction SilentlyContinue
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
