# Tasks: Apple ID Authentication Failure Remediation (Store AMD & 301/204 Fix)

**Specification**: `specs/012-apple-auth-breakage-investigation/spec.md`  
**Plan**: `specs/012-apple-auth-breakage-investigation/plan.md`  

---

## Phase 1: Analysis & Reproduction Testing
- [x] [T001] Reproduce error log and diagnose root cause of `status=301` and `status=204` on legacy `ipatool-cpp 1.0.9`.
- [x] [T002] Study 4PDA topic 1119329 (post 4420+) and verify community status on the Apple authentication lockout.
- [x] [T003] Study `kda2495/IPA_Downloader` repository (Issue #29) and examine community feedback.
- [x] [T004] Analyze `Sorvigolova/ipatool` PR #5 (`t0pd3v1c3`) code changes in `appstore.cpp`, `store_proof.h`, and `store_adi.cpp`.
- [x] [T005] Test `majd/ipatool v2.8.0` on Windows x64: measure cold-start provisioning time, range requests on 26.6 GB IPSW, and process timeout behavior.
- [x] [T006] Formulate comparison matrix between `Sorvigolova/ipatool PR #5` and `majd/ipatool v2.8.0`.

## Phase 2: Tooling & Binary Preparation
- [x] [T007] Prepare automated GitHub Actions workflow to build `Sorvigolova/ipatool` PR #5 (`d5050b1`) using MSYS2 MinGW64 on `windows-latest`.
- [x] [T008] Compile static `ipatool.exe` (~45 MB) with Unicorn x86/ARM64 and Store AMD support.
- [x] [T009] Deploy compiled binary to `tools/windows_amd64_sap_beta/ipatool.exe` and verify binary startup and `--help`.

## Phase 3: Application Code & Diagnostics Refinements
- [x] [T010] Update `AuthService.cs` with explicit classification for `status=301` and `status=204` (Apple Store AMD protocol transition).
- [x] [T011] Add localized user-friendly explanation strings in `Strings.ru.xaml`, `Strings.en.xaml`, and `Loc.cs`.
- [x] [T012] Verify `IpatoolProfile.cs` preserves `%USERPROFILE%\.ipatool\store-proof` directory across cookie resets and profile repairs.
- [x] [T013] Update `build/Fetch-Tools.ps1` with the new binary version and SHA-256 hash.

## Phase 4: Verification & Regression Testing
- [x] [T014] Run test sign-in flow (`auth login`) with PR #5 binary to verify Store AMD challenge (`SP`) handling and 2FA prompt generation.
- [x] [T015] Verify session restoration (`auth info`) on the new Store AMD session.
- [x] [T016] Run full unit/solution build (`dotnet build -c Release`) to guarantee 0 errors and 0 warnings.
