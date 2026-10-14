# Implementation Plan: 006-firmware-batch-and-animations

**Branch**: `006-firmware-batch-and-animations` | **Date**: 2026-10-14 | **Spec**: [spec.md](./spec.md)

## Summary

Реализация двух ключевых улучшений в модуле прошивок (`FirmwareView` & `FirmwareViewModel`):
1. **Плавные WPF-анимации добавления и удаления моделей** в списке «Мои устройства» (входной Fade/Scale и выходной Fade/Slide/Collapse с гарантией удаления именно целевого элемента).
2. **Панель пакетного управления прошивками**: явные кнопки **«Проверить прошивки»** (ручной запуск опроса актуальности TSS и манифестов с индикацией) и **«Скачать все подписанные» / «Скачать выбранные»** (пакетный запуск скачивания с поддержкой множественного выбора через чекбоксы).

---

## Technical Context

**Language/Version**: C# 12 / .NET 8.0 Windows (`net8.0-windows`)  
**UI Framework**: WPF, XAML, CommunityToolkit.Mvvm  
**Design System**: SaglitzDesign / Apple macOS Tahoe Glass, `Brush.Surface`, `Brush.Accent`, `Brush.Firmware`, `Radius.Card`, Segoe MDL2 Assets  
**Threading & Async**: Non-blocking asynchronous I/O, `CancellationTokenSource`, `Progress<T>`, WPF Dispatcher  
**Localization**: Strict parity in `Strings.ru.xaml` and `Strings.en.xaml` verified via `check_loc.py`  
**Target Release**: IPA Studio `v1.8.49`

---

## Constitution Check

- [x] **Safety & Non-Breaking Invariant**: Существующие механизмы скачивания, HTTP Range, докачка, сохранение настроек не ломаются.
- [x] **Clean WPF & MVVM Architecture**: Вся бизнес-логика и состояние пакетного скачивания находятся в `FirmwareViewModel`. View управляет исключительно презентацией и Storyboard-анимациями.
- [x] **User Feedback & Honest Progress**: Во время проверки отображается реальное состояние (вращающийся индикатор), при пакетном скачивании — точный подсчет добавленных заданий и сводный прогресс.
- [x] **Localization**: 100% новых текстовых строк локализованы на русский и английский языки.

---

## Architecture & Design Details

### 1. Model Items & Animations in `MyDevices`

- **Item Container Appearance Animation (Add)**:
  В `ListBoxItem` стиле или `DataTemplate` списка `MyDevices` настраивается `Loaded` EventTrigger со Storyboard:
  - `Opacity`: 0.0 $\to$ 1.0 (Duration: 0:0:0.22, `CubicEase Out`)
  - `RenderTransform.TranslateTransform.Y`: 10 $\to$ 0 (Duration: 0:0:0.22, `CubicEase Out`)
  - `RenderTransform.ScaleTransform.ScaleX` / `ScaleY`: 0.94 $\to$ 1.0 (Duration: 0:0:0.22, `CubicEase Out`)
- **Item Container Exit Animation (Remove)**:
  В `FirmwareView.xaml.cs` обработчик клика кнопки удаления (`RemoveDeviceButton_Click`):
  1. Извлекает `FirmwareDevice` из `DataContext` строки.
  2. Находит контейнер строки (`ListBoxItem` или `Border`/`Grid`).
  3. Запускает Storyboard выхода:
     - `Opacity`: 1.0 $\to$ 0.0 (Duration: 0:0:0.18, `CubicEase In`)
     - `RenderTransform.TranslateTransform.X`: 0 $\to$ -24 (Duration: 0:0:0.18, `CubicEase In`)
     - `LayoutTransform.ScaleTransform.ScaleY`: 1.0 $\to$ 0.0 (Duration: 0:0:0.20, `CubicEase In`) — обеспечивает плавное схлопывание пространства и подтягивание нижних элементов вверх без резких прыжков.
  4. По событию `Completed` вызывает метод `vm.RemoveDevice(device)`.
  5. В `FirmwareViewModel.cs` обновляется сигнатура команды: `RemoveDevice(FirmwareDevice? device = null)`, чтобы всегда удалялся переданный элемент (исправление бага удаления только выделенного устройства).

