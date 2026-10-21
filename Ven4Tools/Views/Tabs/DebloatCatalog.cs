using System;
using System.Collections.Generic;
using System.Linq;
using Ven4Tools.Services;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views.Tabs
{
    /// <summary>
    /// Реестр доступных твиков очистки: имя, идентификатор, категория, уровень риска
    /// и пояснение для пользователя. Это справочные данные, а не UI-логика, поэтому
    /// они вынесены из code-behind вкладки «Очистка» в отдельный класс. Идентификаторы
    /// здесь — те же, что разбирает <see cref="Services.DebloatTweakExecutor"/> и что
    /// попадают в снапшоты конфигурации, менять их без нужды нельзя.
    /// </summary>
    public static class DebloatCatalog
    {
        public static List<DebloatItem> BuildItems() => new()
        {
            // ── Приложения ────────────────────────────────────────────────────────
            new("Xbox Game Bar",           "Microsoft.XboxGamingOverlay",    "app", "safe",     "Оверлей для записи и скриншотов Xbox. Никак не влияет на геймплей."),
            new("Xbox App",                "Microsoft.XboxApp",              "app", "safe",     "Клиент Xbox. Не нужен без Xbox-аккаунта."),
            new("Xbox Identity Provider",  "Microsoft.XboxIdentityProvider", "app", "safe",     "Аутентификация Xbox."),
            new("Xbox TCUI",               "Microsoft.Xbox.TCUI",            "app", "safe",     "Интерфейсы Xbox. Безопасно удалять."),
            new("Xbox Speech To Text",     "Microsoft.XboxSpeechToTextOverlay","app","safe",    "Голосовой ввод Xbox."),
            new("3D Builder",              "Microsoft.3DBuilder",            "app", "safe",     "Редактор 3D-моделей. Почти никем не используется."),
            new("3D Viewer",               "Microsoft.Microsoft3DViewer",    "app", "safe",     "Просмотрщик 3D-файлов."),
            new("Mixed Reality Portal",    "Microsoft.MixedReality.Portal",  "app", "safe",     "VR-портал. Не нужен без шлема."),
            new("Cortana",                 "Microsoft.549981C3F5F10",        "app", "safe",     "Голосовой помощник Cortana."),
            new("Tips",                    "Microsoft.Getstarted",           "app", "safe",     "Подсказки Windows. Назойливые всплывающие советы."),
            new("Get Help",                "Microsoft.GetHelp",              "app", "safe",     "Помощник поддержки Microsoft."),
            new("Office Hub",              "Microsoft.MicrosoftOfficeHub",   "app", "safe",     "Реклама подписки Office 365."),
            new("Solitaire Collection",    "Microsoft.MicrosoftSolitaireCollection","app","safe","Карточные игры с рекламой."),
            new("People",                  "Microsoft.People",               "app", "safe",     "Приложение «Люди» — контакты."),
            new("Print 3D",                "Microsoft.Print3D",              "app", "safe",     "Утилита 3D-печати."),
            new("Skype",                   "Microsoft.SkypeApp",             "app", "safe",     "Skype UWP. Не нужен при использовании десктопной версии."),
            new("To Do",                   "Microsoft.Todos",                "app", "safe",     "Microsoft To Do — приложение задач."),
            new("Feedback Hub",            "Microsoft.WindowsFeedbackHub",   "app", "safe",     "Сбор отзывов для Microsoft."),
            new("Maps",                    "Microsoft.WindowsMaps",          "app", "safe",     "Карты Windows."),
            new("Voice Recorder",          "Microsoft.WindowsSoundRecorder", "app", "safe",     "Запись звука."),
            new("Groove Music",            "Microsoft.ZuneMusic",            "app", "safe",     "Медиаплеер Windows — заменён приложением Media Player."),
            new("Movies & TV",             "Microsoft.ZuneVideo",            "app", "safe",     "Видеоплеер UWP."),
            new("Phone Link",              "Microsoft.YourPhone",            "app", "safe",     "Синхронизация с Android. Можно убрать если не используете."),
            new("Clipchamp",               "Clipchamp.Clipchamp",            "app", "safe",     "Видеоредактор Microsoft."),
            new("Power Automate",          "Microsoft.PowerAutomateDesktop", "app", "safe",     "RPA-инструмент Microsoft."),

            // ── Приватность ───────────────────────────────────────────────────────
            new("Телеметрия Windows",      "telemetry",         "privacy","moderate","Отключает отправку данных диагностики в Microsoft. HKLM AllowTelemetry=0."),
            new("История активности",      "activity_history",  "privacy","safe",    "Отключает запись действий пользователя. HKLM EnableActivityFeed=0."),
            new("Рекламный идентификатор", "advertising_id",    "privacy","safe",    "Отключает персонализацию рекламы. HKCU AdvertisingInfo\\Enabled=0."),
            new("Советы и предложения",    "content_delivery",  "privacy","safe",    "Отключает авто-установку рекомендуемых приложений."),
            new("Cortana (реестр)",        "cortana_registry",  "privacy","safe",    "Полное отключение Cortana через GPO. HKLM AllowCortana=0."),
            new("Слежение за вводом",      "input_tracking",    "privacy","moderate","Отключает отслеживание рукописного ввода и набора текста."),
            new("Запись диагностики",      "diag_track",        "privacy","moderate","Останавливает и отключает службу DiagTrack (Connected User Experiences)."),

            // ── Службы ────────────────────────────────────────────────────────────
            new("DiagTrack",               "svc_diagtrack",     "service","caution","Служба телеметрии Connected User Experiences. Отключение освобождает ресурсы."),
            new("SysMain (Superfetch)",    "svc_sysmain",       "service","moderate","Prefetch-служба. На SSD нет смысла. На HDD может ухудшить произв-ть."),
            new("WAP Push Message",        "svc_dmwappushsvc",  "service","caution","Служба получения push-сообщений. Используется для MDM и отдельных телеметрий."),

            // ── Реклама и навязанный ИИ ───────────────────────────────────────────
            // Что именно меняет каждый твик — таблица AdsRegistry в DebloatTweakExecutor.
            // Твики с «политикой» блокируют соответствующий переключатель в «Параметрах»
            // («управляется организацией») — об этом сказано в описании; «Вернуть» снимает блок.
            new("Рекомендации в меню «Пуск»",   "ads_start_recommendations", "ads", "safe",
                "Убирает из «Пуска» рекомендации советов, ярлыков и новых приложений. Недавние файлы остаются. Действует после перезапуска Проводника.", "win11"),
            new("Реклама Microsoft 365 и учётной записи", "ads_account_notifications", "ads", "safe",
                "Убирает уведомления учётной записи Microsoft в «Пуске» и на главной странице «Параметров» (предложения подписок, «завершите настройку»).", "win11"),
            new("Предложения приложений в «Пуске»", "ads_start_suggestions", "ads", "safe",
                "Отключает «Иногда показывать предложения в меню «Пуск»» — рекламные плитки и значки приложений из Store."),
            new("Советы и подсказки Windows",   "ads_tips",          "ads", "safe",
                "Отключает всплывающие «советы, рекомендации и предложения при использовании Windows»."),
            new("Рекомендации в «Параметрах»",  "ads_settings_suggestions", "ads", "safe",
                "Убирает «рекомендуемое содержимое» и предложения в приложении «Параметры»."),
            new("Экран приветствия после обновлений", "ads_welcome", "ads", "safe",
                "Отключает экран «Что нового» после обновлений и предложения «завершить настройку устройства» (подписки, OneDrive, Microsoft 365)."),
            new("Реклама на экране блокировки", "ads_lock_screen",   "ads", "safe",
                "Отключает «забавные факты, советы и подсказки» поверх экрана блокировки. Сами картинки «Windows: интересное» остаются."),
            new("Персональные советы по данным диагностики", "ads_tailored", "ads", "safe",
                "Microsoft не будет подбирать советы и рекламу по собранным диагностическим данным."),
            new("Реклама OneDrive в Проводнике", "ads_explorer_sync", "ads", "safe",
                "Отключает уведомления поставщиков синхронизации (предложения OneDrive и Microsoft 365) в Проводнике. Действует после перезапуска Проводника."),
            new("Bing и веб-подсказки в поиске", "ads_search_web",   "ads", "moderate",
                "Поиск Windows перестаёт искать в интернете через Bing и показывать веб-подсказки — только файлы и программы на ПК. Это политика: заодно пропадают подсказки недавних запросов в поиске Проводника."),
            new("Тематические подсказки в поиске", "ads_search_highlights", "ads", "safe",
                "Отключает «главное в поиске» — картинки праздников и новостные подборки в поле поиска. Действует после перезапуска Проводника."),
            new("Виджеты (новости и погода)",   "ads_widgets",       "ads", "safe",
                "Политика отключает виджеты Windows 11: кнопка на панели задач и лента новостей MSN пропадают, переключатель «Виджеты» в «Параметрах» становится заблокированным. Действует после перезапуска Проводника.", "win11"),
            new("Новости и интересы на панели задач", "ads_news_feeds", "ads", "safe",
                "Политика отключает ленту «Новости и интересы» (погода и новости MSN) на панели задач Windows 10. Пункт в контекстном меню панели задач пропадёт. Действует после перезапуска Проводника.", "win10"),
            new("Copilot (политика и кнопка)",  "ads_copilot",       "ads", "safe",
                "Политика «Отключить Windows Copilot» и скрытие кнопки Copilot на панели задач. На новых сборках Copilot — отдельное приложение: его убирает пункт ниже. Действует после перезапуска Проводника."),
            new("Приложение Copilot",           DebloatTweakExecutor.CopilotAppId, "ads", "safe",
                "Удаляет приложение Copilot. Вернуть можно из Microsoft Store."),
            new("Recall (снимки экрана ИИ)",    "ads_recall",        "ads", "safe",
                "Политика запрещает Windows сохранять снимки экрана для Recall. Recall есть только на ПК Copilot+; на остальных пункт ничего не меняет. Переключатель в «Параметрах» станет заблокированным.", "win11"),
            new("Удалить OneDrive",             DebloatTweakExecutor.OneDriveId, "ads", "caution",
                "Удаляет клиент OneDrive штатным установщиком. Файлы в облаке и папка OneDrive на диске остаются. Не выполняется, если в OneDrive перенесены «Рабочий стол», «Документы» или «Изображения». «Вернуть» ставит OneDrive заново через winget."),
        };

        /// <summary>Windows 11 — сборка 22000 и новее (у Windows 10 последняя — 19045).</summary>
        public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

        /// <summary>Подходит ли твик этой Windows. Неподходящие не показываются вовсе.</summary>
        public static bool AppliesTo(DebloatItem item, bool isWindows11) => item.Os switch
        {
            "win11" => isWindows11,
            "win10" => !isWindows11,
            _       => true
        };

        /// <summary>Твики, которые имеют смысл на этой Windows.</summary>
        public static List<DebloatItem> BuildItemsFor(bool isWindows11) =>
            BuildItems().Where(item => AppliesTo(item, isWindows11)).ToList();
    }
}
