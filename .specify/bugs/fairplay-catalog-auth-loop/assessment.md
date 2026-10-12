# Bug Assessment: Бесконечный цикл авторизации Apple ID при установке из Каталога ("Выбрать")

- **Slug**: fairplay-catalog-auth-loop
- **Created**: 2026-10-12T04:40:00+07:00
- **Source**: pasted text
- **Verdict**: valid
- **Severity**: critical

## Report (verbatim or summarized)

> "Суть ошибки следующая — если зайти под учетку, потом в кнопку 'скачать ipa' скачать её и установить на телефон клиента, а в нем авторизоваться под своей учеткой — то все работает. Но если делать это через кнопку 'выбрать' и там из каталога выбрать точно такое же приложение, скачать и установить на телефон клиента — он начинает бесконечно просить учетку: при вводе приложение открывается, закрывается и снова просит учетку, и так по кругу."

## Symptom

1. **Ожидаемое поведение**: Приложение, скачанное и установленное через каталог ("Выбрать"), должно иметь валидную привязку лицензии FairPlay DRM к текущему авторизованному Apple ID. При запуске на устройстве клиента и вводе учетных данных Apple ID (под которым производилась загрузка в IPA Studio) устройство должно успешно авторизовать сессию в FairPlay keybag, и приложение должно штатно запуститься без повторных запросов.
2. **Фактическое поведение**: Приложение, скачанное через каталог ("Выбрать"), устанавливается на устройство, но при запуске запрашивает Apple ID, после ввода логина/пароля на долю секунды запускается, аварийно завершается (`fairplayd` termination) и повторно запрашивает авторизацию в бесконечном цикле (FairPlay Authentication Crash Loop). При этом то же самое приложение, скачанное через "Скачать IPA", запускается и авторизуется штатно.

## Reproduction

1. Авторизоваться в IPA Studio под рабочим Apple ID (например, `user@example.com`).
2. **Контрольный тест ("Скачать IPA")**:
   - Перейти в раздел «Скачать IPA» (`DirectDownloadView`).
   - Найти приложение по Bundle ID, ссылке или App Store ID.
   - Скачать IPA в выбранную папку.
   - Установить полученный файл на устройство клиента.
   - Открыть приложение на телефоне клиента, ввести пароль Apple ID — приложение успешно активируется и работает.
3. **Воспроизведение бага ("Выбрать" / Каталог)**:
   - Подключить устройство клиента, нажать «Выбрать» (`AppPickerView`).
   - Найти и выбрать то же самое приложение из каталога (`Apps_ID_List.txt` / `user-apps.json`).
   - Нажать «Установить выбранные» (запуск через `QueueService`).
   - Дождаться скачивания и установки очереди на телефон клиента.
   - Открыть установленное приложение на телефоне клиента, ввести пароль Apple ID.
   - **Результат**: Приложение моргает (открывается и тут же вылетает) и снова запрашивает учетную запись Apple ID в бесконечном цикле.

## Suspected Code Paths

