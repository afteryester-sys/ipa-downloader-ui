# Implementation Plan: 007-firmware-batch-all-and-selection-animations

## Architecture & Technical Design

### 1. ViewModel Enhancements (`FirmwareViewModel.cs`)
- **Properties**:
  - `CheckAllButtonText`: dynamic text e.g. "Проверить все ({MyDevices.Count})" / "Check All ({MyDevices.Count})".
  - `DownloadAllButtonText`: dynamic text e.g. "Скачать для всех ({MyDevices.Count})" / "Download for All ({MyDevices.Count})".
  - `HasMultipleDevices`: boolean flag `MyDevices.Count > 1` (or `> 0`) to control visibility or enabled state.
- **Commands**:
  - `CheckAllDevicesCommand`: Already exists, enhance with status messaging and reactive loading state `IsChecking`.
  - `DownloadAllMonitoredSignedCommand`: Already exists, ensure it properly activates `IsChecking`/`StatusText` and updates notifications.

### 2. View & XAML Enhancements (`FirmwareView.xaml`, `FirmwareView.xaml.cs`)
- **ListBoxItem Selection Animation (`DeviceCardSelectable`)**:
  - `Border x:Name="Ring"`:
    - RenderTransform with `ScaleTransform` centered at `0.5, 0.5`.
    - `IsSelected = True`: Animate `Opacity` from 0 to 1 (`Duration: 0:0:0.20`, `CubicEase Out`). Animate `ScaleX/ScaleY` from `0.96` to `1.0`.
    - `IsSelected = False`: Animate `Opacity` from 1 to 0 (`Duration: 0:0:0.16`, `CubicEase InOut`). Animate `ScaleX/ScaleY` to `0.96`.
  - `Border x:Name="Fill"`:
    - `IsSelected = True`: Animate `Opacity` from 0 to 1 (`Duration: 0:0:0.20`, `CubicEase Out`).
    - `IsSelected = False`: Animate `Opacity` from 1 to 0 (`Duration: 0:0:0.16`).
  - Tactile Click:
    - `IsPressed = True`: Animate root container `ScaleX/ScaleY` to `0.97` (`Duration: 0:0:0.07`).
    - `IsPressed = False`: Animate root container `ScaleX/ScaleY` to `1.0` (`Duration: 0:0:0.14`).
- **Right Panel Content Transition**:
  - Add an entrance storyboard or trigger in `FirmwareView.xaml.cs` on `ListBox.SelectionChanged` that runs a subtle cross-fade on the firmwares table container (`Opacity 0.4 -> 1.0`, `TranslateY 6 -> 0`).
- **Main Controls Toolbar**:
  - Update `DockPanel Grid.Row="0"` in the right column:
    - Wrap action buttons in a responsive container with:
      - `[ ↻ Проверить ]` (`CheckFirmwaresCommand`)
      - `[ ↻ Проверить все ({N}) ]` (`CheckAllDevicesCommand`)
      - `[ ⤓ Скачать выбранные / подписанные ]` (`DownloadSelectedCommand`)
      - `[ ⤓ Скачать для всех ({N}) ]` (`DownloadAllMonitoredSignedCommand`)
      - `[ ✓ Только подписываемые ]`
- **Devices Sidebar Footer**:
  - In `Border Grid.Row="1"` or between Grid.Row="1" and Grid.Row="2":
    - Add a sleek batch action bar for "Мои устройства" with:
      - Кнопка «Проверить все»
      - Кнопка «Скачать для всех»

### 3. Localization Parity
- Add new localization keys to `Strings.ru.xaml` and `Strings.en.xaml`.
- Verify using `python scratch/check_loc.py`.

### 4. Release Notes & Version Bump
- Add release notes for `1.8.50` in `ReleaseNotesService.cs`.
- Bump version to `1.8.50` in `IPAStudio.App.csproj`.
