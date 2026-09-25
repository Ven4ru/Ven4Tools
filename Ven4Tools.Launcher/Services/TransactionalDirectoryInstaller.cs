using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Ven4Tools.Launcher.Services;

internal interface IDirectoryOperations
{
    bool Exists(string path);
    void Move(string source, string destination);
    void Delete(string path, bool recursive);
}

/// <summary>
/// Пофайловые операции — отдельный шов (seam) от <see cref="IDirectoryOperations"/>,
/// чтобы частичная установка (<see cref="TransactionalDirectoryInstaller.InstallPartial"/>)
/// тестировалась на симуляции сбоя посреди набора файлов, без реального диска.
/// </summary>
internal interface IFileOperations
{
    bool FileExists(string path);
    void CreateDirectory(string path);
    void CopyFile(string source, string destination, bool overwrite);
    void MoveFile(string source, string destination, bool overwrite);
    void DeleteFile(string path);
}

internal sealed class PhysicalDirectoryOperations : IDirectoryOperations
{
    public bool Exists(string path) => Directory.Exists(path);
    public void Move(string source, string destination) => Directory.Move(source, destination);
    public void Delete(string path, bool recursive) => Directory.Delete(path, recursive);
}

internal sealed class PhysicalFileOperations : IFileOperations
{
    public bool FileExists(string path) => File.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void CopyFile(string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite);
    public void MoveFile(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);
    public void DeleteFile(string path) => File.Delete(path);
}

/// <summary>
/// Один файл, который частичная установка должна положить в папку клиента:
/// путь относительно её корня (в формате манифеста, через '/') и уже скачанный
/// и проверенный по SHA256 исходник во временном каталоге.
/// </summary>
internal readonly record struct PartialFileUpdate(string RelativePath, string SourcePath);

internal sealed class TransactionalDirectoryInstaller
{
    private readonly IDirectoryOperations _directories;
    private readonly IFileOperations _files;

    public TransactionalDirectoryInstaller()
        : this(new PhysicalDirectoryOperations(), new PhysicalFileOperations())
    {
    }

    internal TransactionalDirectoryInstaller(IDirectoryOperations directories)
        : this(directories, new PhysicalFileOperations())
    {
    }

    internal TransactionalDirectoryInstaller(IDirectoryOperations directories, IFileOperations files)
    {
        _directories = directories;
        _files = files;
    }

    public void Install(string stagingPath, string targetPath, CancellationToken cancellationToken)
    {
        string staging = Path.GetFullPath(stagingPath);
        string target = Path.GetFullPath(targetPath);
        ValidatePaths(staging, target);

        string backup = target + $".backup-{Guid.NewGuid():N}";
        bool previousVersionMoved = false;
        bool stagingCommitted = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_directories.Exists(staging))
            {
                throw new DirectoryNotFoundException("Каталог staging не найден.");
            }

            if (_directories.Exists(target))
            {
                _directories.Move(target, backup);
                previousVersionMoved = true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _directories.Move(staging, target);
            stagingCommitted = true;
        }
        catch
        {
            if (stagingCommitted && _directories.Exists(target))
            {
                _directories.Delete(target, recursive: true);
            }

            if (previousVersionMoved && _directories.Exists(backup))
            {
                if (_directories.Exists(target))
                {
                    _directories.Delete(target, recursive: true);
                }

                _directories.Move(backup, target);
            }

            throw;
        }

