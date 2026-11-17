# Specification: Monochrome Toolbar, Background Hover Fix & Download Queue Reliability

## 1. Overview
This update resolves a critical UI-thread rendering exception (`DependencyProperty.UnsetValue` for property `Background`), unifies the main screen tool action buttons with a clean Apple-grade monochrome visual style (removing multicolored rainbow accents), and dramatically improves the Direct IPA Download queue responsiveness and ordering.

## 2. Requirements

### 2.1 Background Hover Exception Remediation
- Fix `InvalidOperationException: '{DependencyProperty.UnsetValue}' is not a valid value for property 'Background'` caused by referencing undeclared dynamic/static resources during pointer hover on toolbar buttons.
- Add complete fallback declarations for `Brush.Hover`, `Brush.SurfaceActive`, `Brush.Text.Primary`, and `Brush.Text.Secondary` to both `Palette.Dark.xaml` and `Palette.Light.xaml`.
- Normalize all usage references across `DevicesView.xaml`, `MainWindow.xaml`, and `FirmwareView.xaml`.

### 2.2 Cohesive Monochrome Tool Buttons
- Redesign the 5 tool buttons on `DevicesView.xaml` (Download IPA, Firmware, iCloud, Media Export, Statistics) into a unified monochrome design matching the app's native aesthetic.
- Replace multicolored rainbow tinted backgrounds with standardized `{DynamicResource Brush.AccentSoft}` badge containers and `{DynamicResource Brush.Accent}` icons.

### 2.3 Direct Download Queue Stability & Ordering
- Fix the issue where downloads sometimes failed to add or did not appear in the queue until clicking "Clear finished":
  - Fix deduplication logic so queries without bundle IDs (numeric AppStoreId lookups) are not falsely blocked by `null == null` evaluation.
  - Automatically replace previous finished/failed/cancelled cards of the same app upon re-download.
  - Insert new downloads at index 0 of the queue list so they are immediately visible at the top of the queue without manual scrolling.
  - Maintain FIFO queue execution order by picking the oldest queued item for processing.
  - Strictly marshal all collection mutations (`DownloadQueue`, `TargetDevices`) and item property updates onto the UI Dispatcher.
