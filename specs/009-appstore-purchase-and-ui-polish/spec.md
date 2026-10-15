# Specification: 009-appstore-purchase-and-ui-polish

**Feature**: App Store Purchase Button, Settings & Operations Popup Polish, Firmware Auto-Check Spinner, and Button Hover Boundary Fix  
**Target Version**: 1.8.54  
**Date**: 2026-10-16  

## 1. Problem Statement & Motivation
Users encountered five distinct UX and functional limitations across the application:
1. **App Store Acquisition ("Купить")**: In the "Скачать IPA" (Direct Download) view, if an app is not yet purchased/licensed under the current Apple ID, the user could not acquire a free license directly within the app before downloading.
2. **Settings Gear Popup**: Clicking the gear button opened the menu without a smooth origin-based animation, and clicking the gear button while the menu was open failed to close it (it immediately re-opened due to standard WPF Popup lost-focus semantics).
3. **Firmware Auto-Check Indicator**: When downloads complete and auto-check is enabled, there was no visual feedback (rotating spinner and last-check timestamp) on or beside "Мои устройства" to indicate that automated background monitoring and signature checks are active.
4. **Button Hover Boundary Clipping**: In several menus (e.g. the update menu in Screenshot 3), hovering over buttons scaled them by `1.025`, causing the right or outer edges to expand beyond the container boundaries and get clipped by parent layout frames.
5. **Operations Circle Popup Position & Animation**: The operations popup had an excessive negative horizontal offset (`-268`), pushing it far to the left under the user email instead of appearing naturally anchored beneath the status circle. Furthermore, clicking the status circle while the popup was open failed to dismiss it.

## 2. Requirements & Desired Behavior

### R1. App Store "Купить" (Purchase) Action in Direct Download
- Add a dedicated "Купить" (Purchase) button in [`DirectDownloadView.xaml`](file:///c:/Users/after/OneDrive/Документы/Ipa%20Studio/src/IPAStudio.App/Views/DirectDownloadView.xaml) alongside "Скачать IPA".
- Bound to `PurchaseCommand` in [`DirectDownloadViewModel.cs`](file:///c:/Users/after/OneDrive/Документы/Ipa%20Studio/src/IPAStudio.App/ViewModels/DirectDownloadViewModel.cs).
- Executes `_downloads.PurchaseAsync(FoundApp.AppStoreId, FoundApp.BundleId, ...)` using ipatool's built-in purchase mechanism.
- Provides informative status messages: "Приобретение приложения...", "Приложение успешно получено в App Store", or detailed localized error if failed.
- Once purchased, the app is immediately marked as owned and can be downloaded without leaving the page.

### R2. Settings Gear Menu Toggle & Smooth Unfolding Animation
- Handle `PreviewMouseDown` on the gear button (`UpdateButton`) so that if the menu is already open, clicking the button closes it without re-triggering.
- Add smooth spring unfolding animation (`RenderTransformOrigin="1.0, 0.0"`, `ScaleTransform` + `BackEase Out`) so the flyout gracefully emerges directly from the gear icon.

### R3. Firmware Auto-Check Spinner & Freshness Display
- On the "Мои устройства" header in [`FirmwareView.xaml`](file:///c:/Users/after/OneDrive/Документы/Ipa%20Studio/src/IPAStudio.App/Views/FirmwareView.xaml):
  - When `IsChecking` is true or auto-check is active, animate the refresh icon `&#xE72C;` with a continuous smooth rotation.
  - Display the last check time / auto-check indicator right beside the header and on inactive device cards.
  - When firmware downloads finish and `IsAutoCheckEnabled` is true, automatically trigger a signature check and update timestamps.

### R4. Fix Button Hover Clipping Across All Menus
- In [`Theme.xaml`](file:///c:/Users/after/OneDrive/Документы/Ipa%20Studio/src/IPAStudio.App/Resources/Theme.xaml) (and [`Button.Primary`](file:///c:/Users/after/OneDrive/Документы/Ipa%20Studio/src/IPAStudio.App/Resources/Theme.xaml#L55-L122) / [`Button.Secondary`](file:///c:/Users/after/OneDrive/Документы/Ipa%20Studio/src/IPAStudio.App/Resources/Theme.xaml#L178-L185)):
  - Remove the disruptive `1.025` hover scale transform expansion that cuts off button borders against container clip bounds.
  - Rely on refined Apple-style background/opacity/border highlights on hover, retaining tactile press compression (`0.96` scale) on click.

### R5. Operations Popup Repositioning & Dismissal
- Center or naturally anchor `OperationsPopup` beneath the `OpsButton` status circle (adjust `HorizontalOffset` and `RenderTransformOrigin="0.5, 0.0"`).
- Add `PreviewMouseDown` handler so clicking the circle while the popup is open reliably dismisses it.

## 3. Success Verification
- `dotnet build` succeeds without errors.
- `python scratch/check_loc.py` validates 100% parity of Russian and English keys.
- Version 1.8.54 tagged and released via GitHub Actions CI.
