# Bug Assessment: Сбой XamlParseException при подключении устройства в меню «Скачать IPA»

- **Slug**: selectable-device-twoway-binding
- **Created**: 2026-10-14T19:30:00+07:00
- **Source**: pasted text & screenshot
- **Verdict**: valid
- **Severity**: critical

## Report (verbatim or summarized)

> "вот что происходит при покдлючении любого айфона и если я захожу в меню 'скачать ipa' исправь пожалуйста ."

Скриншот пользователя отображает каскадный шторм модальных окон ошибок:
> `XamlParseException: Привязка типа TwoWay или OneWayToSource не может работать с доступным только для чтения свойством 'Model' типа 'IPAStudio.App.ViewModels.SelectableDevice'.`

## Symptom

1. **Ожидаемое поведение**:
   - При подключении любого iPhone/iPad и переходе на вкладку «Скачать IPA» (`DirectDownloadView`) в панели «Установка на устройство» отображается список подключенных устройств для установки с чекбоксами выбора, названием модели и версией iOS без каких-либо ошибок.
2. **Фактическое поведение**:
   - При подключении любого устройства и открытии вкладки «Скачать IPA» WPF парсер разметки пытается привязать свойство `Run.Text` к `SelectableDevice.Model` в двустороннем режиме (`TwoWay`).
   - Поскольку свойство `SelectableDevice.Model` объявлено только с геттером (`public string Model => Device.Model;`) и не имеет сеттера, WPF выбрасывает исключение `XamlParseException` / `InvalidOperationException`.
   - В обработчике `DispatcherUnhandledException` вызывается `ShowError(ex)` без дебаунса/блокировки повторного вызова, что приводит к лавинообразному открытию десятков модальных окон `MessageBox` поверх окна приложения.

## Reproduction

1. Запустить приложение IPA Studio.
2. Подключить любой iPhone кабелем к компьютеру.
3. Перейти на вкладку «Скачать IPA» (`DirectDownloadView`).
4. Нажать или открыть секцию со списком устройств для установки (`TargetDevices`).
5. **Результат**: Приложение выбрасывает `XamlParseException: Привязка типа TwoWay или OneWayToSource не может работать с доступным только для чтения свойством 'Model' типа 'IPAStudio.App.ViewModels.SelectableDevice'`, и на экране появляется каскад ошибок.

## Suspected Code Paths

1. `src/IPAStudio.App/Views/DirectDownloadView.xaml:342-346`:
   ```xaml
   <TextBlock Style="{StaticResource Text.Caption}" FontSize="11" Margin="0,2,0,0">
       <Run Text="{Binding Model}" />
       <Run Text=" · iOS " />
       <Run Text="{Binding OsVersion}" />
   </TextBlock>
   ```
   В WPF свойство `Run.TextProperty` зарегистрировано с метаданными `FrameworkPropertyMetadataOptions.BindsTwoWayByDefault`. Из-за этого при отсутствии явного указания `Mode` WPF принудительно пытается создать `TwoWay` привязку.
2. `src/IPAStudio.App/ViewModels/DirectDownloadViewModel.cs:30-49`:
   Класс `SelectableDevice`:
   ```csharp
   public string Name => Device.Name;
   public string Model => Device.Model;
   public string OsVersion => Device.OsVersion;
   public string Udid => Device.Udid;
   public DeviceLink Link => Device.Link;
   public string DeviceClass => Device.DeviceClass;
   ```
   Свойства `Name`, `Model`, `OsVersion` являются get-only. Если привязка по ошибке пытается выполнить запись, возникает фатальное исключение.
3. `src/IPAStudio.App/App.xaml.cs:283-296`:
   Метод `ShowError`:
   Не имеет флага `_isShowingError`, из-за чего при циклическом рендеринге коллекции или тиках таймера/событий подключения устройств диалоговые окна ошибок лавинообразно накладываются друг на друга.

## Root Cause Analysis

**Уровень уверенности: 100% (Абсолютный)**.
В WPF элемент `Run` (внутри `TextBlock`) по умолчанию имеет двунаправленный режим привязки (`BindsTwoWayByDefault`). Привязка `{Binding Model}` без параметра `Mode=OneWay` пытается привязаться двусторонне к доступному только для чтения свойству `SelectableDevice.Model`. WPF считает это критической ошибкой разметки и генерирует `XamlParseException`. Точно такая же проблема потенциально затрагивает и `{Binding OsVersion}`.

## Proposed Remediation

1. **В `src/IPAStudio.App/Views/DirectDownloadView.xaml`**:
   Явно указать `Mode=OneWay` для всех привязок внутри `Run.Text`:
   ```xaml
   <TextBlock Style="{StaticResource Text.Caption}" FontSize="11" Margin="0,2,0,0">
       <Run Text="{Binding Model, Mode=OneWay}" />
       <Run Text=" · iOS " />
       <Run Text="{Binding OsVersion, Mode=OneWay}" />
   </TextBlock>
   ```
2. **В `src/IPAStudio.App/ViewModels/DirectDownloadViewModel.cs`**:
   В классе `SelectableDevice` защитно предоставить пустые сеттеры `{ get => ...; set {} }` для оберток, чтобы любая непредвиденная двухсторонняя привязка не ломала приложение.
3. **В `src/IPAStudio.App/App.xaml.cs`**:
   Добавить защиту от каскадного открытия окон ошибок (`_isShowingError`), чтобы в случае единичного сбоя на экране появлялось только одно информативное окно, а не каскад из 10+ окон.
4. **Аудит других XAML файлов**:
   Проверить и добавить `Mode=OneWay` во всех остальных `Run Text="{Binding ...}"` (например, в `SupportAdminWindow.xaml`).
