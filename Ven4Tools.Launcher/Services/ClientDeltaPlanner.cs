using System;
using System.Collections.Generic;
using System.Linq;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// План дельта-обновления: что скачать, что удалить, что уже совпадает.
/// Либо вердикт «дельта не годится, качаем архив целиком» с причиной для журнала.
/// </summary>
internal sealed class ClientDeltaPlan
{
    private ClientDeltaPlan(
        bool fullDownloadRecommended,
        string reason,
        IReadOnlyList<ClientManifestFileEntry> toDownload,
        IReadOnlyList<string> toDelete,
        IReadOnlyList<ClientManifestFileEntry> unchanged)
    {
        FullDownloadRecommended = fullDownloadRecommended;
        Reason = reason;
        ToDownload = toDownload;
        ToDelete = toDelete;
        Unchanged = unchanged;
    }

    /// <summary>Дельта невыгодна или ненадёжна — вызывающий обязан идти полным путём.</summary>
    public bool FullDownloadRecommended { get; }

    /// <summary>Человекочитаемая причина вердикта — попадает в журнал лаунчера.</summary>
    public string Reason { get; }

    /// <summary>Файлы нового манифеста, которых нет локально или у которых другой хеш.</summary>
    public IReadOnlyList<ClientManifestFileEntry> ToDownload { get; }

    /// <summary>Относительные пути, пропавшие из новой версии — их нужно удалить.</summary>
    public IReadOnlyList<string> ToDelete { get; }

    /// <summary>Файлы, совпавшие по пути и хешу — не трогаем вообще.</summary>
    public IReadOnlyList<ClientManifestFileEntry> Unchanged { get; }

    /// <summary>Сколько байт придётся скачать по этому плану.</summary>
    public long DownloadBytes => ToDownload.Sum(e => e.Size);

    internal static ClientDeltaPlan Full(string reason) =>
        new(true, reason, Array.Empty<ClientManifestFileEntry>(), Array.Empty<string>(), Array.Empty<ClientManifestFileEntry>());

    internal static ClientDeltaPlan Delta(
        IReadOnlyList<ClientManifestFileEntry> toDownload,
        IReadOnlyList<string> toDelete,
        IReadOnlyList<ClientManifestFileEntry> unchanged,
        string reason) =>
        new(false, reason, toDownload, toDelete, unchanged);
}

/// <summary>
/// Сравнение файлового манифеста новой версии с манифестом установленной —
/// ядро блочного (дельта-) обновления клиента. Чистая функция: ни сети, ни диска,
/// ни времени — поэтому полностью покрывается unit-тестами, а вся ненадёжная
/// часть (загрузка, запись файлов) вынесена в ClientDeltaInstaller.
///
/// Сервис намеренно оставлен самостоятельным, а не приватной деталью вызывающего
/// кода: тот же план нужен будущей функции «проверить и починить установленный
/// клиент» (она сравнивает манифест с реально посчитанными хешами файлов на диске
/// и переиспользует ровно этот же алгоритм).
/// </summary>
internal static class ClientDeltaPlanner
{
    /// <summary>
    /// Порог выгодности дельты: если по количеству файлов совпадает меньше половины
    /// нового манифеста, дельта не даёт выигрыша (качать почти всё пофайлово медленнее
    /// и хрупче одного zip). Считаем именно реальную выгоду, а не «сколько релизов
    /// назад была установлена версия» — число релизов ничего не говорит об объёме
    /// изменений, а доля совпавших файлов говорит напрямую.
    /// </summary>
    public const double MinimumUnchangedShare = 0.5;

