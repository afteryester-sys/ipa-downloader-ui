# Tasks: 007-firmware-batch-all-and-selection-animations

**Input**: Implementation plan from `specs/007-firmware-batch-all-and-selection-animations/plan.md`  
**Spec**: [spec.md](./spec.md)

---

## Phase 1: ViewModel Extensions

- [x] [T001] Добавить в `FirmwareViewModel.cs` свойства `CheckAllButtonText`, `DownloadAllButtonText` и реактивное обновление при изменении `MyDevices`.
- [x] [T002] Обеспечить блокировку кнопок и индикацию состояния `IsChecking` во время работы `CheckAllDevicesCommand` и `DownloadAllMonitoredSignedCommand`.

---

## Phase 2: View & Animation Enhancements

- [x] [T003] В `FirmwareView.xaml` создать стиль `DeviceCardSelectable` с плавной анимацией появления рамки выделения (`Opacity 0 -> 1`, `Scale 0.96 -> 1.0` с `CubicEase Out`) и затухания при снятии выделения.
- [x] [T004] Добавить тактильный отклик на нажатие карточки устройства (`IsPressed` scale `0.97 -> 1.0`).
- [x] [T005] В `FirmwareView.xaml.cs` добавить плавный переход (cross-fade / slide) при смене выбранного устройства.
- [x] [T006] В правом тулбаре управления добавить кнопки «Проверить все ({N})» и «Скачать для всех ({N})».
- [x] [T007] В сайдбаре устройств «Мои устройства» добавить футер с быстрыми кнопками пакетных действий («Проверить все» и «Скачать для всех»).

---

## Phase 3: Localization & Release Notes

- [x] [T008] Добавить новые ключи локализации в `Strings.ru.xaml` и `Strings.en.xaml`.
- [x] [T009] Проверить паритет локализации скриптом `python scratch/check_loc.py`.
- [x] [T010] Добавить описание версии `1.8.50` в `ReleaseNotesService.cs`.

---

## Phase 4: Build, Verification & Release Delivery

- [x] [T011] Выполнить сборку `dotnet build` и убедиться в отсутствии ошибок.
- [x] [T012] Поднять версию до `1.8.50` в `src/IPAStudio.App/IPAStudio.App.csproj`.
- [x] [T013] Опубликовать релиз `v1.8.50` в GitHub репозитории и проконтролировать завершение рабочего процесса сборки.
