using System;
using System.IO;
using System.Net;
using System.Net.Http.Headers;

namespace Ven4Tools.Launcher.Services;

/// <summary>Что делать с ответом на запрос докачки (Range: bytes=N-).</summary>
internal enum ResumeAction
{
    /// <summary>206 ровно с запрошенного байта — дописываем в конец .partial.</summary>
    Append,

    /// <summary>200: источник докачку не поддерживает и отдал файл целиком — пишем с нуля
    /// из этого же ответа.</summary>
    RestartFromZero,

    /// <summary>416, 206 не с того байта или без Content-Range — ответу не верим:
    /// .partial выбрасывается, запрос повторяется без Range.</summary>
    Reject,
}

/// <summary>
/// Чистая логика докачки: разбор ответа на Range-запрос и итоговая длина файла.
/// Без сети и диска — покрыта unit-тестами.
///
/// Докачка не проверяет, что продолжение взято из того же файла (If-Range не
/// используется: у CDN, зеркала и GitHub разные ETag на один и тот же архив). Склейку
/// из разных файлов отсекает итоговая сверка SHA256 — поэтому докачка разрешена
/// только там, где ожидаемый хеш известен (см. FallbackDownloader).
/// </summary>
internal static class DownloadResume
{
    public static ResumeAction Classify(HttpStatusCode status, ContentRangeHeaderValue? range, long requestedFrom)
    {
        if (status == HttpStatusCode.OK) return ResumeAction.RestartFromZero;
        if (status != HttpStatusCode.PartialContent) return ResumeAction.Reject;

        if (range == null || !range.HasRange || range.From != requestedFrom) return ResumeAction.Reject;
        if (!string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)) return ResumeAction.Reject;
        // «bytes N-M/L» с концом раньше начала или длиной меньше уже скачанного — мусор.
        if (range.To.HasValue && range.To.Value < requestedFrom) return ResumeAction.Reject;
        if (range.HasLength && range.Length!.Value <= requestedFrom) return ResumeAction.Reject;
        return ResumeAction.Append;
    }

    /// <summary>
    /// Полная длина файла: из Content-Range (bytes N-M/L → L), иначе N + Content-Length
    /// продолжения; неизвестна — null (тогда полноту проверяет только SHA256).
    /// </summary>
    public static long? ExpectedTotal(ResumeAction action, ContentRangeHeaderValue? range, long? contentLength, long requestedFrom)
    {
        if (action != ResumeAction.Append) return contentLength;
        if (range?.HasLength == true) return range.Length;
        return contentLength.HasValue ? requestedFrom + contentLength.Value : null;
    }

    /// <summary>Длина .partial-файла (0, если его нет или он не читается).</summary>
    public static long PartialLength(string partialPath)
    {
        try
        {
            var info = new FileInfo(partialPath);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }
}

/// <summary>
/// Источник принял соединение, но перестал передавать данные дольше порога
/// (детектор зависания). Наследник TimeoutException: для вызывающего кода это всё
/// тот же «источник не ответил вовремя», а журнал может назвать причину точнее.
/// </summary>
internal sealed class DownloadStalledException : TimeoutException
{
    public DownloadStalledException(string message, Exception? inner = null) : base(message, inner) { }
}
