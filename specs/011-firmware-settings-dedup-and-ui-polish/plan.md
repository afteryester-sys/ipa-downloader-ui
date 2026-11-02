# Implementation Plan: Firmware Settings Persistence, Queue Deduplication & UI Polish

**Feature Folder**: `specs/011-firmware-settings-dedup-and-ui-polish`  
**Architecture & Tech Stack**: .NET 8 WPF, MVVM CommunityToolkit, Apple Liquid Glass Design Language.

## Technical Architecture & Changes
1. **Settings Persistence**:
   - Add `[System.Text.Json.Serialization.JsonIgnore]` attribute to `FirmwareCheckIntervalHours` in `SettingsService.cs` to prevent deserialization conflict with `FirmwareCheckIntervalMinutes`.
   - Add `partial void OnSegmentCountChanged(int value)` and `partial void OnIsAutoCheckEnabledChanged(bool value)` in `FirmwareViewModel.cs` to save immediately upon change.
   - Invoke `PersistDownloadSettings()` within `CloseSettings()` in `FirmwareViewModel.cs`.
2. **Queue Deduplication**:
   - In `StartFirmwareWorkflowAsync` and `EnqueueBatchReleases`, inspect existing jobs for matching path or device identifier + release build id.
   - If job state is `Queued` or `IsActive`, skip enqueuing without creating duplicate rows.
   - In `EvaluateSchedule`, prune any accidental duplicates by tracking visited destination paths.
3. **Device Download Timestamps**:
   - Add `LastDownloadText` and `HasDownloadedFirmware` to `FirmwareDevice`.
   - In `RefreshAutoUpdateTimestamps`, resolve `sub?.LastDownloadUtc` for each device, formatting `Загрузка: ДД.ММ.ГГГГ ЧЧ:ММ` or `Не загружалась`.
   - In `StartFirmwareWorkflowAsync`, call `RefreshAutoUpdateTimestamps()` and update `LastTssCheckText = string.Format(Loc.Get("L.Firmware.LastChecked"), DateTime.Now.ToString("HH:mm"))`.
4. **Queue Header Layout & Liquid Glass Button Styles**:
   - In `FirmwareView.xaml`, define `QueueActionButton` style with `FocusVisualStyle="{x:Null}"`, rounded corners (7px), and WPF Storyboards for hover scale (1.025) and press scale (0.96).
   - Convert queue header `DockPanel` to a responsive 2-column `Grid` with `MaxWidth="170"` and `TextTrimming="CharacterEllipsis"` on `OverallSizeText`.
   - Bind `PauseAllCommand` visibility to `HasActiveJobs`, `ResumeAllCommand` to `HasResumableJobs`, and `ClearFinishedCommand` to `HasFinishedJobs`.
   - Localize active suffix with `L.Firmware.ActiveCountSuffix`.
