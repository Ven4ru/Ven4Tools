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
        public bool ReduceMotion { get; set; } = false;

        // Установка
        public bool SilentInstall { get; set; } = false;
        public string DefaultInstallFolder { get; set; } = "";
        // Убирать ярлыки рабочего стола, появившиеся во время установки через клиент.
        // Выключено по умолчанию: решение владельца 16.10.2026.
        public bool RemoveNewDesktopShortcuts { get; set; } = false;

        // Уведомления
        public bool NotifyAppUpdates { get; set; } = true;
        public bool NotifyInstallComplete { get; set; } = true;

        // Автообновление программ: выключено, пока пользователь сам не включит.
        // Работает, пока клиент запущен (в том числе свёрнутый в трей).
        public bool AutoUpdateApps { get; set; } = false;
        // "daily" | "weekly"
        public string AutoUpdateFrequency { get; set; } = "weekly";
        // Идентификаторы пакетов winget, которые автообновление не трогает.
        public System.Collections.Generic.List<string> AutoUpdateExcluded { get; set; } = new();
        public System.DateTime? AutoUpdateLastRunUtc { get; set; }

        // Windows Update: "NotSet" (первый вход ещё не пройден), "NotifyOnly", "NotifyAndDownload".
        public string WindowsUpdateMode { get; set; } = "NotSet";

        // Приватность
        public bool SaveInstallHistory { get; set; } = true;

        // Параноидальный режим: отключает фоновую и диагностическую сетевую
        // активность клиента (не буквально "всё", кроме каталога/установки —
        // список конкретный, см. текст чекбокса на вкладке «Настройки» и README
        // раздел «Сетевые обращения», сверять оттуда при добавлении нового
        // сетевого вызова, а не полагаться на общую формулировку здесь):
        // краш-отчёты, отзывы, фоновые проверки обновлений (winget и Windows
        // Update — включая автопроверку при открытии вкладки, но не ручную
        // кнопку), вся диагностика вкладки «Сеть» (в т.ч. ручная), загрузка
        // значков, индикатор доступности в каталоге. Загрузка каталога и
        // скачивание/установка приложений продолжают работать всегда.
        public bool ParanoidMode { get; set; } = false;

        /// <summary>Венчик в оверлее поверх игр: жарко, нормально или холодно по температурам.</summary>
        public bool OverlayShowMascot { get; set; } = true;

        /// <summary>
        /// Показывать оверлей в играх с античитом. По умолчанию — нет: оверлей сам скрывается,
        /// как только запущена защищённая игра (окно поверх игры античиты видят).
        /// </summary>
        public bool OverlayInAntiCheatGames { get; set; } = false;

        /// <summary>
        /// Держать драйвер датчиков PawnIO включённым всегда. По умолчанию — нет: устройство
        /// включается только пока Ven4Tools читает датчики процессора. Включать, если драйвер
        /// нужен и другим программам (FanControl).
        /// </summary>
        public bool SensorDriverAlwaysOn { get; set; } = false;

        /// <summary>Когда Ven4Tools последний раз включал устройство драйвера датчиков (UTC).</summary>
        public System.DateTime? PawnIoLastEnabledUtc { get; set; }

        // Первый запуск
        public bool HasSelectedCategory { get; set; } = false;

        // Офлайн-режим
        public bool OfflineMode { get; set; } = false;
        public string OfflineCachePath { get; set; } = "";

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