    /// <summary>
    /// Сравнение путей — OrdinalIgnoreCase. Пути в манифесте регистрозависимы
    /// по формату, но кладутся они на файловую систему Windows, которая регистр
    /// не различает: «Ven4Tools.dll» и «ven4tools.dll» — это ОДИН файл на диске.
    /// Ordinal-сравнение считало бы их разными и породило бы план, где один и тот
    /// же файл одновременно скачивается и удаляется.
    /// </summary>
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static ClientDeltaPlan Plan(ClientFileManifest? remote, ClientFileManifest? local)
    {
        if (remote?.Files == null || remote.Files.Count == 0)
        {
            return ClientDeltaPlan.Full("манифест новой версии пуст или не разобран");
        }

        // Манифест из сети превращается в пути на диске — некорректная запись
        // делает непригодным весь план целиком (частично применённая дельта хуже
        // честной полной загрузки).
        foreach (var entry in remote.Files)
        {
            if (!ManifestPathGuard.IsSafeRelativePath(entry.Path))
            {
                return ClientDeltaPlan.Full($"недопустимый путь в манифесте новой версии: {entry.Path}");
            }
            if (!DownloadValidator.IsValidSha256(entry.Sha256))
            {
                return ClientDeltaPlan.Full($"некорректный SHA256 в манифесте новой версии: {entry.Path}");
            }
            if (entry.Size < 0)
            {
                return ClientDeltaPlan.Full($"некорректный размер файла в манифесте новой версии: {entry.Path}");
            }
        }

        // Дубликаты путей (в т.ч. отличающиеся только регистром) означают, что
        // манифест сам себе противоречит — на диск такое лечь не может.
        var remoteByPath = new Dictionary<string, ClientManifestFileEntry>(PathComparer);
        foreach (var entry in remote.Files)
        {
            if (!remoteByPath.TryAdd(entry.Path!, entry))
            {
                return ClientDeltaPlan.Full($"путь повторяется в манифесте новой версии: {entry.Path}");
            }
        }

        if (local?.Files == null || local.Files.Count == 0)
        {
            // Нет подтверждённого состава установленной версии — сравнивать не с чем.
            return ClientDeltaPlan.Full("нет локального манифеста установленной версии");
        }

        var localByPath = new Dictionary<string, string>(PathComparer);
        foreach (var entry in local.Files)
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Sha256)) continue;
            localByPath[entry.Path!] = entry.Sha256!;
        }

        var toDownload = new List<ClientManifestFileEntry>();
        var unchanged = new List<ClientManifestFileEntry>();
        foreach (var entry in remote.Files)
        {
            if (localByPath.TryGetValue(entry.Path!, out string? localHash) &&
                string.Equals(localHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                unchanged.Add(entry);
            }
            else
            {
                toDownload.Add(entry);
            }
        }

        double unchangedShare = (double)unchanged.Count / remote.Files.Count;
        if (unchangedShare < MinimumUnchangedShare)
        {
            return ClientDeltaPlan.Full(
                $"совпало лишь {unchanged.Count} из {remote.Files.Count} файлов " +
                $"({unchangedShare * 100:F0}%) — дельта невыгодна");
        }

        var toDelete = new List<string>();
        foreach (var entry in local.Files)
        {
            if (string.IsNullOrWhiteSpace(entry.Path)) continue;
            if (remoteByPath.ContainsKey(entry.Path!)) continue;
            // Удаляем только то, что мы сами когда-то положили и что можем безопасно
            // разрешить в путь внутри папки клиента. Битая запись в локальном кэше —
            // повод пропустить именно её, а не отменять всю дельту: локальный манифест
            // не подписан, его порча не является атакой на целостность новой версии.
            if (!ManifestPathGuard.IsSafeRelativePath(entry.Path)) continue;
            toDelete.Add(entry.Path!);
        }

        // Нечего качать и нечего удалять — установленная версия уже совпадает с
        // манифестом пофайлово. Это валидный дельта-план (мгновенное «обновление»).
        return ClientDeltaPlan.Delta(
            toDownload,
            toDelete,
            unchanged,
            $"к загрузке {toDownload.Count} файлов, к удалению {toDelete.Count}, " +
            $"без изменений {unchanged.Count} из {remote.Files.Count}");
    }
}
