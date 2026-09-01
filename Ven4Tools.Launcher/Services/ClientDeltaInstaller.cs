using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Скачанные файлы дельты: набор для передачи в
/// <see cref="TransactionalDirectoryInstaller.InstallPartial"/> вместе с открытыми
/// защитными хендлами на каждый файл. Хендлы (FileShare.Read) открыты с момента
/// проверки SHA256 каждого файла и держатся до конца установки — ровно та же
/// защита от TOCTOU, что и у полного пути с zip-архивом. Dispose закрывает их все
/// (вызывающий код обязан обернуть весь блок «скачать → установить» в using).
/// </summary>
internal sealed class DeltaDownloadSet : IDisposable
{
    private readonly List<IDisposable> _guards;

    public DeltaDownloadSet(IReadOnlyList<PartialFileUpdate> updates, List<IDisposable> guards)
    {
        Updates = updates;
        _guards = guards;
    }

    public IReadOnlyList<PartialFileUpdate> Updates { get; }

    public void Dispose()
    {
        foreach (var guard in _guards)
        {
            try { guard.Dispose(); } catch { /* закрытие хендла не должно ронять установку */ }
        }
        _guards.Clear();
    }
}

/// <summary>
/// Оркестрация блочного (дельта-) обновления клиента: скачивает только изменившиеся
/// файлы публикации и применяет их поверх установленной версии одной транзакцией.
///
/// Источники для отдельного файла: CDN-домен → CDN прямой IP → зеркало на хостинге.
/// GitHub в цепочке отсутствует намеренно: в GitHub Releases лежит только цельный
/// zip-архив релиза, отдельных файлов публикации там нет — поэтому GitHub остаётся
/// источником исключительно полного пути обновления.
///
/// Класс НЕ пытается чинить себя сам: любая ошибка (недоступный файл, несовпавший
/// SHA256, сбой применения) выбрасывается наружу, а вызывающий код откатывается на
/// обычную полную загрузку архива. Это сознательный компромисс — полный путь уже
/// зрелый и надёжный, а вторая ветка «частично применённая дельта пытается
/// доисправиться» была бы самой опасной частью всей функции.
/// </summary>
internal sealed class ClientDeltaInstaller
{
    private readonly FallbackDownloader _downloader;
    private readonly TransactionalDirectoryInstaller _installer;
    private readonly InstalledManifestStore _store;

    public ClientDeltaInstaller()
        : this(new FallbackDownloader(), new TransactionalDirectoryInstaller(), new InstalledManifestStore())
    {
    }

    internal ClientDeltaInstaller(
        FallbackDownloader downloader,
        TransactionalDirectoryInstaller installer,
        InstalledManifestStore store)
    {
        _downloader = downloader;
        _installer = installer;
        _store = store;
    }

    /// <summary>
    /// Скачивает все файлы плана во временный каталог, проверяя SHA256 каждого
    /// (fail-closed: файл без совпавшего хеша не принимается ни от одного источника).
    /// </summary>
    public async Task<DeltaDownloadSet> DownloadChangedFilesAsync(
        ClientDeltaPlan plan,
        string filesBaseUrl,
        string? filesMirrorBaseUrl,
        string workingDirectory,
        DownloadSource preference,
        HttpClient normalClient,
        HttpClient ipPinnedClient,
        Action<int, int>? fileProgress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var updates = new List<PartialFileUpdate>();
        var guards = new List<IDisposable>();
        var set = new DeltaDownloadSet(updates, guards);

        try
        {
            Directory.CreateDirectory(workingDirectory);

            for (int i = 0; i < plan.ToDownload.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = plan.ToDownload[i];
                string relative = entry.Path!;
                string cdnUrl = CombineUrl(filesBaseUrl, relative);
                string? mirrorUrl = filesMirrorBaseUrl == null ? null : CombineUrl(filesMirrorBaseUrl, relative);

                var candidates = FallbackDownloader.BuildCandidates(
                    preference,
                    cdnUrl,
                    mirrorUrl,
                    // GitHub не раздаёт отдельные файлы публикации — см. описание класса.
                    githubUrl: null,
                    normalClient,
                    ipPinnedClient);

                if (candidates.Count == 0)
                {
                    throw new InvalidOperationException($"Нет доверенных источников для файла {relative}.");
                }

                // Временный файл с плоским именем: относительные пути публикации могут
                // содержать подкаталоги, а раскладывать их во временном каталоге незачем —
                // куда файл ляжет, определяет RelativePath при установке.
                string targetPath = System.IO.Path.Combine(workingDirectory, $"{i:D5}.bin");

                fileProgress?.Invoke(i + 1, plan.ToDownload.Count);
                var result = await _downloader.DownloadAsync(
                    candidates,
                    targetPath,
                    cancellationToken,
                    entry.Sha256).ConfigureAwait(false);

                guards.Add(result);
                updates.Add(new PartialFileUpdate(relative, targetPath));
            }

            log?.Invoke($"📥 Скачано изменившихся файлов: {updates.Count}");
            return set;
        }
        catch
        {
            set.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Применяет скачанный набор одной транзакцией и, только после её успеха,
    /// записывает новый локальный манифест установленной версии. Порядок важен:
    /// кэш состава обязан описывать то, что реально лежит на диске, поэтому он
    /// пишется последним и лишь по факту успешной фиксации файлов.
    /// </summary>
    public void Apply(
        ClientFileManifest remote,
        ClientDeltaPlan plan,
        DeltaDownloadSet downloaded,
        string clientPath,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        _installer.InstallPartial(clientPath, downloaded.Updates, plan.ToDelete, cancellationToken);

        if (!_store.Save(remote))
        {
            // Файлы обновлены успешно — это не повод считать обновление неудачным.
            // Но без кэша следующее обновление пойдёт полным путём, и об этом стоит
            // сказать в журнале, чтобы «почему дельта не сработала» не выяснялось вслепую.
            log?.Invoke("⚠️ Не удалось сохранить локальный манифест — следующее обновление будет полным");
        }
    }

    /// <summary>
    /// Базовый URL + относительный путь файла. Каждый сегмент экранируется отдельно:
    /// пробелы и не-ASCII в именах файлов публикации иначе дали бы некорректный URL,
    /// а экранирование пути целиком съело бы разделители '/'.
    /// Чистая функция — покрыта unit-тестами.
    /// </summary>
    internal static string CombineUrl(string baseUrl, string relativePath)
    {
        string prefix = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        var segments = relativePath.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }
        return prefix + string.Join('/', segments);
    }
}
