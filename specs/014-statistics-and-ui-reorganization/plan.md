# Implementation Plan: Statistics Dashboard, Tools Reorganization & Operations Popup Bounds

## 1. Architecture & Services
1. `IPAStudio.Core`:
   - Create `src/IPAStudio.Core/Models/StatisticsModels.cs` for tracking entities (`AppInstallRecord`, `DownloadRecord`, `DeviceConnectionRecord`, `StatisticsSummary`, `TopItemStat`, `TimelinePoint`).
   - Create `src/IPAStudio.Core/Services/StatisticsService.cs` with JSON persistence, recording methods, and period aggregation algorithms (Today, 7 Days, Month, Year, All Time).
2. Wire recording hooks:
   - `QueueViewModel.cs`: Record successful app installs and transfers with device and method metadata.
   - `DirectDownloadViewModel.cs`: Record IPA downloads.
   - `FirmwareViewModel.cs`: Record firmware downloads.
   - `DeviceService.cs`: Record device connections.

## 2. Presentation & Views
1. `StatisticsViewModel.cs` & `StatisticsView.xaml`:
   - Full-featured analytics screen with period selector, KPI summary cards, visual bar charts, method breakdown, top apps, popular devices, and recent events log.
2. Tools Toolbar on `DevicesView.xaml`:
   - Add modern tools bar with icons: Download IPA, Firmware, iCloud, Media Export, Statistics.
   - Connect commands on `DevicesViewModel.cs` to navigate via `INavigator`.
   - Remove duplicate Tools section from Settings popup in `MainWindow.xaml`.
3. Operations Popup Fix in `MainWindow.xaml`:
   - Adjust `HorizontalOffset` from `-146` to `-268` and set `RenderTransformOrigin="0.9, 0.0"`.
4. Notifications:
   - Format batch completion text in `QueueViewModel` and `ShellViewModel` with exact item counts: "Установка {N} приложений успешно завершена".
