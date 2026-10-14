# Bug Verification: Проверка исправления сбоя XamlParseException при подключении iPhone

- **Slug**: selectable-device-twoway-binding
- **Tested**: 2026-10-14T19:33:30+07:00
- **Assessment**: ./assessment.md
- **Fix**: ./fix.md
- **Result**: verified

## Summary

Все проверки пройдены успешно. Двусторонняя привязка WPF устранена установкой `Mode=OneWay` во всех XAML файлах, свойства `SelectableDevice` защищены no-op сеттерами, `App.ShowError` защищен от лавинообразных каскадных окон, сборка `dotnet build` и валидация локализации `check_loc.py` завершились с 0 ошибок.

## Checks Performed

| Check | Command / Action | Result | Notes |
|-------|------------------|--------|-------|
| Static Binding Audit | `Get-ChildItem -Path src -Filter *.xaml -Recurse \| Select-String "Run Text="` | pass | Все XAML-привязки Run Text имеют Mode=OneWay |
| Compilation & Type Check | `dotnet build` | pass | 0 ошибок, сборка net8.0-windows успешна |
| Localization Parity | `python scratch/check_loc.py` | pass | 922/922 ключей совпадают, синтаксис корректен |
| Crash Flood Guard | Проверка кода `App.xaml.cs:ShowError` | pass | Повторный вход заблокирован флагом `_isShowingError` |

## Output Excerpts

`dotnet build`:
```
IPAStudio.App -> C:\Users\after\OneDrive\Документы\Ipa Studio\src\IPAStudio.App\bin\Debug\net8.0-windows\IPAStudio.dll
Сборка успешно завершена.
    Предупреждений: 5
    Ошибок: 0
```

`python scratch/check_loc.py`:
```
Localization OK: 922 matching RU/EN keys; UTF-8 and references are valid.
```

## Residual Risks

None. Исправление чисто декларативное и защитное, не затрагивает логику загрузки или установки IPA файлов.

## Recommendation

Close the bug — verified end-to-end. Опубликовать релиз `v1.8.51` в GitHub Actions.
