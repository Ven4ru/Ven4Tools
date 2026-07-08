namespace Ven4Tools.Models
{
    public class UserProfile
    {
        // Catalog
        public string CatalogMode { get; set; } = "full"; // "basic", "extended", "full"
        public bool HideInstalled { get; set; } = false;
        public string DefaultSort { get; set; } = "alpha"; // "alpha", "category"

        // UI
        public string Theme { get; set; } = "teal";
        public string Language { get; set; } = "auto"; // "auto", "ru", "en"
        public bool CompactMode { get; set; } = false;
        public string AccentColorHex { get; set; } = ""; // "" = theme default

        // Install
        public bool SilentInstall { get; set; } = false;
        public string DefaultInstallFolder { get; set; } = "";

        // Notifications
        public bool NotifyAppUpdates { get; set; } = true;
        public bool NotifyNewApps { get; set; } = false;

        // Windows Update: "NotSet" (первый вход ещё не пройден), "NotifyOnly", "NotifyAndDownload".
        public string WindowsUpdateMode { get; set; } = "NotSet";

        // Privacy
        public bool SaveInstallHistory { get; set; } = true;

        // Параноидальный режим: отключает ВСЕ исходящие сетевые запросы клиента,
        // кроме двух жизненно необходимых — загрузки каталога приложений и
        // скачивания/установки самих приложений. Блокирует отправку краш-отчётов,
        // отзывов, фоновые проверки обновлений и авто-пинги определения сети.
        public bool ParanoidMode { get; set; } = false;

        // First-run
        public bool HasSelectedCategory { get; set; } = false;

        // Offline mode
        public bool OfflineMode { get; set; } = false;
        public string OfflineCachePath { get; set; } = "";

        // Принудительный онлайн-режим: игнорировать результат ConnectivityMonitor
        // и всегда считать соединение активным. Нужен для VPN/прокси, где детект
        // сети даёт ложноотрицательные результаты и онлайн-вкладки ошибочно скрываются.
        public bool ForceOnlineMode { get; set; } = false;

        // Quick pins (max 6 app IDs)
        public System.Collections.Generic.List<string> PinnedAppIds { get; set; } = new();

        // Tray
        public bool MinimizeToTray { get; set; } = false;
    }
}
