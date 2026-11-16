# Technical Plan: Firmware Batch Workflow Resilience, Automation & Performance

## 1. Architecture & Component Changes

### A. Core Services
1. **`FirmwareDownloadService.cs`**:
   - Refactor multi-segment downloading to write directly to a shared target `.part` file:
     - Open target file with `FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite`.
     - Pre-allocate total length once (`output.SetLength(length)`).
     - Each segment stream seeks to `segment.Start + segment.Downloaded` and writes incoming bytes directly to its slice.
     - When all segments finish, the file is already assembled.
     - Eliminate separate segment part files, eliminating 100% of the disk copy phase.
   - Add `CheckAvailableDiskSpace(string folder, long requiredBytes, out long availableBytes)`:
     - Inspect `DriveInfo` for the root drive.
     - Provide structured diagnostics.
   - Enhance `CleanupInvalidTemporaryFiles` and add `PruneObsoleteFirmwaresAsync(string folder, IEnumerable<FirmwareDevice> monitoredDevices, Func<string, Task<IReadOnlyList<FirmwareRelease>>> fetchReleases)`:
     - Delete obsolete unsigned `.ipsw` files safely.

2. **`SettingsService.cs` & `Settings.cs`**:
   - Ensure all firmware settings are persisted:
     - `FirmwareAutoCheckEnabled`
     - `FirmwareCheckIntervalMinutes`
     - `FirmwareAutoDeleteObsolete`
     - `FirmwareSequentialDownload`
     - `FirmwareVerifyHash`
     - `FirmwareAutoResumeOnLaunch`
     - `FirmwareAutoDismissHistory`

### B. ViewModels
1. **`FirmwareViewModel.cs`**:
   - Dynamic Start/Stop state:
     - Add `bool IsWorkflowActiveOrRunning => IsChecking || IsWorkflowActive || HasActiveJobs;`
     - `ToggleFirmwareWorkflowCommand`: If `IsWorkflowActiveOrRunning`, cancel/stop workflow; otherwise call `StartFirmwareWorkflowAsync(force: true)`.
     - Bind button text, icon, and background dynamically.
   - Robust Execution Flow in `StartFirmwareWorkflowAsync`:
     - Free space check before starting jobs.
     - Pruning of broken / obsolete files.
     - Auto-resuming of any pending `.part` downloads in `DestinationFolder`.
     - Queueing of signed releases for all monitored devices.
   - History & Errors Enhancement:
     - Enhance `FirmwareLogEntry` with category: `LogCategory` (`Info`, `Error`, `Download`, `Network`).
     - Observable filtered collection: `VisibleLogs` based on active filter tab (`All`, `Errors`, `Downloads`).
     - `ErrorCount`: Count of error-level log entries with badge display.
     - `AutoDismissHistory`: Timer-based auto-close of History popup after 5 seconds if enabled.
   - Network Recovery:
     - Reconnect loop with exponential backoff and network availability listeners.
     - Auto-resume without user intervention.
   - Auto-launch startup:
     - In `OnNavigatedTo`, scan for pending `.part` downloads and auto-resume if configured.

### C. Views & Styles
1. **`FirmwareView.xaml`**:
   - Update Start button to bind to `ToggleFirmwareWorkflowCommand`, with dynamic text, icon, and danger styling when active.
   - History button with error badge indicator.
   - History popup with tab filter (`Все`, `Ошибки`, `Загрузки`), close [✕] button, and auto-close toggle switch.
   - Enhanced animations on buttons (hover/press scale effects, pulsing indicators).
2. **Localization**:
   - Add all new string keys to `Strings.ru.xaml` and `Strings.en.xaml`.
   - Run `build/Check-Localization.ps1` to ensure consistency.

## 2. Verification Strategy
- Clean build: `dotnet build -c Release` (0 warnings, 0 errors).
- Localization check: `powershell -ExecutionPolicy Bypass -File build/Check-Localization.ps1`.
- Verify direct single-file writing logic.
- Verify toggle state transitions and settings persistence.
