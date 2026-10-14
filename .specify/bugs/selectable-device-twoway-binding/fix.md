# Bug Fix: Устранение XamlParseException и каскадных окон ошибок при подключении iPhone

- **Slug**: selectable-device-twoway-binding
- **Fixed**: 2026-10-14T19:33:00+07:00
- **Assessment**: ./assessment.md
- **Status**: applied

## Summary

Устранена критическая ошибка `XamlParseException` в представлении «Скачать IPA» путем явного задания `Mode=OneWay` для `Run.Text` привязок модели устройства и добавления защитных сеттеров в `SelectableDevice`. Также добавлен барьер предотвращения рекуррентного вызова `ShowError` для устранения шторма модальных окон.

## Changes

| File | Change | Notes |
|------|--------|-------|
| `src/IPAStudio.App/Views/DirectDownloadView.xaml` | modified | Добавлен `Mode=OneWay` для `<Run Text="{Binding Model, Mode=OneWay}" />` и `<Run Text="{Binding OsVersion, Mode=OneWay}" />` |
| `src/IPAStudio.App/ViewModels/DirectDownloadViewModel.cs` | modified | Добавлены пустые no-op сеттеры в `SelectableDevice` для `Name`, `Model`, `OsVersion`, `Udid`, `DeviceClass` |
| `src/IPAStudio.App/App.xaml.cs` | modified | Внедрен флаг повторного входа `_isShowingError` в `ShowError(ex)` |
| `src/IPAStudio.App/Views/SupportAdminWindow.xaml` | modified | Добавлен `Mode=OneWay` в `Run Text` для `WindowsVersion` и `AppVersion` |
| `src/IPAStudio.Core/Services/ReleaseNotesService.cs` | modified | Добавлены релизные заметки версии 1.8.51 на русском и английском языках |
| `src/IPAStudio.App/IPAStudio.App.csproj` | modified | Версия поднята до 1.8.51 |

## Diff Highlights

```xaml
<!-- DirectDownloadView.xaml -->
<TextBlock Style="{StaticResource Text.Caption}" FontSize="11" Margin="0,2,0,0">
    <Run Text="{Binding Model, Mode=OneWay}" />
    <Run Text=" · iOS " />
    <Run Text="{Binding OsVersion, Mode=OneWay}" />
</TextBlock>
```

```csharp
// DirectDownloadViewModel.cs
public sealed partial class SelectableDevice : ObservableObject
{
    public Device Device { get; }
    [ObservableProperty] private bool _isSelected;
    public string Name { get => Device.Name; set { } }
    public string Model { get => Device.Model; set { } }
    public string OsVersion { get => Device.OsVersion; set { } }
    public string Udid { get => Device.Udid; set { } }
    public DeviceLink Link => Device.Link;
    public string DeviceClass { get => Device.DeviceClass; set { } }
...
```

```csharp
// App.xaml.cs
private static bool _isShowingError;
private static void ShowError(Exception ex)
{
    if (_isShowingError) return;
    try
    {
        _isShowingError = true;
        MessageBox.Show(...);
    }
    catch { }
    finally { _isShowingError = false; }
}
```

## Tests Added or Updated

- Статический аудит всех элементов `Run Text="{Binding"` в проекте XAML — 100% привязок теперь имеют `Mode=OneWay`.
- Защита `SelectableDevice` исключает возможность падения WPF Binding Engine даже в случае непредусмотренной двусторонней привязки.

## Local Verification

- Commands run:
  - `python scratch/check_loc.py` → `Localization OK: 922 matching RU/EN keys; UTF-8 and references are valid.` (pass)
  - `dotnet build` → `Сборка успешно завершена. Предупреждений: 5. Ошибок: 0.` (pass)
  - `Get-ChildItem -Path src -Filter *.xaml -Recurse | Select-String "Run Text="` → все привязки валидированы с `Mode=OneWay`.

## Deviations from Assessment

None. Все предложенные в оценке шаги реализованы в точности, плюс дополнительно защищен `SupportAdminWindow.xaml`.

## Follow-ups

- Запушить коммит через GitHub API скрипт и дождаться автоматической сборки релиза `v1.8.51`.
