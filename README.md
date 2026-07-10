# IPA Studio

Нативное Windows 10/11 приложение для загрузки IPA из App Store и пакетной установки приложений на iPhone/iPad. Полная переработка [IPA_Downloader](https://github.com/kda2495/IPA_Downloader) с графическим интерфейсом.

*Native Windows 10/11 app for downloading IPA files from the App Store and batch-installing them onto iPhone/iPad. A complete GUI rework of [IPA_Downloader](https://github.com/kda2495/IPA_Downloader).*

## Возможности / Features

- Авторизация Apple ID с поддержкой 2FA (через `ipatool`)
- Каталог из ~570 предзагруженных приложений (список из оригинального проекта) с иконками из iTunes Lookup API
- Живое обнаружение подключённых iPhone/iPad с анимированными карточками устройств (модель, iOS, батарея)
- Мультивыбор приложений галочками, поиск и фильтры (загружено / есть лицензия / установлено)
- Очередь: проверка наличия IPA на диске → проверка/получение лицензии → загрузка → установка на устройство
- Параллельные загрузки, последовательная установка, повтор при ошибках
- Детальный многостадийный прогресс с плавными анимациями
- Русский и английский интерфейс, светлая тема Fluent-стиля
- Выбор версии ipatool: v2 (без iCloud/anisette) или v3 (+ `anisette.exe`)

## Сборка / Build

Требуется Windows + [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
# 1. Скачать tool-бинарники (ipatool, libimobiledevice)
powershell -ExecutionPolicy Bypass -File build/Fetch-Tools.ps1

# 2. Запуск в режиме разработки
dotnet run --project src/IPAStudio.App

# 3. Публикация self-contained сборки
dotnet publish src/IPAStudio.App -c Release -r win-x64 --self-contained true -o build/publish
powershell -File build/Fetch-Tools.ps1 -OutDir build/publish/tools

# 4. Сборка установщика (нужен Inno Setup 6)
iscc build/installer.iss
# Результат: build/output/IPAStudio-Setup-1.0.0.exe
```

### CI

GitHub Actions (`.github/workflows/build.yml`) автоматически собирает установщик и портативную версию на каждый push. Тег `v*` создаёт GitHub Release с Setup.exe.

## Требования на машине пользователя / End-user requirements

- Windows 10/11 x64
- Для установки на iPhone: драйверы Apple (Apple Mobile Device Support — ставятся вместе с iTunes). Установщик предупредит, если они не найдены.
- Учётная запись Apple ID. Загружаются только приложения, доступные вашей учётной записи (бесплатные лицензируются автоматически).

## Структура проекта / Project layout

```
src/IPAStudio.Core/        # Логика: сервисы ipatool/ideviceinstaller, очередь, каталог
src/IPAStudio.App/         # WPF UI: Login, Devices, AppPicker, Queue, Settings
build/Fetch-Tools.ps1      # Скачивание CLI-инструментов
build/installer.iss        # Inno Setup установщик
.github/workflows/build.yml
```

## Лицензия / License

Основано на IPA_Downloader (см. лицензию исходного проекта). Инструменты `ipatool` и `libimobiledevice` принадлежат их авторам.
