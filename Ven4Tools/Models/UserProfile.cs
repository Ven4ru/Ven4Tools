namespace Ven4Tools.Models
{
    public class UserProfile
    {
        // Каталог
        public string CatalogMode { get; set; } = "full"; // "basic", "extended", "full"
        public bool HideInstalled { get; set; } = false;
        public string DefaultSort { get; set; } = "alpha"; // "alpha", "category"

        // Интерфейс
        public string Theme { get; set; } = "teal";
        public string Language { get; set; } = "auto"; // "auto", "ru", "en"
        public bool CompactMode { get; set; } = false;
        public bool ReduceMotion { get; set; } = false;

        // Установка
        public bool SilentInstall { get; set; } = false;
        public string DefaultInstallFolder { get; set; } = "";

        // Уведомления
        public bool NotifyAppUpdates { get; set; } = true;

        // Windows Update: "NotSet" (первый вход ещё не пройден), "NotifyOnly", "NotifyAndDownload".
        public string WindowsUpdateMode { get; set; } = "NotSet";

        // Приватность
        public bool SaveInstallHistory { get; set; } = true;

        // Параноидальный режим: отключает ВСЕ исходящие сетевые запросы клиента,
        // кроме двух жизненно необходимых — загрузки каталога приложений и
        // скачивания/установки самих приложений. Блокирует отправку краш-отчётов,
        // отзывов, фоновые проверки обновлений и авто-пинги определения сети.
        public bool ParanoidMode { get; set; } = false;

        // Первый запуск
        public bool HasSelectedCategory { get; set; } = false;

        // Офлайн-режим
        public bool OfflineMode { get; set; } = false;
        public string OfflineCachePath { get; set; } = "";

        // LEGACY: защита от отката каталога (anti-rollback) переехала в отдельный
        // DPAPI-защищённый CatalogVersionGuard (catalog_guard.dat) — plaintext-поле
        // здесь можно было сбросить правкой profile.json, снимая защиту. Поле оставлено
        // только для разовой миграции: при первом запуске новой версии его значение
        // наследуется в защищённое хранилище. Больше не обновляется.
        public int LastCatalogVersion { get; set; } = 0;

        // Принудительный онлайн-режим: игнорировать результат ConnectivityMonitor
        // и всегда считать соединение активным. Нужен для VPN/прокси, где детект
        // сети даёт ложноотрицательные результаты и онлайн-вкладки ошибочно скрываются.
        public bool ForceOnlineMode { get; set; } = false;

        // Закреплённые приложения (не более 6 идентификаторов)
        public System.Collections.Generic.List<string> PinnedAppIds { get; set; } = new();

        // Трей
        public bool MinimizeToTray { get; set; } = false;
    }
}
