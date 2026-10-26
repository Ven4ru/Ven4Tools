using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Один кандидат на загрузку: URL, транспорт (обычный или IP-pinned HttpClient)
/// и человекочитаемая метка источника для лога. Один и тот же URL может встречаться
/// с разными клиентами (например, CDN-домен обычным клиентом и он же прямым IP).
/// </summary>
internal readonly record struct DownloadCandidate(string Url, HttpClient Client, string SourceLabel);

/// <summary>
/// Результат успешной загрузки: метка сработавшего источника + открытый
/// FileShare.Read-хендл на итоговый файл. Хендл открывается сразу после
/// перемещения файла на место и ДО сверки SHA256, а сама сверка читает из него —
/// значит проверен ровно тот файл, который держит хендл. Держать его в вызывающем
/// коде (using) до конца использования файла (запуск/распаковка), иначе между
/// проверкой хеша и использованием откроется окно TOCTOU для подмены файла другим
/// процессом того же пользователя. Dispose закрывает хендл.
/// </summary>
internal sealed class DownloadResult : IDisposable
{
    public string SourceLabel { get; }
    public FileStream Guard { get; }

    public DownloadResult(string sourceLabel, FileStream guard)
    {
        SourceLabel = sourceLabel;
        Guard = guard;
    }

    public void Dispose() => Guard.Dispose();
}

internal sealed class FallbackDownloader
{
    /// <summary>
    /// Порог детектора зависания: столько времени без единого байта тела ответа —
    /// и источник считается зависшим. Порог именно по ОТСУТСТВИЮ данных, а не по
    /// скорости: медленный, но живой канал (байты идут, пусть и по чуть-чуть)
    /// зависшим не считается никогда. Типичный случай, ради которого он нужен, —
    /// провайдер пропускает первые килобайты с адреса и дальше молчит, не разрывая
    /// соединения: раньше такой источник ждали минуту, прежде чем идти к следующему.
    /// </summary>
    internal static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Прежний простой между чтениями — когда детектор выключен флагом.</summary>
    internal static readonly TimeSpan LegacyIdleTimeout = TimeSpan.FromSeconds(60);

    // Таймаут на соединение и заголовки — детектор его не меняет (см. SendAsync).
    private static readonly TimeSpan DefaultHeadersTimeout = TimeSpan.FromSeconds(30);

    // Сколько раз можно пройти по списку источников, если каждый круг продвигал
    // загрузку (докачка с того же байта). Круг без единого нового байта — конец:
    // крутить источники, которые ничего не отдают, бессмысленно. Общую длительность
    // всё равно ограничивает бюджет операции у вызывающего кода.
    private const int MaxPassesWithResume = 3;

    private readonly bool _stallDetection;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _headersTimeout;

    /// <summary>
    /// Детектор зависания и докачка — по флагу из подписанного манифеста
    /// (<see cref="LauncherFeatureFlags.StallDetectionEnabled"/>, по умолчанию ВКЛ).
    /// Флаг читается при создании загрузчика: идущая загрузка поведение не меняет.
    /// </summary>
    public FallbackDownloader()
        : this(LauncherFeatureFlags.StallDetectionEnabled)
    {
    }

    /// <summary>Явный режим и пороги — для тестов (ждать в них секунды незачем).</summary>
    internal FallbackDownloader(bool stallDetection, TimeSpan? idleTimeout = null, TimeSpan? headersTimeout = null)
    {
        _stallDetection = stallDetection;
        _idleTimeout = idleTimeout ?? (stallDetection ? StallTimeout : LegacyIdleTimeout);
        _headersTimeout = headersTimeout ?? DefaultHeadersTimeout;
    }

