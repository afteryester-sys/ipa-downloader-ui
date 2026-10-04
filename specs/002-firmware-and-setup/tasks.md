# Tasks: Production Firmware IPSW Center & Intelligent Driver Setup Recovery

**Feature**: [spec.md](spec.md) | [plan.md](plan.md)  
**Target Version**: `1.8.38`  

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

- [x] **[T006]** Version bump to `1.8.38` in `src/IPAStudio.App/IPAStudio.App.csproj`.

- [ ] **[T007]** Verification & Release:
  - Run regression tests.
  - Push commit and tag `v1.8.38` to GitHub to trigger `auto-release.yml`.
