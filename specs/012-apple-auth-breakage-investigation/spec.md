# Feature / Bug Specification: Apple ID Authentication Failure (Status 301 / Status 204)

**Feature Branch/Folder**: `specs/012-apple-auth-breakage-investigation`  
**Status**: Investigated & Analyzed  
**Created**: 2026-11-09  

---

## 1. Problem Overview & Symptom Description

On or around **November 6–8, 2026**, Apple implemented sweeping changes to its App Store and iTunes authentication infrastructure (`buy.itunes.apple.com`, `auth.itunes.apple.com`).

As a result, all third-party App Store clients—including **IPA Studio**, **`kda2495/IPA_Downloader`**, and downstream `ipatool` forks—experienced complete failure during Apple ID login (`auth login`).

### Observed Error Log in IPA Studio
```log
02:00:56.246 [INFO ] Login: step 1 (no code) using SAP BETA.
02:00:56.309 [INFO ] RUN C:\...\tools\windows_amd64_sap_beta\ipatool.exe [sha256:394e8953d317] auth login --email leq77751@gmail.com --password ******** --keychain-passphrase [REDACTED] --format json
02:00:59.159 [INFO ] EXIT ipatool.exe code=1
02:00:59.160 [WARN ]   stderr: Login error: something went wrong (status=301)
02:00:59.161 [WARN ] Login failed (not a 2FA prompt): Login error: something went wrong (status=301)
02:00:59.163 [WARN ] Login failed (Unknown): Login error: something went wrong (status=301)
02:01:09.645 [INFO ] Login: step 1 (no code) using SAP BETA.
02:01:09.706 [INFO ] RUN C:\...\tools\windows_amd64_sap_beta\ipatool.exe [sha256:394e8953d317] auth login --email leq77751@gmail.com --password ******** --keychain-passphrase [REDACTED] --format json
02:01:13.385 [INFO ] EXIT ipatool.exe code=1
02:01:13.385 [WARN ]   stderr: Login error: something went wrong (status=204)
02:01:13.385 [WARN ] Login failed (not a 2FA prompt): Login error: something went wrong (status=204)
02:01:13.385 [WARN ] Login failed (Unknown): Login error: something went wrong (status=204)
```

---

## 2. Evidence & Research Findings

### A. 4PDA Community Topic (#1119329, post #4420+)
- Topic Title: **«IPA_Downloader (ipatool) - Скрипт для загрузки приложений из App Store»**
- In the topic header and posts starting from November 6, 2026, an official announcement was posted:
  > *"Важное объявление в шапке темы: Из-за изменений со стороны Apple, начиная с ноября 2026 года, авторизация в подобных инструментах временно переставала работать. Разработчики следят за ситуацией и ищут исправление (при этом уже авторизованные сессии или ранее сохраненные данные у многих продолжают функционировать, если не производить выход из аккаунта)."*
- Multiple users confirmed that existing saved accounts (`~/.ipatool` session keychain) continue downloading previously purchased apps, but logging in again (`Re-login` / `auth login`) immediately fails with `status=301` or `status=204`.

### B. Upstream `kda2495/IPA_Downloader` Repository (Issue #29)
- Issue `#29` opened on November 6, 2026: **"Login error (status=204 / status=301)"**.
- Script author (`kda2495`) confirmed on November 6:
  > *"Подтверждаю. Ну что же, Apple снова ставит нам палки в колеса. Будем пробовать решать. Жду разработчика ipatool-cpp (Sorvigolova). Данные ему отправил, посмотрим, можно ли быстро все исправить."*
  > *"Как только выпустит новую версию ipatool-cpp - выпущу новую версию IPA_Downloader"*
