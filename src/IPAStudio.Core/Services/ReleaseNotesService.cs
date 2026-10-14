namespace IPAStudio.Core.Services;

/// <summary>Category of a changelog item for visual badging.</summary>
public enum ReleaseCategory
{
    Feature,
    Improvement,
    Fix
}

/// <summary>Single categorized highlight entry within a release note.</summary>
public sealed class ReleaseItem
{
    public ReleaseCategory Category { get; }
    public string CategoryLabel { get; }
    public string Title { get; }
    public string Description { get; }

    public ReleaseItem(ReleaseCategory category, string categoryLabel, string title, string description)
    {
        Category = category;
        CategoryLabel = categoryLabel;
        Title = title;
        Description = description;
    }
}

/// <summary>Complete release note for a specific application version.</summary>
public sealed class ReleaseNote
{
    public string Version { get; }
    public string Date { get; }
    public string Title { get; }
    public IReadOnlyList<ReleaseItem> Items { get; }

    public ReleaseNote(string version, string date, string title, IReadOnlyList<ReleaseItem> items)
    {
        Version = version;
        Date = date;
        Title = title;
        Items = items;
    }
}

/// <summary>
/// Provides curated, localized release notes for the running and past versions of IPA Studio.
/// </summary>
public sealed class ReleaseNotesService
{
    public IReadOnlyList<ReleaseNote> GetAllReleaseNotes(string language)
    {
        var isRu = !string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);

