# IPA Studio Constitution

## Core Principles

### I. Safety & Non-Breaking Invariant (NON-NEGOTIABLE)
Existing working features (Apple ID authentication, SAP Beta kbsync downloads, 8.3 path compatibility, device pairing, in-app updater) MUST NEVER be broken. Any code change must preserve existing regressions safety and maintain backward compatibility.

### II. Test-First Verification (NON-NEGOTIABLE)
Before deploying or merging any new feature or engine change, comprehensive tests (unit and/or contract integration tests) MUST be written and executed. No feature goes into production without proven passing tests.

### III. Native Device & Apple Protocol Integrity
All device communication must strictly adhere to Apple service contracts via `libimobiledevice` / `iMobileDevice-net` (AFC, house_arrest, installation_proxy, mobile_image_mounter). Staging paths and temporary native handles must remain 7-bit ASCII safe on Windows to avoid filesystem codepage crashes.

### IV. Clean WPF & MVVM Architecture
UI logic must live in ViewModels using `CommunityToolkit.Mvvm`. Views are strictly for presentation and drag-and-drop bindings. Long-running device or network tasks must be asynchronous, cancellable via `CancellationToken`, and provide real-time progress reporting without freezing the UI thread.

### V. User Feedback & Transparent Progress
Any file transfer, download, or device operation must provide instant, accurate real-time feedback: bytes transferred, total size (or honest indeterminate indicator), current speed (EMA), and clear localized status messages in Russian and English.

## Additional Constraints

- **Runtime**: .NET 8 (net8.0-windows), C# 12 / latest.
- **Operating Systems**: Windows 10/11 x64.
- **CI/CD & Delivery**: Automated builds via GitHub Actions; releases published with version bump in `IPAStudio.App.csproj` and installer artifact `IPAStudio-Setup-<version>.exe`.
- **Localization**: All user-visible strings must be in localization dictionaries (`Loc.Get` / `Loc.Format`).

## Development Workflow & Quality Gates

1. **Specification**: Feature requirements, user flows, and acceptance criteria defined via Spec Kit.
2. **Architecture & Plan**: Component design, protocol contracts, and test strategy in `plan.md`.
3. **Tasks**: Dependency-ordered breakdown in `tasks.md`.
4. **Test Implementation**: Test suite created and verified before finalizing code.
5. **Implementation**: Code changes executed strictly according to spec.
6. **Convergence**: Verification against requirements.

## Governance

This Constitution supersedes informal agreements. Any amendment requires rationale and semantic version bump.

**Version**: 1.0.0 | **Ratified**: 2026-10-01 | **Last Amended**: 2026-10-01
