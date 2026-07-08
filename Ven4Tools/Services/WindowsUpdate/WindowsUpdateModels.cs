using System;
using System.Collections.Generic;

namespace Ven4Tools.Services.WindowsUpdate
{
    /// <summary>Один патч Windows, как он приходит из Windows Update Agent.</summary>
    public sealed class WindowsUpdateItem
    {
        // UpdateID из COM API — стабильный идентификатор конкретного обновления,
        // используется для повторного поиска/установки (не доверяем позиции в списке).
        public string UpdateId { get; init; } = "";
        public string Title { get; init; } = "";
        public IReadOnlyList<string> CategoryNames { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> KbArticleIds { get; init; } = Array.Empty<string>();
        public long SizeBytes { get; init; }
        public string Severity { get; init; } = ""; // MsrcSeverity: "Critical", "Important", "" и т.д.
        public bool IsDownloaded { get; init; }
        public bool EulaAccepted { get; init; }
        public string EulaText { get; init; } = "";
    }

    /// <summary>Результат Search() — либо список патчей, либо явная ошибка с сообщением на русском.</summary>
    public sealed class WindowsUpdateSearchResult
    {
        public bool Success { get; init; }
        public IReadOnlyList<WindowsUpdateItem> Items { get; init; } = Array.Empty<WindowsUpdateItem>();
        public string ErrorMessage { get; init; } = "";

        public static WindowsUpdateSearchResult Ok(IReadOnlyList<WindowsUpdateItem> items) =>
            new() { Success = true, Items = items };

        public static WindowsUpdateSearchResult Failed(string message) =>
            new() { Success = false, ErrorMessage = message };
    }

    /// <summary>Прогресс скачивания/установки одного патча — для IProgress&lt;T&gt; в UI.</summary>
    public sealed class WindowsUpdateProgress
    {
        public string CurrentTitle { get; init; } = "";
        public int CompletedCount { get; init; }
        public int TotalCount { get; init; }
        public string Phase { get; init; } = ""; // "Скачивание" | "Установка"
        public int PercentComplete { get; init; }
    }

    /// <summary>Итог установки одного патча.</summary>
    public sealed class WindowsUpdateItemOutcome
    {
        public string UpdateId { get; init; } = "";
        public string Title { get; init; } = "";
        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = "";
    }

    /// <summary>Итог всей партии установки.</summary>
    public sealed class WindowsUpdateInstallOutcome
    {
        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = "";
        public IReadOnlyList<WindowsUpdateItemOutcome> Items { get; init; } = Array.Empty<WindowsUpdateItemOutcome>();
        public bool RebootRequired { get; init; }
    }
}
