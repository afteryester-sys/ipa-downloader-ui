# Implementation Plan: Direct Download Queue & Menu Overflow Fix

- **Spec**: `specs/010-direct-download-queue/spec.md`
- **Target Version**: `1.8.56`

---

## 1. Architecture & Design

### A. Window Bounds Fix
In `src/IPAStudio.App/MainWindow.xaml`:
- Update `ScrollViewer` inside `UpdaterPopup`:
  ```xaml
  MaxHeight="{Binding ActualHeight, ElementName=Root,
              Converter={StaticResource Subtract}, ConverterParameter=165}"
  ```
  This caps the scrollable area to `Window.Height - 165px`, ensuring the entire popup card plus margin and padding stays strictly inside the client area of `MainWindow`.

### B. Direct Download Queue Model (`DirectDownloadQueueItem.cs`)
Create `src/IPAStudio.App/ViewModels/DirectDownloadQueueItem.cs`:
- Inherits from `ObservableObject`.
- Observable Properties:
  - `AppEntry App`
  - `string Name`, `string BundleId`, `string Version`, `string Developer`
  - `string? IconUrl`, `ImageSource? IconImage`
  - `string DestinationFolder`, `string? FileNameOverride`
  - `string? IpaPath`
  - `DirectQueueState State` (Queued, Connecting, Transferring, Finalizing, Done, Failed, Cancelled, Installing)
  - `double Progress` (0..100)
  - `bool IsIndeterminate`
  - `string StatusText`
  - `string? ErrorText`
  - `long DownloadedBytes`, `long TotalBytes`, `double SpeedBps`
  - `string FormattedSize`, `string FormattedSpeed`, `string RemainingText`
  - `bool IsDevicePickerOpen`
  - `ObservableCollection<SelectableDevice> TargetDevices`
  - `bool HasConnectedDevices`, `bool CanInstall`
  - `CancellationTokenSource? Cts`
  - `Operation? Operation`
- RelayCommands:
  - `CancelCommand`: cancels active download or installation
  - `RetryCommand`: requeues failed/cancelled download
  - `OpenFolderCommand`: opens Windows Explorer with .ipa selected
  - `ToggleDevicePickerCommand`: opens/closes device installation drawer
  - `InstallToDeviceCommand`: installs the downloaded .ipa onto selected devices
  - `RemoveCommand`: removes item from queue list

### C. DirectDownloadViewModel Queue Integration
In `src/IPAStudio.App/ViewModels/DirectDownloadViewModel.cs`:
- Add `ObservableCollection<DirectDownloadQueueItem> DownloadQueue`.
- Add `bool HasQueue => DownloadQueue.Count > 0`.
- Update `DownloadAsync`:
  - When user clicks "Скачать" (or "В очередь"):
    - Instead of setting single-item `IsDownloading = true` on the whole page, construct a `DirectDownloadQueueItem` and add to `DownloadQueue`.
    - Automatically trigger background queue processing (`ProcessQueueAsync`).
    - Keep search inputs and purchase button active and unlocked.
  - Implement `ProcessQueueAsync`:
    - Checks for next `Queued` item.
    - Limits parallel downloads (1 active direct download at a time to prevent Apple ID authentication collisions, with smooth sequential transition).
    - Updates item progress, speed, and status in real-time.
    - Registers background operation in `OperationService`.
  - Add `ClearFinishedQueueCommand`:
    - Removes all finished, cancelled, or failed downloads from the list.

### D. DirectDownloadView.xaml UI Redesign
In `src/IPAStudio.App/Views/DirectDownloadView.xaml`:
- In the action row:
  - Button "В очередь" / "Скачать" with icon `&#xE896;` or `&#xE898;`.
  - Keep "Купить" button active.
- Below the action row:
  - If `HasQueue == true`:
    - Display "Очередь загрузок" Card:
      - Header: icon, title, active/total items count, "Очистить завершённые" button.
      - ItemsControl bound to `DownloadQueue` with custom item template:
        - Compact modern card for each download.
        - App icon + Name + Version + Bundle ID.
        - Progress bar with animated accent fill.
        - Real-time speed, byte count, and remaining time.
        - Action buttons: Cancel, Install (with device picker drawer), Folder, Retry, Remove.

### E. Localization & Release Notes
- Add keys to `Strings.ru.xaml` and `Strings.en.xaml`.
- Update `ReleaseNotesService.cs` with `1.8.56` entries.
- Bump version in `IPAStudio.App.csproj` to `1.8.56`.
