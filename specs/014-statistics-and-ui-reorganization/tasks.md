# Tasks: Statistics Dashboard, Tools Reorganization & Operations Popup Bounds

- [x] [T001] Create `StatisticsModels.cs` and `StatisticsService.cs` in `IPAStudio.Core` with persistence and period aggregations.
- [x] [T002] Create `StatisticsViewModel.cs` and `StatisticsView.xaml` with KPI cards, bar charts, top apps, devices, and method breakdowns.
- [x] [T003] Integrate statistics recording into `QueueViewModel.cs`, `DirectDownloadViewModel.cs`, `FirmwareViewModel.cs`, and `DeviceService.cs`.
- [x] [T004] Design and place the Tools toolbar with custom icons and animations on `DevicesView.xaml`, and wire commands in `DevicesViewModel.cs`.
- [x] [T005] Clean up the Settings popup in `MainWindow.xaml` to retain only configuration and maintenance items.
- [x] [T006] Correct `OperationsPopup` horizontal positioning in `MainWindow.xaml` so it stays inside window boundaries.
- [x] [T007] Update completion notifications and cards to display exact processed counts (e.g. "Установка 10 приложений успешно завершена").
- [x] [T008] Add bilingual localization strings in `Strings.ru.xaml` and `Strings.en.xaml` and verify via `Check-Localization.ps1`.
- [x] [T009] Verify zero warnings and zero errors via `dotnet build -c Release`.