        if (isRu)
        {
            return new List<ReleaseNote>
            {
                new ReleaseNote(
                    version: "1.8.52",
                    date: "15.10.2026",
                    title: "Кристальная четкость текста и оптимизация рендеринга",
                    items: new List<ReleaseItem>
                    {
                        new ReleaseItem(
                            ReleaseCategory.Improvement,
                            "УЛУЧШЕНИЕ",
                            "Кристальная четкость текста в окне обновлений",
                            "Устранен эффект размытия («стереоэффект») текста. Тень модального окна вынесена в независимый фоновый слой, разблокировав прямое субпиксельное сглаживание ClearType и выравнивание глифов по физическим пикселям экрана."),
                        new ReleaseItem(
                            ReleaseCategory.Improvement,
                            "УЛУЧШЕНИЕ",
                            "Оптимизация анимации и снятие трансформаций",
                            "После завершения всплытия карточки активные таймеры трансформаций корректно сбрасываются в фиксированные пиксельные координаты, исключая субпиксельные смещения."),
                    }),

                new ReleaseNote(
                    version: "1.8.51",
                    date: "14.10.2026",
                    title: "Стабильность подключения устройств и защита от сбоев интерфейса",
                    items: new List<ReleaseItem>
                    {
                        new ReleaseItem(
                            ReleaseCategory.Fix,
                            "ИСПРАВЛЕНИЕ",
                            "Устранение ошибки при подключении iPhone в «Скачать IPA»",
                            "Исправлена критическая ошибка XamlParseException привязки данных модели устройства. Теперь при подключении любого iPhone/iPad список устройств открывается мгновенно и без сбоев."),
                        new ReleaseItem(
                            ReleaseCategory.Fix,
                            "ИСПРАВЛЕНИЕ",
                            "Защита от каскадных окон ошибок",
                            "Добавлен глобальный барьер повторного входа для диалогов сообщений об ошибках, предотвращающий лавинообразное открытие множества всплывающих окон при UI-сбоях."),
                        new ReleaseItem(
                            ReleaseCategory.Improvement,
                            "УЛУЧШЕНИЕ",
                            "Отказоустойчивость привязок разметки",
                            "Все свойства моделей выбора устройств оснащены защитными обработчиками, предотвращающими сбои WPF разметки во всех режимах привязки."),
                    }),

                new ReleaseNote(
                    version: "1.8.50",
                    date: "14.10.2026",
                    title: "Пакетные действия со всеми устройствами и плавная анимация переключения",
                    items: new List<ReleaseItem>
                    {
                        new ReleaseItem(
                            ReleaseCategory.Feature,
                            "НОВОЕ",
                            "Проверка и скачивание всей пачки устройств",
                            "Добавлены кнопки «Проверить все» и «Скачать для всех» как в панели управления над прошивками, так и в блоке «Мои устройства» слева."),
                        new ReleaseItem(
                            ReleaseCategory.Improvement,
                            "УЛУЧШЕНИЕ",
                            "Плавная анимация выделения и переключения устройств",
                            "Синяя рамка выделения и фон карточки теперь плавно расцветают с мягким тактильным пружинящим откликом вместо резкого переключения."),
                        new ReleaseItem(
                            ReleaseCategory.Improvement,
                            "УЛУЧШЕНИЕ",
                            "Плавный переход содержимого таблицы",
                            "При выборе другого устройства таблица доступных прошивок аккуратно переключается с эффектом мягкого кросс-фейда."),
                    }),

                new ReleaseNote(
                    version: "1.8.49",
                    date: "14.10.2026",
                    title: "Пакетное скачивание прошивок и анимации",
                    items: new List<ReleaseItem>
                    {
                        new ReleaseItem(
                            ReleaseCategory.Feature,
                            "НОВОЕ",
                            "Пакетное скачивание прошивок",
                            "Кнопка запуска множественного скачивания позволяет загружать выбранные версии или все актуальные подписанные прошивки в один клик."),
                        new ReleaseItem(
                            ReleaseCategory.Feature,
                            "НОВОЕ",
                            "Ручная проверка статуса Apple TSS",
                            "Кнопки быстрой проверки для отдельного устройства и для всех добавленных моделей сразу опрашивают серверы Apple и обновляют статусы подписи."),
                        new ReleaseItem(
                            ReleaseCategory.Improvement,
                            "УЛУЧШЕНИЕ",
                            "Плавные анимации добавления и удаления",
                            "Карточки моделей в меню прошивок теперь плавно появляются с эффектом наплыва и мягко схлопываются при удалении без резких визуальных скачков."),
                    }),

                new ReleaseNote(
                    version: "1.8.48",
                    date: "14.10.2026",
                    title: "Окно «Что нового» при обновлении",
                    items: new List<ReleaseItem>
                    {
                        new ReleaseItem(
                            ReleaseCategory.Feature,
                            "НОВОЕ",
                            "Окно «Что нового» при обновлении",
                            "При первом запуске после каждого обновления программы появляется плавное окно с кратким и наглядным списком всех изменений."),
                        new ReleaseItem(
                            ReleaseCategory.Improvement,
                            "УЛУЧШЕНИЕ",
                            "Плавные анимации и дизайн в стиле iOS / macOS",
                            "Всплывающее окно оснащено мягким затемнением фона, плавной анимацией появления и цветными бейджами категорий."),
                        new ReleaseItem(
                            ReleaseCategory.Improvement,
                            "УЛУЧШЕНИЕ",
                            "История версий всегда под рукой",
                            "Список изменений можно повторно открыть в любой момент из Настроек или главного меню (шестерёнки)."),
                    }),

                new ReleaseNote(
                    version: "1.8.47",
                    date: "13.10.2026",
                    title: "Установка на устройство и русские имена",
                    items: new List<ReleaseItem>
                    {
                        new ReleaseItem(
                            ReleaseCategory.Feature,
                            "НОВОЕ",
                            "Установка на устройство в ручном скачивании",
                            "Кнопка «Установить на устройство» позволяет сразу отправить скачанный IPA на подключённый iPhone или iPad."),
                        new ReleaseItem(
                            ReleaseCategory.Improvement,
                            "УЛУЧШЕНИЕ",
                            "Интерактивный выбор устройств и мультиустановка",
                            "Панель выбора устройств появляется по клику, поддерживает тактильную анимацию нажатия и установку на несколько устройств одновременно."),
                        new ReleaseItem(
                            ReleaseCategory.Fix,
                            "ИСПРАВЛЕНИЕ",
                            "Поддержка русских названий без транслитерации",
                            "Имена файлов на кириллице (например, «Сберкидс.ipa») больше не заменяются английской транслитерацией."),
                        new ReleaseItem(
                            ReleaseCategory.Fix,
                            "ИСПРАВЛЕНИЕ",
                            "Адаптивная панель кнопок",
                            "Кнопки панели действий теперь аккуратно переносятся на новую строку и не обрезают текст («Открыть папку»)."),
                    }),

                new ReleaseNote(
                    version: "1.8.46",
                    date: "12.10.2026",
                    title: "Прямая загрузка без очередей",
                    items: new List<ReleaseItem>
                    {
                        new ReleaseItem(
                            ReleaseCategory.Feature,
                            "НОВОЕ",
                            "Прямое скачивание IPA",
                            "Возможность быстро скачать приложение по Bundle ID или ссылке без подключения устройства и без очередей."),
                        new ReleaseItem(
                            ReleaseCategory.Improvement,
                            "УЛУЧШЕНИЕ",
                            "Интеграция с библиотекой iTunes",
                            "Автоматическое обнаружение установленного iTunes 12.6.5.3 для прямой загрузки через iTunes API."),
                        new ReleaseItem(
                            ReleaseCategory.Fix,
                            "ИСПРАВЛЕНИЕ",
                            "Сброс статуса загрузки при смене Apple ID",
                            "Файлы, привязанные к другой учётной записи, больше не отмечаются как скачанные для текущего аккаунта."),
                    }),

                new ReleaseNote(
                    version: "1.8.45",
                    date: "11.10.2026",
                    title: "Исправление авторизации FairPlay",
                    items: new List<ReleaseItem>
                    {
                        new ReleaseItem(
                            ReleaseCategory.Fix,
                            "ИСПРАВЛЕНИЕ",
                            "Устранение зацикливания авторизации",
                            "Исправлена проблема, при которой приложения, установленные из каталога, бесконечно запрашивали Apple ID."),
                        new ReleaseItem(
                            ReleaseCategory.Improvement,
                            "УЛУЧШЕНИЕ",
                            "Поддержка бинарных манифестов bplist00",
                            "Точный разбор метаданных iTunesMetadata.plist для надёжной верификации лицензий FairPlay."),
                    }),
            };
        }

