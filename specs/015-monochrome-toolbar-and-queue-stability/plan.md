# Implementation Plan: Monochrome Toolbar, Background Hover Fix & Download Queue Reliability

## 1. Theme and Resource Dictionaries
1. `src/IPAStudio.App/Resources/Palette.Dark.xaml` & `Palette.Light.xaml`:
   - Declare `Color.SurfaceActive` and `Brush.SurfaceActive`.
   - Provide backward-compatible alias brushes `Brush.Hover`, `Brush.Text.Primary`, and `Brush.Text.Secondary`.
2. `src/IPAStudio.App/MainWindow.xaml`:
   - Replace missing `Brush.Text.Primary` and `Brush.Text.Secondary` with `Brush.Text` and `Brush.TextMuted`.

## 2. Views and Toolbar
1. `src/IPAStudio.App/Views/DevicesView.xaml`:
   - Fix `ToolCardButton` style to bind `Background` and `BorderBrush` dynamically using `Brush.SurfaceHover` and `Brush.Accent`.
   - Restyle tool badges (Download IPA, Firmware, iCloud, Media Export, Statistics) with unified single-color styling (`Brush.AccentSoft` + `Brush.Accent`).

## 3. Direct Download Queue
1. `src/IPAStudio.App/ViewModels/DirectDownloadViewModel.cs`:
   - Add robust app comparison logic for queue presence check (handling both `BundleId` and `AppStoreId`).
   - Remove stale finished/failed cards when starting a fresh download for the same app.
   - Insert new downloads at index 0 of `DownloadQueue` on UI thread.
   - Execute queue in FIFO order by taking the oldest queued item.
   - Dispatch all queue and item property changes safely via `RunOnUi`.
2. `src/IPAStudio.App/ViewModels/DirectDownloadQueueItem.cs`:
   - Marshal `TargetDevices` collection modifications to the UI Dispatcher.

## 4. Verification & Release
1. Verify 0 missing XAML resources via static scan.
2. Verify Russian and English localization dictionaries via `build/Check-Localization.ps1`.
3. Validate Release build with `dotnet build IPAStudio.sln -c Release`.
4. Bump version to 1.8.71 and update release notes.