- Contributor `t0pd3v1c3` published a pull request to `Sorvigolova/ipatool` (PR #5) fixing the issue.

### C. `Sorvigolova/ipatool` (PR #5: "Fix: Restore App Store authentication and IPA downloads with Store AMD")
- Submitted on November 8, 2026 by `t0pd3v1c3`:
  - **Endpoint Changes**: Apple now routes native authentication to `/auth/v1/native/` with `application/x-apple-plist` Content-Type instead of legacy form encoding.
  - **Redirect Bug in old ipatool**: Apple issues HTTP `301 Moved Permanently` (and relative `/auth/` URLs) instead of only HTTP `302`. The old `ipatool-cpp` had a hardcoded `if (status == 302)` check; any 301 fell through to the unhandled failure branch: `throw IpaError("something went wrong (status=" + std::to_string(lastRes.statusCode) + ")")`.
  - **Hardware / Device Attestation (Store AMD / ADI)**:
    - Apple now responds to authentication attempts with challenge headers: `x-apple-amd-action: SP` and `x-apple-amd-data: <challenge>`.
    - If the client does not answer the challenge with Store AMD proof (`X-Apple-AMD-Data`), Apple rejects or answers with `204 No Content`.
    - PR #5 solves this by downloading two official Android ARM64 libraries from Apple Music APK (`libCoreADI.so` and `libstoreservicescore.so`), running them in a Unicorn Engine sandbox locally, and generating the required `Store AMD` response headers stored in `~/.ipatool/store-proof`.
- Status of PR #5: PR is pending review and merge by repository owner `Sorvigolova`.

### D. Upstream `majd/ipatool` (Release v2.8.0, Nov 8 2026)
- On November 8, 2026, `majd` merged PR #643 and published release **`v2.8.0`**:
  > *"Restored App Store authentication with local Anisette generation and provisioning. Migrated store operations and signing to modern protocol."*
- `majd/ipatool` v2.8.0 resolves the issue in Go by bundling full Apple macOS 27 Sonoma/Tahoe framework emulation (Unicorn) for Anisette v4 and Store AMD.
- Note on v2.8.0 on Windows: on first launch, it downloads ~175 MB of macOS framework components from Apple CDN via HTTP range requests to initialize the emulator.

---

## 3. Root Cause Analysis

| Factor | Technical Cause |
|---|---|
| **Apple API Overhaul** | Apple deprecated the legacy `MZFinance.woa/wa/authenticate` endpoint and made Store AMD device attestation (`SP` machine action) mandatory. |
| **HTTP 301 Failure** | Apple returns `301 Moved Permanently` to redirect clients to `/auth/v1/native/`. The older `ipatool-cpp` (v1.0.9 shipped in IPA Studio) only handled HTTP `302`, causing 301 to be classified as an unmapped error (`status=301`). |
| **HTTP 204 Failure** | When the client retried or contacted the new endpoint without valid Store AMD attestation headers, Apple returned HTTP `204 No Content`, which older `ipatool` threw as `status=204`. |
| **Outdated Bundled Binary** | IPA Studio is currently bundling `ipatool-cpp` v1.0.9 (dated Sep 30, 2026, SHA256 `394e8953d317...`) in `tools/windows_amd64_sap_beta/`, and legacy `majd/ipatool` v2.5.0 in `tools/windows_amd64_v2/`. Neither binary possesses Store AMD generation capability. |

---

## 4. Solution Strategy & Comparison

Three paths exist to restore authentication in IPA Studio:

### Option 1: Wait for `Sorvigolova/ipatool` Release (Merged PR #5) & `IPA_Downloader 4.3.4` (Recommended for Stability & Footprint)
- **Pros**:
  - `ipatool-cpp` is a lightweight native C++ binary (~45 MB) that uses tiny Android ARM64 libraries (~1 MB) for Store AMD rather than full 175 MB macOS frameworks.
  - Exactly aligns with `kda2495/IPA_Downloader` ecosystem (4PDA community standard).
  - Fast execution, low memory overhead.
- **Cons**: Depends on maintainer `Sorvigolova` merging PR #5 and publishing a release (expected within 24-48 hours).

### Option 2: Prebuild / Compile `Sorvigolova/ipatool` PR #5 on Windows (Immediate Native Fix)
- **Pros**:
  - Solves the problem immediately using the exact PR #5 code verified by `t0pd3v1c3` and the 4PDA community.
  - Zero dependency on upstream release timing.
- **Cons**: Requires MSYS2 MINGW64 / CMake / Ninja build environment with `nlohmann-json`, `minizip`, `mbedtls`.

### Option 3: Upgrade to `majd/ipatool` v2.8.0
- **Pros**: Already officially published on GitHub releases as precompiled `ipatool-2.8.0-windows-amd64.tar.gz`.
- **Cons**: Downloads 175 MB of macOS Mach-O binaries on first launch via HTTP range requests; can take 3–6 minutes to initialize on first run.

---

## 5. Actionable Next Steps

1. **Inform the User** with a clear, concise summary of the 4PDA and GitHub findings.
2. **Monitor / Track PR #5**: Track `Sorvigolova/ipatool` PR #5 and `kda2495/IPA_Downloader` issue #29.
3. **Enhance IPA Studio Diagnostics & UX**:
   - Update `AuthService.cs` to specifically detect `status=301` and `status=204` as Apple Server Protocol Overhaul errors, displaying a human-friendly notification:
     *"Apple обновила протокол авторизации App Store (Store AMD / HTTP 301). Разработчики ipatool уже подготовили исправление (PR #5), обновление ожидается в ближайшее время."*
   - Prevent misleading users into thinking they typed a wrong password or entered an invalid 2FA code.