- `src/IPAStudio.Core/Services/QueueService.cs:355-385` — проверка и повторное использование кешированного файла (`reusable = item.App.IsDownloaded && !IsLicensedToAnotherAccount(...)`).
- `src/IPAStudio.Core/Services/QueueService.cs:470-505` — `RunDownloadStageCoreAsync`: вызов `_download.DownloadAsync(item.App, autoPurchase: true)`, пропуск этапа `PurchaseAsync`, если `result.Success == true`, даже если `ipatool` не оформил покупку (`purchased=false`).
- `src/IPAStudio.Core/Services/DownloadService.cs:456-463` — `TryReuseExistingArchive`: повторное использование старого или неполного архива из `%USERPROFILE%\IPAStudio\Apps\`.
- `src/IPAStudio.Core/Services/DownloadService.cs:1140-1178` — обработка результата работы `ipatool`: отсутствие проверки флага `purchased=false` при успешном коде завершения `code=0` (`success=true`).
- `src/IPAStudio.Core/Tools/IpaLicense.cs:110-142, 274-302` — метод `ReadAccount` использует регулярное выражение исключительно для XML-формата `iTunesMetadata.plist`. Если `iTunesMetadata.plist` сохранен в формате бинарного plist (`bplist00`), поля `appleId` и `DSPersonID` не извлекаются (`null`), в результате чего проверка `BelongsToAnotherAccount` ложно возвращает `false` (архив считается валидным и пригодным к установке).
- `src/IPAStudio.Core/Services/CatalogService.cs:782-817` — `RefreshDownloadedFlags` сопоставляет файлы в папке `Apps` только по подстроке `_{entry.AppStoreId}` без строгой верификации актуальности версии и принадлежности лицензии.
- `src/IPAStudio.Core/Resources/Apps_ID_List.txt` — статические, устаревшие App Store ID в каталоге (для удаленных/перевыпущенных приложений банков и санкционных сервисов), по которым невозможно оформить лицензию на новый Apple ID.

## Root Cause Hypothesis

**Уровень уверенности: Высокий (High).**
Корневая причина проблемы кроется в расхождении архитектуры между прямой загрузкой («Скачать IPA») и очередью каталога («Выбрать»):

1. **Скрытый пропуск оформления лицензии (`purchased=false`)**:
   В современных сборках `ipatool` (включая активный SAP-beta бекенд `windows_amd64_sap_beta\ipatool.exe`) вызов `ipatool download -i <ID> --purchase` скачивает Mach-O бинарник из CDN даже тогда, когда Apple Store не оформил покупку лицензии для текущего Apple ID (в логах фиксируется: `purchased=false success=true`). Так как `ipatool` завершается с кодом `0`, `DownloadService` считает загрузку успешной (`result.Success = true`). Блок явной покупки `PurchaseAsync` в `QueueService` не срабатывает (он завязан только на условие `!result.Success && result.LicenseRequired`). В результате IPA сохраняется и передается на устройство без зарегистрированной в Apple транзакции владения для данного Apple ID. При запуске на устройстве сервер Apple (`buy.itunes.apple.com`) видит отсутствие покупки у аккаунта и отказывает в выдаче FairPlay-билета (`kbsync`), провоцируя бесконечный цикл краша и авторизации.
2. **Ошибочное повторное использование старых/битых архивов из `%USERPROFILE%\IPAStudio\Apps`**:
   При установке через «Выбрать» `QueueService` в первую очередь проверяет локальный кеш через `RefreshDownloadedFlags` и `reusable`. Если в папке `Apps` уже лежал файл с совпадающим `_{AppStoreId}` (скачанный ранее другим аккаунтом или скачанный с ошибкой `purchased=false`), `QueueService` **полностью пропускает скачивание** (`skipDownload = true`) и отправляет на устройство клиента устаревший или чужой IPA. При этом в «Скачать IPA» загрузка всегда идет напрямую в целевую папку пользователя (например, «Чеки» / Рабочий стол) минуя кеш очереди `Apps`.
3. **Невозможность прочесть лицензию из Binary Plist (`bplist00`) в `IpaLicense.cs`**:
   `IpaLicense.ReadAccount` парсит `iTunesMetadata.plist` только регулярными выражениями под XML. Однако `ipatool` и официальные пакеты App Store часто упаковывают `iTunesMetadata.plist` в бинарном формате `bplist00`. В этом случае `AppleId` и `DSPersonID` не парсятся (становятся `null`), из-за чего проверка `IpaLicense.BelongsToAnotherAccount` всегда возвращает `false`. Это приводит к тому, что чужой или нелицензированный файл принимается за "свой" и без предупреждения устанавливается клиенту.
4. **Несоответствие статических App Store ID в `Apps_ID_List.txt` актуальным версиям в магазине**:
   В каталоге `Apps_ID_List.txt` многие ID являются историческими (например, оригинальные ID Сбера, Тинькофф и т.д., удаленные из App Store). На аккаунтах, где эти приложения не были куплены до 2022 года, покупка через Storefront API невозможна. В режиме «Скачать IPA» пользователь ищет приложение по Bundle ID или ссылке и получает актуальный рабочий Track ID клона/перевыпуска, на который Apple успешно оформляет лицензию.

## Proposed Remediation

### Preferred Solution

1. **Обработка `purchased=false` и принудительное лицензирование (`DownloadService` & `QueueService`)**:
   - В `DownloadService.cs` парсить вывод `ipatool` на наличие `purchased=false` (или структурного JSON-ответа). Если флаг `purchased=false` обнаружен, фиксировать, что лицензия не была подтверждена (`LicenseRequired = true`).
   - В `QueueService.cs` перед установкой гарантировать, что приложение либо уже числится в списке подтвержденных покупок (`_settings.IsKnownOwned`), либо для него успешно отработал метод `PurchaseAsync`. Если скачанный файл имеет `purchased=false`, вызывать `_download.PurchaseAsync` до передачи файла в установщик.

2. **Поддержка парсинга Binary Plist (`bplist00`) в `IpaLicense.cs`**:
   - Доработать `IpaLicense.ReadAccount`, добавив распознавание сигнатуры `bplist00` и парсинг бинарного формата plist (чтение строковых и целочисленных объектов для ключей `appleId`, `apple-id`, `DSPersonID`, `dsPersonId`, `itemId`).
   - Благодаря этому `BelongsToAnotherAccount` и валидация лицензии перед установкой станут строго детерминированными и не пропустят чужие/битые файлы.

3. **Защита от ошибочного повторного использования кеша (`QueueService` & `CatalogService`)**:
   - В `QueueService.ProcessItemAsync` разрешать `reusable = true` только в том случае, если:
     а) `iTunesMetadata.plist` и `sinf` гарантированно присутствуют и прочитаны.
     б) `report.AppleId` совпадает с текущим `_auth.CurrentAccount?.Email`.
     в) `item.App.AppStoreId` совпадает с `report.ItemId`.
   - В противном случае старый файл из `Apps` игнорировать и производить чистую загрузку с привязкой к текущей активной учетной записи.

4. **Актуализация метаданных и ID перед постановкой в очередь из Каталога**:
   - При выборе приложения в `AppPickerViewModel`, если для приложения доступен `BundleId`, выполнять быструю сверку через iTunes Lookup API, чтобы убедиться, что `AppStoreId` актуален для текущего региона учетной записи, либо использовать поиск по `BundleId` при скачивании (`ipatool -b <BundleId> --purchase`).

### Files likely to change:
- `src/IPAStudio.Core/Tools/IpaLicense.cs`
- `src/IPAStudio.Core/Services/DownloadService.cs`
- `src/IPAStudio.Core/Services/QueueService.cs`
- `src/IPAStudio.Core/Services/CatalogService.cs`
- `src/IPAStudio.App/ViewModels/AppPickerViewModel.cs`

### Tests to add or update:
- Тест парсера `IpaLicense`: проверка чтения `appleId` и `DSPersonID` из реального файла `bplist00` (`AmneziaWG` / `Mail.ru`).
- Тест детекции `purchased=false`: проверка перевода статуса загрузки в `LicenseRequired`.
- Тест кеша очереди: проверка отклонения архива, если он привязан к другому Apple ID или не содержит валидного `DSPersonID`.

## Risks & Considerations

- **Дополнительное время на `PurchaseAsync`**: Явный вызов `PurchaseAsync` добавляет 1-3 секунды сетевого взаимодействия с Apple Storefront API, однако это гарантирует отсутствие бесконечного цикла крашей на телефоне клиента.
- **Удаленные приложения (Delisted)**: Для приложений, полностью удаленных из всех магазинов Apple, покупка на новые Apple ID невозможна в принципе. Программа должна открыто и понятно сообщать об этом пользователю, а не делать вид, что приложение успешно установлено.

## Open Questions

- Требуется ли в интерфейсе очереди добавить кнопку «Принудительно перекачать» (в обход кеша `AppsFolder`) для удобства мастеров и сервисных центров?
- Использовать ли для каталога автоматический fallback на поиск по `BundleId` (`ipatool -b`), если числовой AppStoreId из `Apps_ID_List.txt` возвращает `purchased=false`?
