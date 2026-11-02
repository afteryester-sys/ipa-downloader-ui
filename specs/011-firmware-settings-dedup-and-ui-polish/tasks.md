# Tasks: Firmware Settings Persistence, Queue Deduplication & UI Polish

- [x] [T001] Add `[JsonIgnore]` to `FirmwareCheckIntervalHours` in `SettingsService.cs` to prevent deserialization overwrite.
- [x] [T002] Add `LastDownloadText` and `HasDownloadedFirmware` properties to `FirmwareDevice` in `FirmwareModels.cs`.
- [x] [T003] Add `Release` property alias on `FirmwareDownloadJob` for ergonomic release inspection.
- [x] [T004] Implement deduplication in `StartFirmwareWorkflowAsync` and `EnqueueBatchReleases` in `FirmwareViewModel.cs` for queued, active, and existing jobs.
- [x] [T005] Update `EvaluateSchedule` in `FirmwareViewModel.cs` to eliminate duplicate destination paths.
- [x] [T006] Update `RefreshAutoUpdateTimestamps` to populate device card `LastDownloadText` and `HasDownloadedFirmware`.
- [x] [T007] Update `StartFirmwareWorkflowAsync` to dynamically refresh `LastTssCheckText` timestamp every minute.
- [x] [T008] Add `OnSegmentCountChanged`, `OnIsAutoCheckEnabledChanged`, and `PersistDownloadSettings` calls on modal close.
- [x] [T009] Define `QueueActionButton` style with Apple Liquid Glass aesthetics, cubic easing animations, and `FocusVisualStyle="{x:Null}"` in `FirmwareView.xaml`.
- [x] [T010] Redesign queue header with a responsive 2-column Grid with text trimming to prevent button overlap.
- [x] [T011] Bind device cards to `LastDownloadText` with green/muted status indicator.
- [x] [T012] Add localized strings to `Strings.ru.xaml` and `Strings.en.xaml` and verify 100% parity.
- [x] [T013] Add v1.8.64 release notes in `ReleaseNotesService.cs` and bump version in `IPAStudio.App.csproj`.
- [x] [T014] Verify build passes with 0 errors and 0 warnings in Debug and Release modes.
