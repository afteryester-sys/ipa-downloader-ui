# Implementation Plan: Production Firmware IPSW Center & Intelligent Driver Setup Recovery

**Feature Directory**: `specs/002-firmware-and-setup`  
**Specification**: [spec.md](spec.md)  
**Target Version**: `1.8.38`  
**Status**: Approved for Implementation  

---

## 1. Technical Context & Affected Components

### Component 1: `IPAStudio.Core/Services/DependencyService.cs`
- **Current State**:
  - `CheckITunesInstalled()` detects existence via registry and folders, but does NOT distinguish between legacy iTunes (12.6.5.3) and modern iTunes (12.13+), nor does it check if the driver service (`Apple Mobile Device Service`) actually matches.
  - `InstallITunesAsync` tries `winget` (which fails on existing version conflicts), then downloads `iTunes64Setup.exe` and runs it with `/quiet /norestart`.
  - `/quiet` silently fails when conflicting MSI components exist. Then `File.Delete(setupPath)` deletes the installer, leaving the user stranded.
- **Planned Enhancements**:
  - Implement `GetInstalledITunesVersion()`: inspects registry `Uninstall` hives for DisplayName and DisplayVersion.
  - Implement `IsLegacyITunesConflict(out string version, out string uninstallString)`: flags versions <= 12.6.5.3 or mismatched AMDS.
  - Implement `UninstallLegacyITunesAsync()`: invokes the recorded `MsiExec.exe /x {GUID} /passive` or launches clean uninstall.
  - Implement interactive fallback in `InstallITunesAsync()`: if silent execution returns non-zero, immediately launch `iTunes64Setup.exe` with standard wizard UI (`UseShellExecute = true, Verb = "runas"`) so Windows Installer prompts for upgrade/repair.
  - Preserve installer in user's Downloads or cache directory so it can be re-run manually.

### Component 2: `IPAStudio.App/ViewModels/SetupViewModel.cs` & `SetupView.xaml`
- **Planned Enhancements**:
  - Add properties `HasLegacyITunesConflict`, `LegacyITunesVersionMessage`.
  - Add command `UninstallLegacyITunesCommand` to trigger clean removal in 1 click.
  - Add command `OpenDownloadedInstallerCommand` to let the user re-run the downloaded setup file without redownloading.
  - Update XAML with warning banner and action buttons when conflict is detected.

### Component 3: `IPAStudio.App/Views/FirmwareView.xaml` & `FirmwareViewModel.cs`
- **Planned Enhancements**:
  - Implement complete UI from `Firmware_Design_Preview.html`:
    - Top segmented mode tabs: `[ 📱 Мои устройства ]` and `[ ⚡ Одиночное скачивание ]`.
    - Left sidebar: monitored device list with status badges, "+ Модель", `⚡ Одиночное скачивание` direct button, and interactive Auto-check toggle switch.
    - Right area - Mode 1: Table of firmware for selected device.
    - Right area - Mode 2: Apple model dropdown (all models) + Direct IPSW URL input + table of official versions with `[ Скачать разово ]`.
    - Shared bottom download dock: job title, badge `⚡ Одиночное` or `📱 Мониторинг`, progress bar, speed, pause/resume, cancel, open folder.
    - Detailed Settings Modal (gear icon): tabs for Network & Threads (2/4/8/16), Auto-check intervals, and SHA-256 verification.

### Component 4: `IPAStudio.App/MainWindow.xaml`
- **Planned Enhancements**:
  - Dynamic `OpsCorner` indicator:
    - When 1 task: single stroke ring (Orange for firmware, Blue for app install) with `1` in center.
    - When 2+ tasks: multi-segment equal halves (180° Blue, 180° Orange) with total count in center.

### Component 5: Project Version & GitHub Actions CI
- **Planned Enhancements**:
  - Increment version to `1.8.38` in `src/IPAStudio.App/IPAStudio.App.csproj`.
  - Push commit and tag `v1.8.38` to GitHub repository `afteryester-sys/ipa-downloader-ui`.
  - GitHub Actions `auto-release.yml` will automatically build the Windows binary, publish the GitHub Release, and the app will detect the update.

---

## 2. Implementation Phasing

- **Phase 1**: Core Driver/iTunes Setup Recovery (`DependencyService.cs`, `SetupViewModel.cs`, `SetupView.xaml`).
- **Phase 2**: Firmware UI & Single Download Mode (`FirmwareView.xaml`, `FirmwareViewModel.cs`).
- **Phase 3**: Multi-Segment Circle Indicator in `MainWindow.xaml`.
- **Phase 4**: Verification, Unit Tests & Version Bump (`v1.8.38`).
- **Phase 5**: Commit, Tag & Push to trigger Auto-Release CI.
