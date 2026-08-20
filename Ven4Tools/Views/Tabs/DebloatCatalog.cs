using System.Collections.Generic;
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
        };
    }
}
