# Tasks: Production Firmware IPSW Center & Intelligent Driver Setup Recovery

**Feature**: [spec.md](spec.md) | [plan.md](plan.md)  
**Target Version**: `1.8.42`  

## Task Checklist

- [x] **[T001]** Enhance `DependencyService.cs`:
  - Detect installed iTunes version and GUID.
  - Implement `IsLegacyITunesConflict(out string version, out string uninstallString)`.
  - Add `UninstallLegacyITunesAsync()`.
  - In `InstallITunesAsync()`: if silent execution returns non-zero, automatically fallback to interactive elevated execution with UI (`UseShellExecute = true, Verb = "runas"`), and preserve downloaded setup executable.

- [x] **[T002]** Update `SetupViewModel.cs`, `SetupView.xaml`, and localization files (`Strings.ru.xaml`, `Strings.en.xaml`):
  - Expose legacy conflict detection, version info, and 1-click `UninstallLegacyITunesCommand`.
  - Provide a clear banner on the setup screen when conflicting legacy iTunes (12.6.5.3) is found.
  - Add button to run the downloaded installer directly.

- [x] **[T003]** Implement redesigned `FirmwareView.xaml`:
  - Top segmented mode tabs: `[ 📱 Мои устройства ]` and `[ ⚡ Одиночное скачивание ]`.
  - Persistent left sidebar: device list with status badges, "+ Модель", `⚡ Одиночное скачивание` shortcut, and interactive Auto-check toggle.
  - Mode 1: Monitored device firmware list.
  - Mode 2: "⚡ Одиночное скачивание" with Apple model dropdown + Direct IPSW URL + table with `[ Скачать разово ]`.
  - Shared bottom download dock with progress bar, download speed, remaining time, controls.
  - Detailed Settings Modal (threads, TSS check interval, Windows notifications, SHA-256 verification, cache cleanup).

- [x] **[T004]** Update `FirmwareViewModel.cs` and `FirmwareCatalogService.cs`:
  - Support single download mode without registering device to monitored list.
  - Support direct IPSW URL downloads.
  - Support settings persistence (threads, auto-check, SHA-256).

- [x] **[T005]** Update `MainWindow.xaml`:
  - Implement multi-segment circular indicator in `OpsCorner`:
    - 1 active task -> solid ring (orange for IPSW, blue for app install) with `1`.
    - 2+ active tasks -> divided circle (equal blue + orange arcs) with total count in center.

- [x] **[T006]** Clean Interface Design & Bug Review (v1.8.40):
  - Eliminate duplicate settings gear button in header of `FirmwareView.xaml`.
  - Add inline gear button to left sidebar TSS tile.
  - Add dedicated "Apple IPSW Firmware" card in global `SettingsView.xaml` and `SettingsViewModel.cs`.
  - Replace TV icons (`&#xE7F4;`) with iPhone/device glyphs (`&#xE8EA;`).
  - Add empty states for Mode 1 and Mode 2 tables so right pane is never a blank void when no device is selected.
  - Version bump to `1.8.40` in `src/IPAStudio.App/IPAStudio.App.csproj`.

- [x] **[T007]** Verification & Release:
  - Push commit to GitHub to trigger `auto-release.yml` for `v1.8.40`.

- [x] **[T008]** Apple HIG Design Polish, Tactile Motion & Bugfixes (v1.8.41):
  - Fix Back (`< Назад`) button by adding `GoBackCommand` in `FirmwareViewModel.cs`.
  - Add 56px clearance margin (`Margin="0,0,56,0"`) to header segmented control in `FirmwareView.xaml` to eliminate overlap with MainWindow settings gear.
  - Replace sharp square device glyphs (`&#xE8EA;`) with concentric circular badges and rounded Apple device silhouettes across empty states, tabs, and device list items.
  - Implement fluid tactile hover & press micro-interaction scale animations (`0.96` press compression, `1.02` hover) across all button styles in `Theme.xaml`, `MainWindow.xaml`, and `FirmwareView.xaml`.
  - Bump version to `1.8.41` in `IPAStudio.App.csproj`.

- [x] **[T009]** Fluid Micro-Animations, Pause/Resume Reliability & Layout Fixes (v1.8.42):
  - Integer rounding for ETA / remaining time in `FirmwareDownloadJob.cs` (`RemainingText`), completely eliminating layout jitter.
  - Fix single download badge (`⚡ Одиночное`) text clipping via `DockPanel` layout with `LastChildFill="True"`.
  - Fix download pause/resume bug in `FirmwareViewModel.cs` and `FirmwareDownloadService.cs`:
    - Preserve `DestinationPath` and `.download.json` manifest across pause and resume.
    - Set `FileShare.ReadWrite` to avoid file locks.
    - Handle socket abort exceptions (`OperationCanceledException`, `AggregateException`, `IsCancellationRequested`) to ensure clean `Paused` state instead of `Failed`.
    - Save manifest reliably on cancel using uncancelled token.
    - Dynamic pause/resume glyph: Play (`&#xE768;`) when paused, Pause (`&#xE769;`) when active, with dynamic tooltips.
    - Dynamic status indicator dot in shared bottom download dock: green `#30D158` when active, amber `Brush.Warning` when paused, red `Brush.Danger` when failed.
  - True fluid WPF Storyboards with `CubicEase EasingMode="EaseOut"` for hover, press, and release across buttons, sliders, segmented toggles, tabs, and checkboxes.
  - Bump version to `1.8.42` in `IPAStudio.App.csproj`.

- [x] **[T010]** Multi-Device Download Strips, Speed Smoothing, Purple Palette, Caching & Settings Redesign (v1.8.43):
  - Fix speed & ETA jitter: Throttle progress reporting to 300ms windows and calculate smoothed speed via Exponential Moving Average (EMA) in `FirmwareDownloadService.cs`.
  - Fix wrong firmware label bug: Decouple Mode 1 `DownloadFirmwareCommand` from Mode 2 `DownloadSingleFirmwareCommand` in `FirmwareViewModel.cs` and `FirmwareView.xaml`.
  - Instant device switching: Add persistent disk and in-memory cache in `FirmwareCatalogService.cs` (`%LOCALAPPDATA%\IPAStudio\firmware-cache`) so switching devices never hangs, fails or leaves empty firmwares.
  - Color palette shift from brown to Apple System Purple: Add `Brush.Firmware` (`#AF52DE` / `#BF5AF2`) and `Brush.FirmwareSoft` across `Palette.Light.xaml` and `Palette.Dark.xaml`. Update `OpsCorner` circular progress arcs and operations list in `MainWindow.xaml`.
  - Multi-device download queue: Replace single-dock layout in `FirmwareView.xaml` with multi-task queue strips matching `QueueView`, with per-item progress bars, speed, size, subtitle, and individual pause/resume/cancel controls.
  - Redesign settings sub-window: Native 540px sheet with crisp subpixel rendering, smooth entrance scale/fade Storyboard animation, larger 13-14px typography, and scheduled download time range (e.g. 00:00 - 06:00).
  - Animated TSS auto-check toggle: Fluid iOS thumb slide and background color transition on checked/unchecked state.
  - Bump version to `1.8.43` in `IPAStudio.App.csproj`.
