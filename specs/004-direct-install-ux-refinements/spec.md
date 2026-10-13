# Spec 004: Direct Install UX Refinements & Unicode Filename Support

## 1. Problem Statement
1. **Action Row Button Clipping**: In `DirectDownloadView.xaml`, `MaxWidth="720"` and a horizontal `StackPanel` causes the 4th button («Открыть папку») to clip on the right («Открыть паг...»), even in fullscreen mode.
2. **Device Selection Visibility**: The «Устройство для установки» card is currently shown unconditionally as a permanent section on the page. It should only appear upon pressing «Установить на устройство».
3. **Multi-Device Selection & Interaction Animation**: Users need the ability to select one or multiple connected devices to install the downloaded IPA onto. Selecting/clicking device cards must feature the native tactile selection animation (`ScaleTransform` pop + `Ring` accent glow) used in `OnDeviceView.xaml`.
4. **Russian / Unicode Filename Transliteration**: Renaming an IPA file with Cyrillic characters (e.g., «Сберкидс») forces transliteration to English (`Sberkids`) because `DownloadService.TryNormalizeIpaFileName` calls `MakeAsciiSafeName`. It must preserve Russian/Unicode characters.

## 2. Requirements & User Scenarios

### Scenario 1: Responsive Action Row & No Button Clipping
- Given the direct download view is displayed (in normal or fullscreen mode),
- When buttons are shown (Download, iTunes, Install to device, Open folder),
- Then all buttons must fit comfortably without text truncation. On narrow screens, buttons wrap gracefully to a second row using `WrapPanel`.

### Scenario 2: On-Demand Device Selection Panel
- Given a downloaded IPA is ready in `DirectDownloadView`,
- When the user clicks «Установить на устройство»,
- Then the device selection panel toggles open (or closes if already open).
- The panel presents connected devices, allowing selection of one or multiple devices, with a confirm button «Установить на выбранные» and a close/cancel button.

### Scenario 3: Multi-Device Selection with Tactile Animation
- Given multiple devices are connected,
- When the user clicks a device card in the device selector,
- Then the card plays the signature click/pop animation (`ScaleTransform` keyframes from 0.98 to 1.0) and accent ring border animation (`BorderThickness="2"`, `Opacity="1"`).
- The user can select multiple devices (checkbox/check badge reflects state).
- Clicking «Установить» proceeds with installation to each selected device.

### Scenario 4: Preserving Cyrillic Filenames
- Given the rename panel is opened under a found app,
- When the user types «Сберкидс» and clicks Save,
- Then the output filename is stored as `Сберкидс`, not transliterated to `Sberkids`.
- When downloaded, the output file is saved as `Сберкидс.ipa`.

## 3. Success Criteria
- Action row buttons never clip or truncate text in fullscreen or normal mode.
- Device selection panel is hidden until «Установить на устройство» is clicked.
- Single and multi-device selection works with animated cards.
- Russian app names like «Сберкидс» are saved with Cyrillic filenames.
- Build compiles with 0 errors, tests and localization pass.