    /// <summary>
    /// Скачивает файл, перебирая источники по порядку до первого успеха. По каждому
    /// кандидату — проверка доверенного хоста (до и после редиректа), .partial-файл и
    /// обязательная сверка SHA256. Возвращает результат с SourceLabel фактически
    /// сработавшего кандидата (для лога «скачано через …») и открытым защитным
    /// хендлом файла (см. <see cref="DownloadResult"/>) — вызывающий код обязан
    /// держать его открытым (using) до конца использования файла.
    ///
    /// Первый кандидат — как прежний «основной»: ошибка валидации его хоста
    /// (InvalidOperationException) не проглатывается. Для остальных кандидатов
    /// недоверенный хост/сетевая ошибка проглатываются и идёт переход к следующему.
    /// Отмена вызывающей стороной (её токен) — сразу пробрасывается, следующие
    /// кандидаты не пробуются.
    ///
    /// Детектор зависания (флаг манифеста, по умолчанию ВКЛ): источник, переставший
    /// отдавать данные на <see cref="StallTimeout"/>, бросается, и загрузка идёт
    /// дальше со СЛЕДУЮЩЕГО источника с того же байта (Range: bytes=N- в тот же
    /// .partial). Докачка — только при известном SHA256: склейку кусков разных файлов
    /// отсекает итоговая сверка. Без хеша (компоненты Microsoft, проверяемые
    /// Authenticode) каждый источник по-прежнему качается с нуля.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        IReadOnlyList<DownloadCandidate> candidates,
        string targetPath,
        CancellationToken cancellationToken,
        string? expectedSha256 = null,
        Action<long, long?>? progress = null,
        Action<string, string>? switchingTo = null)
    {
        if (candidates == null || candidates.Count == 0)
        {
            throw new InvalidOperationException("Список источников загрузки пуст.");
        }

        string partialPath = targetPath + ".partial";
        // Остаток прошлой попытки под тем же именем к этой загрузке отношения не
        // имеет — докачка только в пределах одного вызова.
        TryDelete(partialPath);

        bool resume = _stallDetection && expectedSha256 != null;
        int maxPasses = resume ? MaxPassesWithResume : 1;

        Exception? lastError = null;
        bool anyAttempted = false;

        try
        {
            for (int pass = 0; pass < maxPasses; pass++)
            {
                long keptAtPassStart = DownloadResume.PartialLength(partialPath);

                for (int i = 0; i < candidates.Count; i++)
                {
                    DownloadCandidate candidate = candidates[i];
                    bool isFirst = pass == 0 && i == 0;

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
                            // Причина отказа предыдущего кандидата передаётся отдельно:
                            // «не прошёл проверку целостности» и «недоступен» — разные
                            // события, и первое нельзя показывать как проблему со связью.
                            switchingTo?.Invoke(candidate.SourceLabel, DescribeFailure(lastError));
                        }

                        // Защитный хендл открывается внутри DownloadSingleAsync — ДО вычисления
                        // SHA256 и из него же: проверенным оказывается ровно тот файл, который
                        // вызывающий код потом использует, окна между проверкой и использованием
                        // не остаётся вовсе.
                        FileStream guard = await DownloadSingleAsync(
                            candidate.Client,
                            candidate.Url,
                            targetPath,
                            partialPath,
                            resume,
                            cancellationToken,
                            expectedSha256,
                            progress).ConfigureAwait(false);

                        return new DownloadResult(candidate.SourceLabel, guard);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        // Отмена именно вызывающей стороной (наш токен) — не пробуем следующие
                        // источники, операция в любом случае должна остановиться.
                        throw;
                    }
                    catch (OperationCanceledException ex)
                    {
                        // Токен вызывающего НЕ отменён — значит, это таймаут источника
                        // (заголовки). Наружу он не должен уйти как
                        // OperationCanceledException: вызывающие трактуют этот тип как
                        // «пользователь нажал Отмена» — дельта не переходила на полную
                        // загрузку, а сбой всех источников показывался как «Отменено».
                        lastError = new TimeoutException("Источник не ответил вовремя", ex);
                        anyAttempted = true;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        anyAttempted = true;
                    }
                }

                // Следующий круг — только если этот продвинул докачку: в .partial
                // теперь больше, чем было в начале круга. Тогда источники живы, просто
                // рвут или подвешивают соединение. Источник, отдающий не тот файл,
                // .partial не продвигает (несовпадение хеша его удаляет), и качать его
                // по кругу снова незачем.
                if (DownloadResume.PartialLength(partialPath) <= keptAtPassStart)
                {
                    break;
                }
            }
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }

        TryDelete(partialPath);

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

    /// <summary>
    /// Скачивает один источник и возвращает ОТКРЫТЫЙ защитный хендл итогового файла
    /// (FileShare.Read). Хендл открывается сразу после перемещения .partial на место
    /// и ДО сверки SHA256, а сама сверка читает из него же — поэтому подмена файла
    /// между проверкой и использованием невозможна: с момента открытия хендла никто
    /// не может ни записать в файл, ни удалить, ни переименовать его. Подмена ДО
    /// открытия хендла безвредна — она попадёт под тот же хеш и будет отвергнута.
    /// Идиома та же, что в клиентской прямой загрузке (InstallationService).
    ///
    /// resume: .partial от прошлого источника продолжается запросом с Range. Сетевой
    /// сбой посреди тела (в том числе зависание) .partial при этом НЕ удаляет —
    /// следующий источник продолжит с того же байта. Несовпавший хеш удаляет всё.
    /// </summary>
    private async Task<FileStream> DownloadSingleAsync(
        HttpClient client,
        string url,
        string targetPath,
        string partialPath,
        bool resume,
        CancellationToken cancellationToken,
        string? expectedSha256,
        Action<long, long?>? progress)
    {
        if (!resume)
        {
            TryDelete(partialPath);
        }

        FileStream? guard = null;
        bool committed = false;

        try
        {
            long resumeFrom = resume ? DownloadResume.PartialLength(partialPath) : 0;

            // Ответ на докачку, которому нельзя верить (416, 206 не с того байта),
            // означает «начать этот источник с нуля»: .partial выбрасывается, запрос
            // повторяется без Range. Не больше одного повтора.
            HttpResponseMessage response = await SendAsync(client, url, resumeFrom, cancellationToken).ConfigureAwait(false);
            ResumeAction action = ResumeAction.RestartFromZero;
            // Ошибочный ответ (503 и т.п.) — обычный отказ источника: уже скачанное
            // остаётся следующему. Разбирается только успешный ответ и 416.
            if (resumeFrom > 0 &&
                (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable))
            {
                action = DownloadResume.Classify(response.StatusCode, response.Content.Headers.ContentRange, resumeFrom);
                if (action == ResumeAction.Reject)
                {
                    response.Dispose();
                    TryDelete(partialPath);
                    resumeFrom = 0;
                    action = ResumeAction.RestartFromZero;
                    response = await SendAsync(client, url, 0, cancellationToken).ConfigureAwait(false);
                }
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                if (!DownloadValidator.IsAllowedDownloadHostAfterRedirect(response))
                {
                    throw new InvalidOperationException("Редирект загрузки ведёт на недоверенный домен.");
                }

                // 200 на докачку — источник отдал файл целиком: пишем с нуля из этого
                // же ответа, уже скачанное не годится.
                long offset = action == ResumeAction.Append ? resumeFrom : 0;
                long? totalBytes = DownloadResume.ExpectedTotal(
                    action, response.Content.Headers.ContentRange, response.Content.Headers.ContentLength, resumeFrom);
                long bytesRead = 0;
                var buffer = new byte[81920];

                // Sliding-таймаут простоя между чтениями — без него сервер, переставший
                // отвечать после заголовков, вешал бы загрузку до внешнего таймаута
                // вызывающего кода. Перезаводится после КАЖДОГО прочитанного блока, то есть
                // срабатывает только тогда, когда данных нет совсем (детектор зависания).
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idleCts.CancelAfter(_idleTimeout);

                // ConfigureAwait(false) на весь горячий цикл: на архиве в сотни МБ это
                // тысячи итераций по 80 КБ, каждая с двумя await — без этого каждая из них
                // маршалилась через WPF SynchronizationContext лаунчера, хотя сам цикл
                // никакой UI не трогает (progress?.Invoke ниже сам маршалит на UI-поток
                // через Dispatcher — см. вызывающий код в MainWindow.Download.cs).
                var progressStopwatch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var destination = new FileStream(
                        partialPath,
                        offset > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 81920,
                        useAsync: true);

                    // .partial мог измениться между замером и открытием — докачка
                    // поверх файла другой длины склеила бы мусор.
                    if (offset > 0 && destination.Length != offset)
                    {
                        throw new IntegrityCheckFailedException("Файл частичной загрузки изменился — докачка невозможна.");
                    }

                    int count;
                    while ((count = await source.ReadAsync(buffer.AsMemory(), idleCts.Token).ConfigureAwait(false)) > 0)
                    {
                        idleCts.CancelAfter(_idleTimeout);
                        await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                        bytesRead += count;

                        // Троттлинг: раньше progress?.Invoke дёргался на каждые 80 КБ (~2300
                        // раз на 180 МБ архиве вместо ~100 нужных для процентного бара).
                        // Финальный вызов — без троттла, чтобы 100% гарантированно долетело.
                        if (progressStopwatch.ElapsedMilliseconds >= 100 || offset + bytesRead == totalBytes)
                        {
                            progress?.Invoke(offset + bytesRead, totalBytes);
                            progressStopwatch.Restart();
                        }
                    }

                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Сработал таймаут простоя, а не отмена пользователем. Уже принятые
                    // байты остаются в .partial (поток записи закрыт штатно и сброшен на
                    // диск) — следующий источник продолжит с них.
                    throw new DownloadStalledException(
                        $"Источник не передавал данные {_idleTimeout.TotalSeconds:0} с (получено {offset + bytesRead} байт).", ex);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (totalBytes is > 0 && offset + bytesRead != totalBytes.Value)
                {
                    // Больше ожидаемого — файл заведомо не тот, докачивать нечего.
                    if (offset + bytesRead > totalBytes.Value)
                    {
                        throw new IntegrityCheckFailedException(
                            $"Получено больше ожидаемого: {offset + bytesRead} из {totalBytes.Value} байт.");
                    }
                    throw new IOException(
                        $"Загрузка неполная: получено {offset + bytesRead} из {totalBytes.Value} байт.");
                }
            }

            File.Move(partialPath, targetPath, overwrite: true);
            committed = true;

            // Защита включается ПЕРВОЙ операцией после перемещения: следующей строкой,
            // без единого await между Move и открытием хендла.
            guard = new FileStream(
                targetPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);

            // null — единственный способ сказать «хеша нет» (компоненты Microsoft,
            // проверяемые Authenticode). Пустую/пробельную строку пропускать нельзя:
            // вызывающий код клиента считает целостность подтверждённой по
            // !IsNullOrEmpty, и строка из пробелов проходила бы вообще без сверки.
            if (expectedSha256 != null)
            {
                string actualSha256 = await Helpers.FileHashHelper
                    .ComputeSha256Async(guard, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(
                    actualSha256,
                    expectedSha256,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new IntegrityCheckFailedException(
                        "Контрольная сумма загруженного файла не совпала.");
                }

                // Хендл отдаётся вызывающему коду — оставляем его в начале файла,
                // а не на позиции конца после хеширования.
                guard.Position = 0;
            }

            return guard;
        }
        catch (Exception ex)
        {
            // Хендл закрывается ДО удаления файла: FileShare.Read не даёт удалить
            // файл, пока он открыт.
            guard?.Dispose();
            // Сетевой сбой посреди тела при докачке — .partial оставляем следующему
            // источнику. Всё остальное (отмена, недоверенный редирект, несовпадение
            // данных, режим без докачки) убирает его, как раньше.
            if (!(resume && !committed && IsResumableFailure(ex)))
            {
                TryDelete(partialPath);
            }
            // Файл уже переехал на итоговое место, но не прошёл проверку — оставлять
            // непроверенное содержимое под целевым именем нельзя. До Move целевой файл
            // не трогаем: он мог существовать до нас и к этой попытке отношения не имеет.
            if (committed) TryDelete(targetPath);
            throw;
        }
    }

    /// <summary>
    /// Сбой, после которого уже скачанное годится для продолжения с другого
    /// источника: зависание, обрыв или неполный ответ, сетевая ошибка. Несовпадение
    /// данных (<see cref="IntegrityCheckFailedException"/>, наследник IOException) —
    /// не годится. Чистая функция — покрыта unit-тестами.
    /// </summary>
    internal static bool IsResumableFailure(Exception ex) =>
        ex is not IntegrityCheckFailedException &&
        ex is DownloadStalledException or IOException or HttpRequestException;

    /// <summary>
    /// GET с ожиданием только заголовков. Таймаут на соединение и заголовки — свой,
    /// отдельный от простоя тела: клиенты клиентских загрузок создаются с
    /// Timeout.InfiniteTimeSpan (MainWindow._httpClient и IpPinnedHttpClientFactory),
    /// поэтому источник, принявший соединение и замолчавший до отправки заголовков,
    /// висел бы до ручной отмены — и съедал бы весь перебор источников.
    /// from больше нуля — запрос продолжения (Range: bytes=from-).
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string url, long from, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (from > 0)
        {
            request.Headers.Range = new RangeHeaderValue(from, null);
        }

        using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headersCts.CancelAfter(_headersTimeout);
        return await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            headersCts.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Короткое описание причины отказа источника для журнала лаунчера.
    /// </summary>
    private static string DescribeFailure(Exception? error) => error switch
    {
        IntegrityCheckFailedException => "не прошёл проверку целостности",
        DownloadStalledException => "перестал передавать данные",
        _ => "недоступен",
    };

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

}