        return new List<ReleaseNote>
        {
            new ReleaseNote(
                version: "1.8.52",
                date: "10/15/2026",
                title: "Crisp Text Rendering & UI Subpixel Snapping",
                items: new List<ReleaseItem>
                {
                    new ReleaseItem(
                        ReleaseCategory.Improvement,
                        "IMPROVEMENT",
                        "Crisp text rendering in What's New dialog",
                        "Resolved text blurriness and chromatic stereoscopic fringing in the release dialog. Window shadow was isolated to a dedicated background layer, unlocking native ClearType subpixel rendering and display-mode pixel snapping."),
                    new ReleaseItem(
                        ReleaseCategory.Improvement,
                        "IMPROVEMENT",
                        "Animation cleanup and transform release",
                        "Scale and translation animation clocks are cleanly released upon modal presentation, locking visual elements into exact integer physical device pixels."),
                }),

            new ReleaseNote(
                version: "1.8.51",
                date: "10/14/2026",
                title: "Device Connection Stability & UI Crash Protection",
                items: new List<ReleaseItem>
                {
                    new ReleaseItem(
                        ReleaseCategory.Fix,
                        "FIX",
                        "Resolved crash when connecting iPhone in Direct Download",
                        "Fixed a critical XamlParseException caused by WPF two-way data binding on read-only device model properties. Connecting any iPhone or iPad now works smoothly."),
                    new ReleaseItem(
                        ReleaseCategory.Fix,
                        "FIX",
                        "Modal error storm protection",
                        "Introduced a global re-entrancy guard for error dialogs, preventing cascades of stacked message boxes during UI rendering events."),
                    new ReleaseItem(
                        ReleaseCategory.Improvement,
                        "IMPROVEMENT",
                        "Resilient XAML property bindings",
                        "Device selection models now include resilient setter guards across all view bindings to guarantee zero crashes under any WPF binding mode."),
                }),

            new ReleaseNote(
                version: "1.8.50",
                date: "10/14/2026",
                title: "Batch Actions for All Devices and Fluid Card Transitions",
                items: new List<ReleaseItem>
                {
                    new ReleaseItem(
                        ReleaseCategory.Feature,
                        "NEW",
                        "Batch Check and Download for All Devices",
                        "Dedicated 'Check All' and 'Download for All' buttons are now prominently available in both the top firmware toolbar and the sidebar footer."),
                    new ReleaseItem(
                        ReleaseCategory.Improvement,
                        "IMPROVEMENT",
                        "Fluid selection bloom and tactile press animation",
                        "Device cards smoothly bloom their accent selection ring with tactile spring physics instead of abrupt snapping."),
                    new ReleaseItem(
                        ReleaseCategory.Improvement,
                        "IMPROVEMENT",
                        "Smooth table cross-fade transition",
                        "Switching between devices gently cross-fades the firmware table for a cohesive Apple-grade experience."),
                }),

            new ReleaseNote(
                version: "1.8.49",
                date: "10/14/2026",
                title: "Firmware Batch Downloads & Fluid Animations",
                items: new List<ReleaseItem>
                {
                    new ReleaseItem(
                        ReleaseCategory.Feature,
                        "NEW",
                        "Batch firmware downloads",
                        "Launch multiple downloads at once for selected releases or all active signed firmwares with a single click."),
                    new ReleaseItem(
                        ReleaseCategory.Feature,
                        "NEW",
                        "Instant Apple TSS verification",
                        "Manual check buttons for single devices and all tracked models query Apple servers and refresh TSS signing statuses on demand."),
                    new ReleaseItem(
                        ReleaseCategory.Improvement,
                        "IMPROVEMENT",
                        "Fluid device addition and removal animations",
                        "Model cards in the firmware sidebar smoothly animate in and seamlessly collapse upon removal without abrupt UI jumps."),
                }),

            new ReleaseNote(
                version: "1.8.48",
                date: "10/14/2026",
                title: "What's New Modal on Update",
                items: new List<ReleaseItem>
                {
                    new ReleaseItem(
                        ReleaseCategory.Feature,
                        "NEW",
                        "\"What's New\" modal on update",
                        "On first launch after each app update, a smooth modal appears with a concise summary of new features and fixes."),
                    new ReleaseItem(
                        ReleaseCategory.Improvement,
                        "IMPROVEMENT",
                        "Fluid iOS / macOS-style animations",
                        "Featuring backdrop dimming, fluid entrance/exit animations, and categorized color badges."),
                    new ReleaseItem(
                        ReleaseCategory.Improvement,
                        "IMPROVEMENT",
                        "Changelog always accessible",
                        "Review release notes anytime from Settings or the top-right gear menu."),
                }),

            new ReleaseNote(
                version: "1.8.47",
                date: "10/13/2026",
                title: "Direct Device Install & Unicode Filenames",
                items: new List<ReleaseItem>
                {
                    new ReleaseItem(
                        ReleaseCategory.Feature,
                        "NEW",
                        "Direct device install in manual download",
                        "The \"Install to device\" button lets you deploy downloaded IPAs directly to connected iPhones or iPads."),
                    new ReleaseItem(
                        ReleaseCategory.Improvement,
                        "IMPROVEMENT",
                        "Interactive device picker & multi-device installs",
                        "On-demand target device drawer with tactile micro-pop animations and parallel multi-device deployment."),
                    new ReleaseItem(
                        ReleaseCategory.Fix,
                        "FIX",
                        "Unicode Cyrillic filenames preserved",
                        "Renaming files in Russian preserves Cyrillic characters without forced English transliteration."),
                    new ReleaseItem(
                        ReleaseCategory.Fix,
                        "FIX",
                        "Responsive action row layout",
                        "Action buttons now wrap gracefully without text clipping across all window sizes."),
                }),

            new ReleaseNote(
                version: "1.8.46",
                date: "10/12/2026",
                title: "Queue-Free Direct Downloads",
                items: new List<ReleaseItem>
                {
                    new ReleaseItem(
                        ReleaseCategory.Feature,
                        "NEW",
                        "Standalone IPA download",
                        "Quickly download any app by Bundle ID or App Store link without needing a device connected."),
                    new ReleaseItem(
                        ReleaseCategory.Improvement,
                        "IMPROVEMENT",
                        "iTunes library integration",
                        "Automatic detection of iTunes 12.6.5.3 for direct downloads via iTunes API."),
                    new ReleaseItem(
                        ReleaseCategory.Fix,
                        "FIX",
                        "Per-account download cache validation",
                        "IPAs licensed under another Apple ID are properly differentiated when switching accounts."),
                }),

            new ReleaseNote(
                version: "1.8.45",
                date: "10/11/2026",
                title: "FairPlay Authentication Loop Resolution",
                items: new List<ReleaseItem>
                {
                    new ReleaseItem(
                        ReleaseCategory.Fix,
                        "FIX",
                        "Fixed infinite Apple ID prompts",
                        "Resolved FairPlay licensing mismatches when installing IPAs from the catalog."),
                    new ReleaseItem(
                        ReleaseCategory.Improvement,
                        "IMPROVEMENT",
                        "Binary plist support (bplist00)",
                        "Accurate parsing of iTunesMetadata.plist for strict FairPlay license verification."),
                }),
        };
    }

    public ReleaseNote GetReleaseNote(string version, string language)
    {
        var notes = GetAllReleaseNotes(language);
        var match = notes.FirstOrDefault(n => string.Equals(n.Version, version, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        // Fallback for untracked / future build
        var isRu = !string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        return new ReleaseNote(
            version: version,
            date: DateTime.Now.ToString("dd.MM.yyyy"),
            title: isRu ? $"Обновление {version}" : $"Update {version}",
            items: new List<ReleaseItem>
            {
                new ReleaseItem(
                    ReleaseCategory.Improvement,
                    isRu ? "ОБНОВЛЕНИЕ" : "UPDATE",
                    isRu ? "Улучшения производительности и стабильности" : "Performance and stability improvements",
                    isRu ? $"Версия {version} включает последние доработки и исправления." : $"Version {version} includes the latest fixes and improvements.")
            });
    }

    public bool HasNotesForVersion(string version)
    {
        var notes = GetAllReleaseNotes("ru");
        return notes.Any(n => string.Equals(n.Version, version, StringComparison.OrdinalIgnoreCase));
    }
}
