# Implementation Plan: Quick Transfer (iMazing-style File & Media Transfer)

**Branch**: `001-quick-transfer` | **Date**: 2026-10-01 | **Spec**: [Quick Transfer Spec](./spec.md)

## Summary

Implement an intelligent, iMazing-style Quick Transfer system for IPA Studio. When files or folders are dropped into the Quick Transfer window, the system analyzes file extensions/MIME signatures, determines their categories (Video, Audio, Books, Documents, Photos, Archives, IPAs), scans the connected iPhone for compatible apps, and dynamically highlights and auto-selects the optimal target app (e.g. VLC for videos, Books for e-books, Documents for archives/office). Transfers execute via Apple's native `house_arrest` AFC service with live progress, transfer speed, and post-transfer verification. In strict accordance with the user's requirement, automated tests are implemented and executed before deployment.

## Technical Context

**Language/Version**: C# 12 / .NET 8 (`net8.0-windows`)
**Primary Dependencies**: WPF, `CommunityToolkit.Mvvm`, `iMobileDevice-net` (libimobiledevice AFC & house_arrest)
**Storage**: Ephemeral transfer streams; sandboxed iOS app `/Documents/` via AFC
**Testing**: Comprehensive unit test suite (`IPAStudio.Core.Tests`) with automated test runner script
**Target Platform**: Windows 10/11 x64 connecting to iOS 12.0 – iOS 18.x
**Project Type**: Desktop GUI (WPF MVVM) + Device Service Layer
**Performance Goals**: < 100ms classification & ranking; max USB bandwidth (30–60 MB/s)
**Constraints**: Zero regressions to existing download/auth/install features; 7-bit ASCII safe staging paths

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- [x] **Principle I: Safety & Non-Breaking Invariant**: Preserves all existing download, authentication, and installation mechanisms intact.
- [x] **Principle II: Test-First Verification**: Automated tests for classification, app ranking, and transfer verification are written and pass before implementation release.
- [x] **Principle III: Native Device & Apple Protocol Integrity**: Uses Apple's standard `house_arrest` + AFC protocol, guaranteeing compatibility across iOS versions without jailbreak.
- [x] **Principle IV: Clean WPF & MVVM Architecture**: Business logic isolated in `FileClassifier` and `AppRecommendationEngine` in `IPAStudio.Core`; UI bindings in `QuickTransferDialog`.
- [x] **Principle V: Transparent Progress**: Emits percentage, transferred bytes, total size, and EMA speed.

## Project Structure

### Documentation (this feature)

```text
specs/001-quick-transfer/
├── spec.md              # Feature specification
├── plan.md              # This implementation plan
├── research.md          # Technical research & decisions
├── data-model.md        # Entities & data models
├── quickstart.md        # Test & validation quickstart
├── contracts/           # Interfaces (IFileClassifier, IAppRecommendationEngine)
└── tasks.md             # Execution task breakdown
```

### Source Code

```text
src/IPAStudio.Core/
├── Models/
│   ├── FileSharingModels.cs      # FileCategory, TransferPayload, AppMatchScore
├── Services/
│   ├── FileClassifier.cs         # Pure classification & payload description
│   ├── AppRecommendationEngine.cs# iMazing-style smart matching & scoring
│   ├── FileSharingService.cs     # House arrest & AFC upload engine
tests/
└── IPAStudio.Core.Tests/
    ├── FileClassifierTests.cs    # 50+ unit tests for file category detection
    ├── AppRecommendationTests.cs# App matching & ranking algorithm tests
    └── TestRunner.cs             # Automated test execution harness
src/IPAStudio.App/
├── Views/
│   ├── QuickTransferDialog.xaml  # Modernized iMazing-style UI with recommendation strip
│   └── QuickTransferDialog.xaml.cs# ViewModel/code-behind bindings & event hooks
```

## Complexity Tracking

No constitutional violations. Design uses existing services, follows MVVM, and adds pure, testable components.
