# Implementation Plan: 009-appstore-purchase-and-ui-polish

**Feature**: App Store Purchase Button, Settings & Operations Popup Polish, Firmware Auto-Check Spinner, and Button Hover Boundary Fix  
**Target Version**: 1.8.54  

## Architecture & Component Breakdown

### 1. App Store Purchase in Direct Download
- **ViewModel** (`DirectDownloadViewModel.cs`):
  - Add `[ObservableProperty] private bool _isPurchasing;`
  - Update `IsActionInProgress` to incorporate `IsPurchasing`.
  - Implement `[RelayCommand] private async Task PurchaseAsync()`.
  - Validate authentication (`_auth.IsAuthenticated`). If not signed in, prompt error `L.Direct.NeedLogin`.
  - If `FoundApp` is null, run `LookupAsync()` first.
  - Call `_downloads.PurchaseAsync(FoundApp.AppStoreId, FoundApp.BundleId, CancellationToken.None)`.
  - Handle results: if success, set status `L.Direct.PurchaseSuccess`, mark owned via `_settings.MarkOwned(...)`; if session expired, set error `L.Error.SessionExpired`; otherwise show returned error or `L.Direct.PurchaseFailed`.
- **View** (`DirectDownloadView.xaml`):
  - In the action row (`<WrapPanel>`), add a secondary button:
    `<Button Content="{DynamicResource L.Direct.Purchase}" Style="{StaticResource Button.Secondary}" Command="{Binding PurchaseCommand}" ... />`
  - Show loading state or text when `IsPurchasing` is active.

### 2. Settings Gear Menu Toggle & Origin Animation
- **MainWindow.xaml.cs**:
  - Add `UpdateButton_PreviewMouseDown`: if `UpdaterPopup.IsOpen`, set `UpdaterPopup.IsOpen = false; e.Handled = true;`.
- **MainWindow.xaml**:
  - In `Popup` (named `UpdaterPopup`), set `PopupAnimation="None"`.
  - Inside `Border`, set `RenderTransformOrigin="1.0, 0.0"`.
  - Add `ScaleTransform` animation (`0.25 -> 1.0` with `BackEase Out`) and `Opacity` (`0 -> 1` with `CubicEase Out`) on `Loaded`.

### 3. Firmware Auto-Check Spinner & Timestamp
- **FirmwareViewModel.cs**:
  - When download queue finishes (`Jobs.All(j => !j.IsActive)`), if `IsAutoCheckEnabled` is true, trigger background TSS check: `_ = CheckAllDevicesCoreAsync();`.
  - Expose `IsChecking` and ensure `LastTssCheckText` is maintained.
- **FirmwareView.xaml**:
  - In "Мои устройства" header:
    - Add rotating animation storyboard to the refresh icon `&#xE72C;` when `IsChecking == true`.
    - Beside "Мои устройства", show subtle status: green dot + `LastTssCheckText` or auto-check indicator.
  - On device cards:
    - When `HasActiveDownload` is false and `IsAutoCheckEnabled` is true, show subtle TSS active badge/text.

### 4. Button Hover Boundary Fix
- **Theme.xaml**:
  - In `Button.Primary` control template triggers for `IsMouseOver`:
    - Remove the `ScaleTransform.ScaleX` and `ScaleTransform.ScaleY` hover animation to `1.025`. Keep scale at `1.0` or animate only `Opacity` and background/border brush.
    - Keep press `IsPressed` animation (`0.96` scale) for tactile feedback.
  - This eliminates edge clipping across all menus and cards in the application.

### 5. Operations Popup Repositioning & Dismissal
- **MainWindow.xaml.cs**:
  - Add `OpsButton_PreviewMouseDown`: if `OperationsPopup.IsOpen`, set `OperationsPopup.IsOpen = false; e.Handled = true;`.
- **MainWindow.xaml**:
  - Adjust `HorizontalOffset` on `OperationsPopup` from `-268` to `-140` (centering under the 34px button).
  - Set `RenderTransformOrigin="0.5, 0.0"` on the popup border.
  - Ensure the unfolding animation expands directly downward from the circle status indicator.

### 6. Localization & Release
- Add all localization keys in `Strings.ru.xaml` and `Strings.en.xaml`.
- Update `ReleaseNotesService.cs` with notes for v1.8.54.
- Bump version in `IPAStudio.App.csproj`.
- Validate with `check_loc.py` and `dotnet build`.
- Push commit and monitor `Auto Release`.
