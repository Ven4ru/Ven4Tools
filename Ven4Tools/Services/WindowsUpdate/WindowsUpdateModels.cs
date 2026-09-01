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

    /// <summary>
    /// Итог всей партии фонового скачивания (без установки). Отдельный тип, а не
    /// переиспользование WindowsUpdateInstallOutcome: у скачивания принципиально нет
    /// RebootRequired — файлы просто ложатся в кэш Windows Update, система при этом
    /// не меняется. Поле, которое всегда false, читалось бы как «перезагрузка не нужна
    /// именно по итогу проверки», хотя вопрос вообще не задавался; отдельный тип не
    /// оставляет места для такой ошибки на стороне вызывающего кода. Поэлементный
    /// WindowsUpdateItemOutcome переиспользуется как есть — «патч X, успех/ошибка,
    /// текст ошибки» одинаково осмысленно и для скачивания, и для установки.
    /// <para>
    /// Общего флага Success здесь тоже нет, и по той же причине. Для установки «успех
    /// всей партии» — осмысленный итог: его показывает финальное окно. Для фонового
    /// скачивания частичный результат ценен сам по себе — три патча из пяти уже лежат
    /// в кэше, и установка этих трёх пойдёт мгновенно; оставшиеся повторит следующая
    /// проверка. Флаг «скачалось всё» провоцировал бы вызывающий код на
    /// «if (!Success) return;», молча выбрасывая уже проделанную работу. Поэтому итог
    /// партии читается только поэлементно (Items), а ErrorMessage заполняется, лишь
    /// когда операция вообще не стартовала (нечего качать, подсистема занята, ошибка
    /// COM) — тогда Items пуст.
    /// </para>
    /// </summary>
    public sealed class WindowsUpdateDownloadOutcome
    {
        public string ErrorMessage { get; init; } = "";
        public IReadOnlyList<WindowsUpdateItemOutcome> Items { get; init; } = Array.Empty<WindowsUpdateItemOutcome>();
    }
}