### 2. Manual TSS & Firmware Verification

- В `FirmwareViewModel.cs`:
  - `[ObservableProperty] private bool _isChecking;`
  - `[RelayCommand] private async Task CheckFirmwaresAsync()`:
    - Проверяет `SelectedDevice` (или все `MyDevices` при пакетном запросе).
    - Опрашивает `_catalog.GetDeviceAsync(id, ct)`.
    - Обновляет `Firmwares`, `SingleFirmwares`, `LastCheckUtc`, `LastCheckText`.
    - Устанавливает статус: `Loc.Get("L.Firmware.CheckCompleted")`.
  - `[RelayCommand] private async Task CheckAllDevicesAsync()`:
    - Проходит по всем устройствам в `MyDevices`, запрашивает актуальные данные.
    - Обновляет бейджи и выводит сводку: «Проверено N устройств».

### 3. Batch Multiple Download

- **Выбор прошивок (`FirmwareReleaseItem` или wrapper model)**:
  - Создается модель-обертка или расширение с `[ObservableProperty] private bool _isSelected;` для каждой прошивки в таблице.
  - В шапке таблицы: чекбокс «Выбрать все подписанные» / «Снять выбор».
  - В строках таблицы: компактный чекбокс выбора.
- **Кнопка пакетного запуска**:
  - Располагается в панели управления таблицы:
    - Если выбрано $N > 0$ прошивок: **«Скачать выбранные ({N})»** (кнопка `DownloadSelectedCommand`).
    - Если ничего не выбрано: **«Скачать все подписанные»** (кнопка `DownloadAllSignedCommand`).
  - При клике:
    - Находит все соответствующие релизы.
    - Для каждой прошивки проверяет, не скачан ли уже файл на диск и нет ли ее уже в `Jobs`.
    - Добавляет недостающие прошивки в очередь `Jobs` и немедленно стартует скачивание через `RunJobAsync`.
    - Выводит сводный статус: «Добавлено в очередь: N прошивок».

### 4. Localization Dictionary Keys

Новые ключи в `Strings.ru.xaml` и `Strings.en.xaml`:
- `L.Firmware.CheckFirmwares`: "Проверить прошивки" / "Check Firmwares"
- `L.Firmware.CheckAll`: "Проверить все" / "Check All"
- `L.Firmware.Checking`: "Проверка..." / "Checking..."
- `L.Firmware.CheckCompleted`: "Проверка TSS завершена" / "TSS check completed"
- `L.Firmware.DownloadSelected`: "Скачать выбранные ({0})" / "Download Selected ({0})"
- `L.Firmware.DownloadAllSigned`: "Скачать все подписанные" / "Download All Signed"
- `L.Firmware.SelectAllSigned`: "Выбрать все подписанные" / "Select All Signed"
- `L.Firmware.BatchAdded`: "Добавлено в очередь: {0} прошивок" / "Added to queue: {0} firmwares"
- `L.Firmware.AllAlreadyDownloaded`: "Все выбранные прошивки уже скачаны" / "All selected firmwares are already downloaded"

---

## Verification Plan

1. **Compilation**: `dotnet build -c Debug` & `dotnet build -c Release`.
2. **Automated Tests**: `dotnet test`.
3. **Localization Parity**: `python scratch/check_loc.py` (0 errors).
4. **Interactive Validation**:
   - Добавление моделей: карточки плавно «выезжают» и проявляются.
   - Удаление моделей: карточка плавно затухает, смещается и схлопывается.
   - Кнопка «Проверить прошивки»: крутится индикатор, обновляется время последней проверки.
   - Кнопка «Скачать выбранные» / «Скачать все подписанные»: корректно добавляет пакет заданий в нижнюю панель очереди и начинает загрузку.