        // Удаление старой копии — вне транзакции: установка уже зафиксирована (stagingCommitted),
        // и сбой очистки (например, залоченный файл) не повод откатывать удавшееся обновление.
        // Осиротевший ".backup-*" подчистит CleanupStaleInstallArtifacts при следующем запуске.
        if (previousVersionMoved && _directories.Exists(backup))
        {
            try
            {
                _directories.Delete(backup, recursive: true);
            }
            catch
            {
                // не критично — см. комментарий выше
            }
        }
    }

    /// <summary>
    /// Атомарное применение ЧАСТИЧНОГО набора изменений поверх уже установленного
    /// каталога клиента — основа блочного (дельта-) обновления. В отличие от
    /// <see cref="Install"/>, каталог целиком не подменяется: файлы, не вошедшие в
    /// дельту (а это подавляющее большинство публикации), не трогаются вообще.
    ///
    /// Транзакция в три фазы, все временные имена помечены одним идентификатором
    /// операции — чтобы остатки прерванной установки можно было потом опознать
    /// (<see cref="IsTransientArtifactName"/>) и зачистить:
    ///
    /// 1. Подготовка: каждый скачанный файл переносится рядом со своим будущим
    ///    местом под именем «файл.new-{id}». Целевые файлы ещё нетронуты.
    /// 2. Фиксация: для каждого файла существующий оригинал переименовывается в
    ///    «файл.old-{id}» (а НЕ удаляется — это и есть путь отката), затем на его
    ///    место встаёт «.new-{id}». Файлы из <paramref name="deletions"/> тоже лишь
    ///    переименовываются в «.old-{id}».
    /// 3. Уборка — только после успеха всего набора: «.old-{id}» удаляются физически.
    ///
    /// Сбой в фазе 2 откатывает уже применённые файлы в обратном порядке, возвращая
    /// каталог к исходному состоянию: половина новой и половина старой версии в папке
    /// клиента — это неработающая программа, худший из возможных исходов.
    /// </summary>
    public void InstallPartial(
        string targetPath,
        IReadOnlyList<PartialFileUpdate> updates,
        IReadOnlyList<string> deletions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(deletions);

        string target = Path.GetFullPath(targetPath);
        if (!_directories.Exists(target))
        {
            throw new DirectoryNotFoundException("Каталог установленного клиента не найден.");
        }

        string operationId = Guid.NewGuid().ToString("N");
        var staged = new List<(string FinalPath, string NewPath)>();
        // Что уже сделано в фазе фиксации — в порядке применения; откат идёт с конца.
        var committed = new List<(string FinalPath, string? BackupPath, bool Placed)>();

        try
        {
            // Фаза 1 — подготовка.
            foreach (var update in updates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string finalPath = ManifestPathGuard.ResolveInside(target, update.RelativePath);
                string? parent = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(parent)) _files.CreateDirectory(parent);

                // Копирование, а не перенос: вызывающий код держит на скачанном файле
                // открытый FileShare.Read-хендл с момента проверки его SHA256 (см.
                // DownloadResult) и не отпускает его до конца этой фазы. Перенос
                // потребовал бы сначала закрыть хендл — и между проверкой хеша и
                // попаданием файла в папку клиента открылось бы окно подмены (TOCTOU).
                string newPath = finalPath + NewSuffix + operationId;
                _files.CopyFile(update.SourcePath, newPath, true);
                staged.Add((finalPath, newPath));
            }

            // Фаза 2 — фиксация.
            foreach (var (finalPath, newPath) in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? backupPath = null;
                if (_files.FileExists(finalPath))
                {
                    backupPath = finalPath + OldSuffix + operationId;
                    _files.MoveFile(finalPath, backupPath, true);
                    committed.Add((finalPath, backupPath, false));
                }

                _files.MoveFile(newPath, finalPath, false);
                if (backupPath != null) committed[^1] = (finalPath, backupPath, true);
                else committed.Add((finalPath, null, true));
            }

            foreach (string relative in deletions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string finalPath = ManifestPathGuard.ResolveInside(target, relative);
                if (!_files.FileExists(finalPath)) continue;

                string backupPath = finalPath + OldSuffix + operationId;
                _files.MoveFile(finalPath, backupPath, true);
                committed.Add((finalPath, backupPath, false));
            }
        }
        catch
        {
            Rollback(committed, staged);
            throw;
        }

        // Фаза 3 — уборка вне транзакции: набор уже зафиксирован, и сбой удаления
        // резервной копии (например, файл занят антивирусом) не повод откатывать
        // удавшееся обновление. Осиротевшие «.old-*» подчищаются при старте лаунчера.
        foreach (var (_, backupPath, _) in committed)
        {
            if (backupPath == null) continue;
            try { _files.DeleteFile(backupPath); }
            catch { /* см. комментарий выше */ }
        }
    }

    private void Rollback(
        List<(string FinalPath, string? BackupPath, bool Placed)> committed,
        List<(string FinalPath, string NewPath)> staged)
    {
        for (int i = committed.Count - 1; i >= 0; i--)
        {
            var (finalPath, backupPath, placed) = committed[i];
            try
            {
                if (placed && _files.FileExists(finalPath)) _files.DeleteFile(finalPath);
                if (backupPath != null && _files.FileExists(backupPath))
                {
                    _files.MoveFile(backupPath, finalPath, true);
                }
            }
            catch
            {
                // Откат делается по принципу «максимум возможного»: исходную причину
                // сбоя вызывающему коду важнее увидеть, чем ошибку самого отката.
            }
        }

        foreach (var (_, newPath) in staged)
        {
            try
            {
                if (_files.FileExists(newPath)) _files.DeleteFile(newPath);
            }
            catch
            {
                // см. комментарий выше
            }
        }
    }

    private const string NewSuffix = ".new-";
    private const string OldSuffix = ".old-";

    /// <summary>
    /// Имя — служебный остаток прерванной частичной установки («файл.new-{32 hex}»
    /// или «файл.old-{32 hex}»). Такие файлы не входят в состав публикации: их
    /// пропускает построение манифеста и зачищает старт лаунчера. Шаблон намеренно
    /// строгий (ровно 32 hex-символа хвостом), чтобы не задеть настоящий файл
    /// клиента с похожим именем. Чистая функция — покрыта unit-тестами.
    /// </summary>
    public static bool IsTransientArtifactName(string? fileName) =>
        TryParseTransientArtifactName(fileName, out _, out _);

    /// <summary>
    /// Разбирает служебное имя остатка на исходное имя файла и признак «это
    /// сохранённый оригинал» (суффикс «.old-», в отличие от «.new-» — заготовки).
    /// Различать их обязательно: заготовку всегда можно удалить, а сохранённый
    /// оригинал при отсутствии файла под штатным именем — единственная копия,
    /// и её нужно возвращать на место. Чистая функция — покрыта unit-тестами.
    /// </summary>
    public static bool TryParseTransientArtifactName(string? fileName, out string originalName, out bool isBackup) =>
        TryParseTransientArtifactName(fileName, out originalName, out isBackup, out _);

    /// <summary>
    /// То же, плюс идентификатор операции (32 hex-хвост): остатки одной прерванной
    /// транзакции нужно разбирать вместе — см. <see cref="RecoverLeftovers"/>.
    /// </summary>
    public static bool TryParseTransientArtifactName(
        string? fileName, out string originalName, out bool isBackup, out string operationId)
    {
        originalName = string.Empty;
        isBackup = false;
        operationId = string.Empty;
        if (string.IsNullOrEmpty(fileName)) return false;

        // Решает суффикс, стоящий ПОСЛЕДНИМ: у файла с «.old-» внутри собственного
        // имени («a.old-b.dll.new-{id}») раньше находился «.old-», хвост после него
        // не проходил проверку, и заготовка «.new-» не опознавалась вовсе.
        int oldMarker = fileName.LastIndexOf(OldSuffix, StringComparison.Ordinal);
        int newMarker = fileName.LastIndexOf(NewSuffix, StringComparison.Ordinal);
        bool backup = oldMarker > newMarker;
        int marker = Math.Max(oldMarker, newMarker);
        if (marker <= 0) return false;

        string tail = fileName[(marker + OldSuffix.Length)..];
        if (tail.Length != 32) return false;
        foreach (char c in tail)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }

        originalName = fileName[..marker];
        isBackup = backup;
        operationId = tail;
        return true;
    }

    /// <summary>
    /// Разбор остатков прерванной <see cref="InstallPartial"/> внутри папки клиента
    /// (процесс убит, отключилось питание). Возвращает (восстановлено, удалено).
    ///
    /// Остатки одной операции решаются вместе. Пока у операции есть хоть одна
    /// заготовка «.new-{id}», фаза фиксации не дошла до конца: часть файлов уже новая
    /// (оригинал — в «.old-{id}»), часть ещё старая (новая версия — в «.new-{id}»).
    /// Такую операцию откатываем целиком: все «.old-{id}» возвращаются на место
    /// поверх уже заменённых файлов, заготовки удаляются. Раньше «.old» удалялся
    /// везде, где файл под штатным именем существовал, и в папке оставалась смесь
    /// двух версий. Файлы, которых в старой версии не было и которые успели лечь
    /// на место, остаются лишними — их покажет проверка целостности.
    ///
    /// Если заготовок у операции нет, фиксация завершилась, и «.old-{id}» — просто
    /// неубранные резервные копии: удаляются. Исключение — «.old» без файла под
    /// штатным именем: это единственная копия, её возвращаем на место.
    /// </summary>
    public static (int Restored, int Removed) RecoverLeftovers(string directory)
    {
        int restored = 0, removed = 0;
        var leftovers = new List<(string Path, string Original, bool IsBackup, string OperationId)>();
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList())
        {
            if (TryParseTransientArtifactName(Path.GetFileName(file),
                    out string originalName, out bool isBackup, out string operationId))
            {
                string original = Path.Combine(Path.GetDirectoryName(file)!, originalName);
                leftovers.Add((file, original, isBackup, operationId));
            }
        }

        var interrupted = new HashSet<string>(
            leftovers.Where(l => !l.IsBackup).Select(l => l.OperationId), StringComparer.OrdinalIgnoreCase);

        // Сначала резервные копии (при откате они ложатся поверх новых файлов),
        // потом заготовки.
        foreach (var (path, original, _, operationId) in leftovers.Where(l => l.IsBackup))
        {
            if (interrupted.Contains(operationId) || !File.Exists(original))
            {
                try { File.Move(path, original, overwrite: true); restored++; }
                catch { /* не удалось вернуть — остаток оставляем, не удаляя */ }
                continue;
            }

            try { File.Delete(path); removed++; }
            catch { /* занято/уже удалено */ }
        }

        foreach (var (path, _, _, _) in leftovers.Where(l => !l.IsBackup))
        {
            try { File.Delete(path); removed++; }
            catch { /* занято/уже удалено */ }
        }

        return (restored, removed);
    }

    /// <summary>
    /// Имя каталога — служебный остаток установки для папки клиента
    /// <paramref name="clientName"/>: «.{имя}.staging-{32 hex}» (распаковка) или
    /// «{имя}.backup-{32 hex}» (прежняя версия на время замены). Строго по шаблону:
    /// по одному префиксу соседняя папка пользователя вроде «Ven4Tools_Client.backup-old»
    /// удалялась бы при старте лаунчера целиком.
    /// </summary>
    public static bool IsInstallLeftoverDirectory(string directoryName, string clientName, out bool isBackup)
    {
        isBackup = false;
        string stagingPrefix = $".{clientName}.staging-";
        string backupPrefix = $"{clientName}.backup-";
        string tail;
        if (directoryName.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
            tail = directoryName[stagingPrefix.Length..];
        else if (directoryName.StartsWith(backupPrefix, StringComparison.OrdinalIgnoreCase))
        {
            tail = directoryName[backupPrefix.Length..];
            isBackup = true;
        }
        else
            return false;

        return tail.Length == 32 && tail.All(Uri.IsHexDigit);
    }

    private static void ValidatePaths(string staging, string target)
    {
        if (string.Equals(staging, target, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Staging и целевой каталог должны различаться.");
        }

        string? stagingParent = Path.GetDirectoryName(staging);
        string? targetParent = Path.GetDirectoryName(target);
        if (!string.Equals(stagingParent, targetParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Staging должен находиться рядом с целевым каталогом для атомарной замены.");
        }
    }
}
