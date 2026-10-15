# Implementation Plan: 008-firmware-ux-progress-and-animations

**Feature**: Улучшения UX прошивок: общий прогресс, индивидуальный прогресс устройств, журнал событий, финализация загрузок и анимация операций  
**Target Version**: 1.8.53  

---

## 1. Architecture & Component Changes

### 1.1 Папка прошивок и настройки
- **`FirmwareViewModel.cs`**:
  - Обеспечить надежную инициализацию `DestinationFolder` с fallback на папку по умолчанию, если значение пустое или null.
  - Реализовать `partial void OnDestinationFolderChanged(string value)` с автоматическим сохранением `_settings.Current.FirmwareFolder` и вызовом `_settings.Save()`.
  - В `BrowseFolderCommand` гарантировать запись выбранного пути в настройки и вызов уведомлений `PropertyChanged`.
- **`FirmwareView.xaml`**:
  - Скорректировать стиль `TextBox` для папки: `MinHeight="34"`, `Padding="10,6"`, `VerticalContentAlignment="Center"`, `Text="{Binding DestinationFolder, UpdateSourceTrigger=PropertyChanged}"`.

### 1.2 Общий прогресс-бар и индивидуальный прогресс устройств
- **`FirmwareViewModel.cs`**:
  - `OverallProgress` уже рассчитывается в `RecomputeAggregate()`.
  - Добавить метод поиска текущего задания для устройства: `GetActiveJobForDevice(string identifier)`.
  - Добавить в элементы списка устройств (или обертку/хелпер) свойства: `HasActiveDownload`, `DownloadProgress`, `DownloadSpeedText`.
- **`FirmwareView.xaml`**:
  - В нижней панели очереди над списком заданий добавить прогресс-бар: `<ProgressBar Value="{Binding OverallProgress}" Height="4" Margin="0,8,0,10" Style="{StaticResource ProgressBar.Accent}" />`.
  - В карточках `MyDevices` ListBox добавить компактную полосу прогресса под заголовком/идентификатором модели, активную только во время загрузки.

### 1.3 Финализация загрузки без зависания на 100%
- **`FirmwareDownloadService.cs`**:
  - Добавить коллбэк/состояние фазы финализации: `Assembling` и `VerifyingHash`.
  - Передавать статус через `IProgress<FirmwareDownloadProgress>` или отдельное событие/текст статуса.
  - Оптимизировать сборку: если файл скачан в 1 сегмент, выполнять атомарный `File.Move`, избегая долгого копирования через стримы.
- **`FirmwareDownloadJob.cs` / `FirmwareViewModel.cs`**:
  - Обновлять `StatusText` во время сборки: «Сборка файла...» и «Проверка SHA-1...».

### 1.4 Адаптивные кнопки и исправление обрезки
- **`FirmwareView.xaml`**:
  - Блок «Пакетные действия» в сайдбаре: перевести кнопки «Проверить все» и «Скачать для всех» на вертикальный стек или адаптивную сетку с `HorizontalAlignment="Stretch"`, чтобы текст не обрезался даже при узком сайдбаре.
  - Верхняя панель управления: обернуть группы кнопок в гибкий `WrapPanel` с корректными отступами.

### 1.5 Журнал событий прошивок и отметка актуальности
- **`FirmwareLogService.cs` / `FirmwareViewModel.cs`**:
  - Создать коллекцию записей событий: `ObservableCollection<FirmwareLogEntry> ActivityLogs`.
  - Логировать события: добавление/удаление устройств, опрос подписей TSS с количеством подписанных версий, старт и окончание скачивания.
  - Отображать отметку времени: `LastTssCheckText` («Проверено: {0:dd.MM.yyyy HH:mm} • Подписи актуальны»).
- **`FirmwareView.xaml`**:
  - Добавить кнопку-иконку журнала (📋) в тулбаре с выпадающим окном/поповером истории событий.

### 1.6 Анимация раскрытия плашки операций
- **`MainWindow.xaml` / `MainWindow.xaml.cs`**:
  - Добавить `ScaleTransform` и `RenderTransformOrigin="1.0, 0.0"` для контейнера `OperationsPopup`.
  - Настроить плавную анимацию разворачивания из точки кнопки-кружка при открытии и сворачивания при закрытии.
