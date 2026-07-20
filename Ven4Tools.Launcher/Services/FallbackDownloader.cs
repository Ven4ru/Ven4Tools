using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Один кандидат на загрузку: URL, транспорт (обычный или IP-pinned HttpClient)
/// и человекочитаемая метка источника для лога. Один и тот же URL может встречаться
/// с разными клиентами (например, CDN-домен обычным клиентом и он же прямым IP).
/// </summary>
internal readonly record struct DownloadCandidate(string Url, HttpClient Client, string SourceLabel);

internal sealed class FallbackDownloader
{
    /// <summary>
    /// Скачивает файл, перебирая источники по порядку до первого успеха. По каждому
    /// кандидату — проверка доверенного хоста (до и после редиректа), .partial-файл и
    /// обязательная сверка SHA256. Возвращает SourceLabel фактически сработавшего
    /// кандидата (для лога «скачано через …»).
    ///
    /// Первый кандидат — как прежний «основной»: ошибка валидации его хоста
    /// (InvalidOperationException) не проглатывается. Для остальных кандидатов
    /// недоверенный хост/сетевая ошибка проглатываются и идёт переход к следующему.
    /// Отмена вызывающей стороной (её токен) — сразу пробрасывается, следующие
    /// кандидаты не пробуются.
    /// </summary>
    public async Task<string> DownloadAsync(
        IReadOnlyList<DownloadCandidate> candidates,
        string targetPath,
        CancellationToken cancellationToken,
        string? expectedSha256 = null,
        Action<long, long?>? progress = null,
        Action<string>? switchingTo = null)
    {
        if (candidates == null || candidates.Count == 0)
        {
            throw new InvalidOperationException("Список источников загрузки пуст.");
        }

        Exception? lastError = null;
        bool anyAttempted = false;

        for (int i = 0; i < candidates.Count; i++)
        {
            DownloadCandidate candidate = candidates[i];
            bool isFirst = i == 0;

            if (!DownloadValidator.IsAllowedDownloadHost(candidate.Url))
            {
                // Первый (основной) источник с недоверенным хостом — жёсткая ошибка,
                // как в прежней двухисточниковой версии. Недоверенный резерв — пропуск.
                if (isFirst)
                {
                    throw new InvalidOperationException("Основной URL загрузки не входит в список доверенных.");
                }
                continue;
            }

            try
            {
                if (anyAttempted)
                {
                    switchingTo?.Invoke(candidate.SourceLabel);
                }

                await DownloadSingleAsync(
                    candidate.Client,
                    candidate.Url,
                    targetPath,
                    cancellationToken,
                    expectedSha256,
                    progress);
                return candidate.SourceLabel;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Отмена именно вызывающей стороной (наш токен) — не пробуем следующие
                // источники, операция в любом случае должна остановиться.
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                anyAttempted = true;
            }
        }

        // Ни один источник не сработал — пробрасываем последнюю реальную ошибку
        // (сохраняя её тип: вызывающий код и тесты различают HttpRequestException/
        // InvalidOperationException), либо сообщаем об отсутствии доверенных источников.
        if (lastError != null)
        {
            ExceptionDispatchInfo.Capture(lastError).Throw();
        }
        throw new InvalidOperationException("Нет доступных доверенных источников загрузки.");
    }

    /// <summary>
    /// Строит упорядоченный список кандидатов на загрузку из доступных ссылок.
    /// Порядок по умолчанию (Auto): CDN(домен) → CDN(прямой IP) → Хостинг(зеркало) →
    /// GitHub. Явный выбор источника переставляет его в начало — остальные остаются
    /// резервом позади в том же относительном порядке (фоллбэк не отключается).
    /// Кандидаты с пустым/null URL пропускаются; одинаковые пары (Url, Client) не
    /// дублируются. Чистая функция без сети — покрыта unit-тестами.
    /// </summary>
    internal static List<DownloadCandidate> BuildCandidates(
        DownloadSource preference,
        string? cdnUrl,
        string? cdnMirrorHostingUrl,
        string? githubUrl,
        HttpClient normalClient,
        HttpClient ipPinnedClient)
    {
        // Базовый порядок Auto. «CDN (прямой IP)» — та же ссылка cdnUrl, но другой
        // транспорт (ipPinnedClient): это не отдельный URL, а отдельная точка TCP-
        // подключения для той же ссылки.
        var ordered = new List<(DownloadSource Source, string? Url, HttpClient Client, string Label)>
        {
            (DownloadSource.CdnDomain,     cdnUrl,              normalClient,   "CDN"),
            (DownloadSource.CdnDirectIp,   cdnUrl,              ipPinnedClient, "CDN (прямой IP)"),
            (DownloadSource.HostingMirror, cdnMirrorHostingUrl, normalClient,   "Хостинг"),
            (DownloadSource.Github,        githubUrl,           normalClient,   "GitHub"),
        };

        if (preference != DownloadSource.Auto)
        {
            int idx = ordered.FindIndex(c => c.Source == preference);
            if (idx > 0)
            {
                var chosen = ordered[idx];
                ordered.RemoveAt(idx);
                ordered.Insert(0, chosen);
            }
        }

        var result = new List<DownloadCandidate>();
        foreach (var c in ordered)
        {
            if (string.IsNullOrWhiteSpace(c.Url)) continue;
            if (result.Any(r =>
                    string.Equals(r.Url, c.Url, StringComparison.OrdinalIgnoreCase) &&
                    ReferenceEquals(r.Client, c.Client)))
                continue;
            result.Add(new DownloadCandidate(c.Url!, c.Client, c.Label));
        }
        return result;
    }

    private static async Task DownloadSingleAsync(
        HttpClient client,
        string url,
        string targetPath,
        CancellationToken cancellationToken,
        string? expectedSha256,
        Action<long, long?>? progress)
    {
        string partialPath = targetPath + ".partial";
        TryDelete(partialPath);

        try
        {
            using HttpResponseMessage response = await client.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            if (!DownloadValidator.IsAllowedDownloadHostAfterRedirect(response))
            {
                throw new InvalidOperationException("Редирект загрузки ведёт на недоверенный домен.");
            }

            long? totalBytes = response.Content.Headers.ContentLength;
            long bytesRead = 0;
            var buffer = new byte[81920];

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(
                partialPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                int count;
                while ((count = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    bytesRead += count;
                    progress?.Invoke(bytesRead, totalBytes);
                }

                await destination.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (totalBytes is > 0 && bytesRead != totalBytes.Value)
            {
                throw new IOException(
                    $"Загрузка неполная: получено {bytesRead} из {totalBytes.Value} байт.");
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                string actualSha256 = await ComputeSha256Async(partialPath, cancellationToken);
                if (!string.Equals(
                    actualSha256,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Контрольная сумма загруженного файла не совпала.");
                }
            }

            File.Move(partialPath, targetPath, overwrite: true);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Основная операция сообщит исходную ошибку; очистка не должна её скрывать.
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
