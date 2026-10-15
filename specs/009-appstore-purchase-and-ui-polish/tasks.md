# Implementation Tasks: 009-appstore-purchase-and-ui-polish

**Feature**: App Store Purchase Button, Settings & Operations Popup Polish, Firmware Auto-Check Spinner, and Button Hover Boundary Fix  
**Target Version**: 1.8.54  

---

- [x] [T001] Implement `PurchaseAsync` command and state in `DirectDownloadViewModel.cs`
- [x] [T002] Add "Купить" (Purchase) button with styling and icon to `DirectDownloadView.xaml`
- [x] [T003] Fix Settings gear popup: add toggle dismiss handler in `MainWindow.xaml.cs` and smooth unfolding animation in `MainWindow.xaml`
- [x] [T004] Remove disruptive hover scale expansion from `Button.Primary` in `Theme.xaml` to fix button clipping across all menus
- [x] [T005] Reposition Operations popup directly beneath the status circle (`HorizontalOffset="-146"`, `RenderTransformOrigin="0.5, 0.0"`) and add toggle dismiss handler in `MainWindow.xaml.cs`
- [x] [T006] Implement rotating spinner and last check timestamp for firmware auto-check in `FirmwareView.xaml` and auto-trigger on completion in `FirmwareViewModel.cs`
- [x] [T007] Add bilingual localization keys in `Strings.ru.xaml` and `Strings.en.xaml`
- [x] [T008] Update `ReleaseNotesService.cs` with v1.8.54 entries and bump version in `IPAStudio.App.csproj`
- [x] [T009] Verify localization parity via `check_loc.py` and run `dotnet build`
- [x] [T010] Push commit to GitHub repository and monitor CI `Auto Release` for v1.8.54
