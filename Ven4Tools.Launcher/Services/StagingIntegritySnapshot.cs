using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Helpers;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Слепок содержимого распакованного каталога (staging): относительный путь →
/// размер и SHA256 каждого файла.
///
/// Зачем. SHA256 архива подтверждает ровно архив: распакованные из него файлы
/// лежат в каталоге рядом с папкой установки, а её выбирает пользователь через
/// обычный диалог выбора папки — то есть в общем случае это каталог, доступный
/// на запись любому процессу того же пользователя. Между распаковкой и
/// фиксацией установки (<see cref="TransactionalDirectoryInstaller.Install"/>)
/// лаунчер может ждать СКОЛЬ УГОДНО ДОЛГО: там диалог «клиент запущен, закрыть?»
/// и ожидание фактического закрытия клиента. Всё это время staging никем не
/// защищён — подменённый в нём файл стал бы установленным клиентом, и проверка
/// хеша архива этого не заметила бы (TOCTOU).
///
/// Слепок снимается СРАЗУ после распаковки (пока никакого ожидания ещё не было)
/// и сверяется непосредственно перед вызовом Install. Полностью «залочить»
/// каталог целиком Windows не позволяет, поэтому окно не исчезает, а
/// сокращается с произвольного времени ожидания пользователя до промежутка
/// между сверкой и переносом каталога — двух соседних операций.
/// </summary>
internal sealed class StagingIntegritySnapshot
{
    private readonly IReadOnlyDictionary<string, Fingerprint> _files;

    private StagingIntegritySnapshot(IReadOnlyDictionary<string, Fingerprint> files) => _files = files;

    /// <summary>Сколько файлов попало в слепок — для журнала лаунчера.</summary>
    public int FileCount => _files.Count;

    private readonly record struct Fingerprint(long Size, string Sha256);

    /// <summary>
    /// Считает размер и SHA256 каждого файла каталога (рекурсивно). Исключений
    /// «служебных» имён здесь намеренно нет: staging только что создан
    /// распаковщиком, и любой лишний файл в нём — уже расхождение.
    /// </summary>
    public static async Task<StagingIntegritySnapshot> CaptureAsync(
        string directoryPath, CancellationToken cancellationToken)
    {
        string root = NormalizeRoot(directoryPath);
        var files = new Dictionary<string, Fingerprint>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            files[ToRelative(root, file)] = await FingerprintAsync(file, cancellationToken).ConfigureAwait(false);
        }

        return new StagingIntegritySnapshot(files);
    }

    /// <summary>
    /// Сверяет каталог со слепком. Возвращает null, если содержимое не изменилось,
    /// иначе — описание ПЕРВОГО найденного расхождения для журнала и сообщения
    /// пользователю (перечислять все смысла нет: установка отменяется на первом же).
    /// </summary>
    public async Task<string?> FindDifferenceAsync(
        string directoryPath, CancellationToken cancellationToken)
    {
        string root;
        try
        {
            root = NormalizeRoot(directoryPath);
        }
        catch (DirectoryNotFoundException)
        {
            return "каталог распаковки исчез";
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relative = ToRelative(root, file);
            if (!_files.TryGetValue(relative, out Fingerprint expected))
            {
                return $"после распаковки появился посторонний файл: {relative}";
            }

            seen.Add(relative);
            var actual = await FingerprintAsync(file, cancellationToken).ConfigureAwait(false);
            if (actual.Size != expected.Size ||
                !string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return $"после распаковки изменился файл: {relative}";
            }
        }

        foreach (string relative in _files.Keys)
        {
            if (!seen.Contains(relative))
            {
                return $"после распаковки исчез файл: {relative}";
            }
        }

        return null;
    }

    private static async Task<Fingerprint> FingerprintAsync(string path, CancellationToken cancellationToken)
    {
        // Размер берётся из того же обхода, что и хеш: он не заменяет хеш, а
        // делает расхождение «файл подменён на файл другой длины» видимым даже
        // при совпавшем (например, обрезанном до нуля и снова дописанном) чтении.
        long size = new FileInfo(path).Length;
        string hash = await FileHashHelper.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        return new Fingerprint(size, hash);
    }

    private static string NormalizeRoot(string directoryPath)
    {
        string root = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Каталог распаковки не найден: {root}");
        }
        return root;
    }

    private static string ToRelative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');
}
