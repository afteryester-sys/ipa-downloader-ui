# Implementation Plan: Direct Install UX Refinements & Unicode Filename Support

## Architecture & Design Decisions

### 1. Filename Normalization (DownloadService.cs)
- Introduce `MakeValidFileName(string input)` in `DownloadService.cs`:
  - Retains all valid Unicode characters (including Cyrillic, Greek, CJK, etc.).
  - Strips/replaces filesystem-illegal characters (`\ / : * ? " < > |` and control chars `< 32`).
  - Trims invalid leading/trailing periods, spaces, underscores.
- Update `TryNormalizeIpaFileName`:
  - Call `MakeValidFileName` instead of `MakeAsciiSafeName`.
- Update `BuildDefaultFileName`:
  - Call `MakeValidFileName` so Russian default app names also stay in Russian.
- Staging note: `NativePathHelper.ResolveSafeNativeOutputPath` already handles 8.3/ASCII staging if `ipatool` needs it, and safely moves the completed IPA back to the Unicode destination path.

### 2. Action Row Layout & MaxWidth (DirectDownloadView.xaml)
- Increase scroll content `MaxWidth` from `720` to `960`.
- Replace `StackPanel Orientation="Horizontal"` in the Action Row with `<WrapPanel Orientation="Horizontal">` with `Margin="0,0,10,10"` on child buttons.
- This ensures all 4 buttons fit on wide screens without clipping, and wrap cleanly on narrow screens.

### 3. Device Selector State & Multi-Select ViewModel (DirectDownloadViewModel.cs)
- Add `SelectableDevice` class (or wrapper):
  - Wraps `Device Device`, implements `INotifyPropertyChanged` with `bool IsSelected`.
- In `DirectDownloadViewModel`:
  - Change device collection to `ObservableCollection<SelectableDevice> TargetDevices`.
  - Add `bool IsDevicePickerOpen` observable property (defaults to `false`).
  - `ToggleDevicePickerCommand`:
    - Toggles `IsDevicePickerOpen = !IsDevicePickerOpen`.
  - `CloseDevicePickerCommand`:
    - Closes `IsDevicePickerOpen = false`.
  - Update `InstallToDeviceCommand`:
    - Gathers all devices in `TargetDevices` where `IsSelected == true`.
    - If none selected, selects the first and installs, or prompts to select at least one device.
    - If multiple selected, runs `InstallAsync` for each selected device sequentially (or concurrently within safe limits), updating progress for each (`[1/2] Name: 45%`, etc.).
    - When install finishes successfully, closes `IsDevicePickerOpen`.

### 4. Interactive Animated Device Cards (DirectDownloadView.xaml)
- Place the device picker card directly under the action row, bound to `Visibility="{Binding IsDevicePickerOpen, Converter={StaticResource BoolToVis}}"`.
- Use `ListBox` with `SelectionMode="Multiple"` or card clicks toggling `IsSelected`.
- Card template:
  - `ScaleTransform x:Name="Pop"` for tactile micro-pop animation.
  - Storyboard trigger on `IsSelected = True` / `IsSelected = False`:
    - Animates `ScaleX` and `ScaleY` from 0.98 to 1.0.
    - Animates `Ring` border thickness to 2 and opacity to 1 with cubic ease.
    - Checkmark badge in corner or icon to make multi-selection obvious.
  - Header: «Выберите устройства для установки».
  - Buttons inside the panel:
    - [Установить на выбранные (N)]
    - [Отмена]

### 5. Localization (Strings.ru.xaml & Strings.en.xaml)
- Add any new keys:
  - `L.Direct.InstallSelected` («Установить на выбранные» / «Install on selected»)
  - `L.Direct.SelectDevicesHint` («Выберите одно или несколько устройств для установки» / «Select one or more devices to install onto»)
  - `L.Direct.NoDevicesSelected` («Выберите хотя бы одно устройство» / «Please select at least one device»)
  - `L.Direct.Close` («Закрыть» / «Close»)
- Validate parity across RU and EN.
