# Implementation Tasks: 010-direct-download-queue

- **Feature**: Direct Download Queue & Menu Overflow Fix
- **Target Version**: `1.8.56`

---

- [ ] [T001] Fix Settings Menu Height Overflow in `src/IPAStudio.App/MainWindow.xaml`: change `ConverterParameter=110` to `165` on `UpdaterPopup` `ScrollViewer`.
- [ ] [T002] Create `DirectDownloadQueueItem.cs` in `src/IPAStudio.App/ViewModels/` with states, progress reporting, commands (Cancel, Retry, OpenFolder, Remove, InstallToDevice).
- [ ] [T003] Implement Queue Management and Background Processor in `src/IPAStudio.App/ViewModels/DirectDownloadViewModel.cs` (`DownloadQueue`, `ProcessQueueAsync`, `ClearFinishedQueueCommand`).
- [ ] [T004] Update `src/IPAStudio.App/Views/DirectDownloadView.xaml` to embed the Queue UI with responsive cards, progress bars, and item action buttons.
- [ ] [T005] Add bilingual localization keys to `Strings.ru.xaml` and `Strings.en.xaml`.
- [ ] [T006] Add v1.8.56 release notes to `ReleaseNotesService.cs` and bump version in `IPAStudio.App.csproj`.
- [ ] [T007] Verify localization parity via `check_loc.py` and run `dotnet build`.
- [ ] [T008] Push commit to GitHub repository and monitor CI `Auto Release` for v1.8.56.
