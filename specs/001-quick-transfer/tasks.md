# Implementation Tasks: Quick Transfer (iMazing-style)

**Feature**: Quick Transfer (001-quick-transfer)
**Date**: 2026-10-01
**Spec**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md)

## Phase 1: Setup & Models

- [x] T001 Define `FileCategory` enum and `TransferPayload`, `AppMatchScore` models in `src/IPAStudio.Core/Models/FileSharingModels.cs`
- [x] T002 Update `FileSharingApp` model in `src/IPAStudio.Core/Models/FileSharingModels.cs` to include supported category tagging and bundle metadata

## Phase 2: Foundational Engines (Pure Logic)

- [x] T003 Implement `FileClassifier` in `src/IPAStudio.Core/Services/FileClassifier.cs` mapping file extensions/MIME signatures to `FileCategory` with folder expansion
- [x] T004 Implement `AppRecommendationEngine` in `src/IPAStudio.Core/Services/AppRecommendationEngine.cs` with known app bundle registry and scoring heuristics

## Phase 3: Automated Test Suite (Mandatory Pre-Implementation Gate)

- [x] T005 [P] [US2] Implement comprehensive `FileClassifierTests.cs` in `tests/IPAStudio.Core.Tests/FileClassifierTests.cs` covering all media, document, book, archive, IPA, and edge cases
- [x] T006 [P] [US2] Implement `AppRecommendationTests.cs` in `tests/IPAStudio.Core.Tests/AppRecommendationTests.cs` verifying ranking (VLC for video, Books/Acrobat for books, fallback to generic containers)
- [x] T007 [US2] Implement `TestRunner.cs` / test harness in `tests/IPAStudio.Core.Tests/TestRunner.cs` and execute tests via CLI to ensure 100% pass rate before UI integration

## Phase 4: Modernized UI & iMazing-Style Flow

- [x] T008 [US1] Update `QuickTransferDialog.xaml` in `src/IPAStudio.App/Views/QuickTransferDialog.xaml` with modern drop-zone, smart destination recommendation cards, and 1-click app switcher
- [x] T009 [US1] [US3] Update `QuickTransferDialog.xaml.cs` in `src/IPAStudio.App/Views/QuickTransferDialog.xaml.cs` to wire `FileClassifier` and `AppRecommendationEngine`, displaying recommended apps instantly upon drop
- [x] T010 [US4] Update upload loop in `QuickTransferDialog.xaml.cs` to stream real-time progress (bytes, speed, percentage) and perform post-transfer byte verification

## Phase 5: Verification & Delivery

- [x] T011 Run the full automated test suite to verify 0 regressions
- [x] T012 Verify localization strings in `Localization.cs` or resource dictionaries for Russian and English
- [x] T013 Prepare commit and bump version for release
