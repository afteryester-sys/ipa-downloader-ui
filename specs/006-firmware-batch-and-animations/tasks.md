# Tasks: 006-firmware-batch-and-animations

**Input**: Implementation plan from `specs/006-firmware-batch-and-animations/plan.md`  
**Spec**: [spec.md](./spec.md)

---

## Phase 1: ViewModel & Data Model Extensions

- [x] [T001] Создать модель-элемент `FirmwareReleaseItem` (или поддержку `IsSelectedForBatch`) с уведомлениями `CommunityToolkit.Mvvm` для выбора строк в таблице прошивок.
- [x] [T002] В `FirmwareViewModel.cs` добавить команду `RemoveDevice(FirmwareDevice? device = null)` с поддержкой удаления конкретного переданного устройства.
- [x] [T003] В `FirmwareViewModel.cs` добавить состояние `IsChecking` и команды ручной проверки `CheckFirmwaresCommand` и `CheckAllDevicesCommand`.
- [x] [T004] В `FirmwareViewModel.cs` добавить команды пакетного скачивания `DownloadSelectedCommand` и `DownloadAllSignedCommand`, а также команду выбора всех подписанных версий `ToggleSelectAllSignedCommand`.

---

## Phase 2: View & Animation Implementation

- [x] [T005] В `FirmwareView.xaml` настроить плавную анимацию появления элементов списка «Мои устройства» (Fade In 0 -> 1 + Scale 0.94 -> 1.0 + Translate Y 10 -> 0 с `CubicEase Out`).
- [x] [T006] В `FirmwareView.xaml` и `FirmwareView.xaml.cs` реализовать обработчик удаления карточки с плавной анимацией выхода (Fade Out 1 -> 0 + Slide Left + LayoutTransform ScaleY 1 -> 0) перед удалением устройства.
- [x] [T007] В `FirmwareView.xaml` добавить в панель управления кнопку «Проверить прошивки» (с индикатором загрузки `IsChecking`) и кнопку «Проверить все» в сайдбаре устройств.
- [x] [T008] В `FirmwareView.xaml` добавить колонку чекбоксов для мультивыбора версий прошивок и кнопку пакетного запуска: «Скачать выбранные ({N})» / «Скачать все подписанные».

---

## Phase 3: Localization & Release Notes

- [x] [T009] Добавить все новые строки локализации в `Strings.ru.xaml` и `Strings.en.xaml`.
- [x] [T010] Запустить проверку скриптом `python scratch/check_loc.py` и устранить любые расхождения.
- [x] [T011] Добавить запись о нововведениях версии `1.8.49` в `ReleaseNotesService.cs`.

---

## Phase 4: Build, Test & Release Delivery

- [x] [T012] Выполнить сборку `dotnet build` и запустить `dotnet test`.
- [x] [T013] Поднять версию до `1.8.49` в `src/IPAStudio.App/IPAStudio.App.csproj`.
- [x] [T014] Опубликовать релиз `v1.8.49` через GitHub API и убедиться в успешном завершении workflow в GitHub Actions.
