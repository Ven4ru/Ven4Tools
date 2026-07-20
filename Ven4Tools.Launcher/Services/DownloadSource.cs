namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Предпочтительный источник загрузки, выбираемый пользователем в настройках.
    /// Значение лишь ПЕРЕСТАВЛЯЕТ выбранный источник в начало цепочки кандидатов —
    /// остальные источники остаются как фоллбэк позади него (см.
    /// FallbackDownloader.BuildCandidates). Порядок членов совпадает с порядком
    /// пунктов ComboBox в SettingsWindow (индекс = (int)значение) — не менять.
    /// </summary>
    public enum DownloadSource
    {
        /// <summary>Авто: CDN(домен) → CDN(прямой IP) → Хостинг(зеркало) → GitHub.</summary>
        Auto,
        /// <summary>GitHub Releases.</summary>
        Github,
        /// <summary>CDN по доменному имени cdn.ven4tools.ru.</summary>
        CdnDomain,
        /// <summary>CDN по прямому IP в обход DNS (SNI/сертификат — штатные).</summary>
        CdnDirectIp,
        /// <summary>Зеркало релизов на хостинге ven4tools.ru/releases/.</summary>
        HostingMirror
    }
}
