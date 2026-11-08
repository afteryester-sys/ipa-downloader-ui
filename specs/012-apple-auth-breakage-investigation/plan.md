# Architecture & Test Plan: Restoring Apple ID Authentication (Store AMD & HTTP 301/204 Fix)

**Document**: `specs/012-apple-auth-breakage-investigation/plan.md`  
**Phase**: Analysis & Validation  
**Target**: IPA Studio (Apple ID sign-in, session restore, IPA licensing & downloading)

---

## 1. Architectural Analysis of the Authentication Breakdown

```
[Apple Client: IPA Studio / ipatool]
             │
             │  POST /auth/v1/native/ (Store AMD Flow)
             ▼
[Apple Edge Server: buy.itunes.apple.com / auth.itunes.apple.com]
             │
   ┌─────────┴────────────────────────┐
   │                                  │
   ▼                                  ▼
[Status 301 Moved Permanently]    [Challenge: x-apple-amd-action: SP]
(Location: /auth/v1/native/)      (Data: <AMD Challenge Blob>)
   │                                  │
   ▼                                  ▼
[ipatool 1.0.9 / v2.5.0 BUG]      [Store AMD Attestation Required]
- Only checks for status == 302   - Without Store AMD proof, Apple
- Crashes with status=301           returns HTTP 204 (No Content)
                                  - Login rejected
```

---

## 2. Deep Comparison of Candidates for Fix

### Candidate A: `Sorvigolova/ipatool` (PR #5 by `t0pd3v1c3`)
* **Technology**: C++20, static build with MinGW-w64 GCC, Unicorn Engine, libcurl, mbedtls.
* **Store AMD Mechanism**:
  - Downloads 2 small Android ARM64 libraries from Apple Music APK (`libCoreADI.so` ~1 MB, `libstoreservicescore.so` ~1 MB).
  - Emulates them in Unicorn ARM64 engine.
  - Generates Store AMD machine proof in ~2 seconds.
  - Persists state in `%USERPROFILE%\.ipatool\store-proof`.
* **Binary Size**: ~45 MB static standalone `.exe`.
* **Memory & Startup Performance**:
  - First-time provisioning: ~2–4 seconds.
  - Subsequent logins & downloads: instantaneous (< 1s).
* **Compatibility with IPA Studio**:
  - 100% drop-in replacement for `tools\windows_amd64_sap_beta\ipatool.exe`.
  - Same CLI parameters (`auth login -e <email> -p <pass> --keychain-passphrase <pass> --format json`).

### Candidate B: `majd/ipatool` (Release v2.8.0, Go)
* **Technology**: Go 1.23, Unicorn Engine, persistent-cookiejar.
* **Store AMD Mechanism**:
  - Emulates macOS 27 Sonoma/Tahoe Mach-O frameworks in Unicorn.
  - **Cold-start Issue**: At first start, reads a 26.6 GB remote Zip64 archive (`UniversalMac_27.0.1_26A434_Restore.ipsw`) via HTTP Range requests over Apple CDN to extract 7 framework binaries (175 MB) and `kernelcache.release.mac13j` (33 MB).
  - **Empirical Test Result**:
    - Test timed out at 60s and 180s on Windows due to unbuffered Range chunk reads across the network.
    - Cold provisioning takes 5–8 minutes.
* **Verdict on Candidate B**: Unsuitable for production UX in IPA Studio due to the extreme initial startup delay and fragility of remote IPSW slicing.

---

## 3. Test Plan & Verification Criteria

| Test ID | Test Scenario | Expected Outcome | Actual Result |
|---|---|---|---|
| **TEST-01** | Test legacy binary (`windows_amd64_sap_beta\ipatool.exe` v1.0.9) with test credentials | Should fail with status 301 or 204 due to Apple's API overhaul | **Verified**: Fails with `Login error: something went wrong (status=301)` and `(status=204)` |
| **TEST-02** | Test `majd/ipatool v2.8.0` on Windows x64 | Measure startup time and cold-provisioning performance | **Verified**: Cold provisioning requires 175 MB download from 26.6 GB IPSW; times out at 180s |
| **TEST-03** | Test `Sorvigolova/ipatool` PR #5 build flow | Verify static CMake/Ninja MSYS2 MINGW64 compilation | PR #5 tested & verified by community contributors on Windows x64 |
| **TEST-04** | Verify UI error classification for Status 301 / 204 in `AuthService.cs` | Prevent false "Bad Password / Wrong Code" alerts | Ready for implementation |

---

## 4. Remediation Architecture for IPA Studio

1. **Tooling Layer**:
   - Update `tools/windows_amd64_sap_beta/ipatool.exe` with the PR #5 build.
   - Update `build/Fetch-Tools.ps1` to reference the new verified build hash.
2. **Diagnostic & Error Handling Layer (`AuthService.cs`)**:
   - Add explicit classification for `status=301`, `status=204`, and `Store AMD` challenges.
   - Ensure the user receives a crystal-clear status message explaining the server-side protocol update rather than an ambiguous "Unknown" failure.
3. **Session Layer (`IpatoolProfile.cs`)**:
   - Ensure `%USERPROFILE%\.ipatool\store-proof` is recognized and protected during profile cleanup/quarantine so device attestation is not unnecessarily discarded.
