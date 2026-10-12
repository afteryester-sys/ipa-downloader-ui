# Plan: Direct Install to Device & Queue Stability

## Architecture & Implementation Plan

### 1. Direct Download UI & ViewModel Enhancements
- **ViewModel (`src/IPAStudio.App/ViewModels/DirectDownloadViewModel.cs`)**:
  - Expose `ObservableCollection<Device> ConnectedDevices` and `Device? SelectedDevice`.
  - Subscribe to `DeviceService.DeviceAdded` and `DeviceService.DeviceRemoved` (or populate from `_devices.Devices`).
  - Auto-select the first device when available and no device is selected.
  - Add `InstallToDeviceCommand`:
    - Checks `SavedPath` (or downloaded IPA).
    - Checks `SelectedDevice`.
    - Manages `IsInstalling`, `InstallProgress`, `InstallStatusText`.
    - Calls `_install.InstallAsync` with real-time `IProgress<InstallProgress>`.
    - Notifies `OperationService` so it appears in operation toasts.
- **View (`src/IPAStudio.App/Views/DirectDownloadView.xaml`)**:
  - Add a dedicated "Target Device" card between the Resolved App card and Action row (or right above Actions).
  - Use mini card chips:
    - Device icon (iOS Phone / Tablet glyph).
    - Device Name, Model, and iOS version.
    - Connection type indicator (USB / Wi-Fi).
    - Accent outline and background when selected (`IsSelected`).
  - If no devices: show subtle status banner with icon ("Нет подключенных устройств. Подключите iPhone или iPad по USB.").
  - In the Action row: add «Установить на устройство» (`Button.Primary` or `Button.Secondary` with lightning/arrow icon).
- **Localization (`Strings.ru.xaml` & `Strings.en.xaml`)**:
  - Add strings for `L.Direct.TargetDevice`, `L.Direct.NoDevices`, `L.Direct.InstallToDevice`, `L.Direct.Installing`, `L.Direct.InstallDone`.

### 2. Queue Stability & Error Recovery
- **QueueService (`src/IPAStudio.Core/Services/QueueService.cs`)**:
  - In `RunAsync`: inside `Parallel.ForEachAsync` lambda, catch `OperationCanceledException`:
    - If `token.IsCancellationRequested` or `ct.IsCancellationRequested`: rethrow (user cancelled).
    - If NOT requested: log warning and mark only `item` as `Failed` with appropriate status detail. Do not abort the loop!
  - In `ProcessItemAsync`:
    - `catch (OperationCanceledException) when (ct.IsCancellationRequested)` -> marks Cancelled, rethrow.
    - `catch (OperationCanceledException)` -> marks Failed with timeout/network error message.
  - In `RetryAsync`:
    - Check if `_cts is not null && !_cts.IsCancellationRequested`. If cancelled or null, create a fresh standalone `CancellationTokenSource`.
- **DownloadService (`src/IPAStudio.Core/Services/DownloadService.cs`)**:
  - In `ResolveExternalVersionIdAsync` and `TryLookupFileSizeAsync`:
    - Catch `OperationCanceledException when (!ct.IsCancellationRequested)` and treat as lookup failure rather than throwing out.
- **InstallService (`src/IPAStudio.Core/Services/InstallService.cs`)**:
  - When account mismatch occurs, do not block if called from user installation where mismatch warning was dismissed/allowed (`allowAccountMismatch: true`).

### 3. Verification & Build
- Verify clean compilation with `dotnet build`.
- Bump version to `1.8.46` in `IPAStudio.App.csproj`.
- Push to GitHub `main` branch to trigger `auto-release.yml`.
