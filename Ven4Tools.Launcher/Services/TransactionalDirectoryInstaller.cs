using System;
using System.IO;
using System.Threading;

namespace Ven4Tools.Launcher.Services;

internal interface IDirectoryOperations
{
    bool Exists(string path);
    void Move(string source, string destination);
    void Delete(string path, bool recursive);
}

internal sealed class PhysicalDirectoryOperations : IDirectoryOperations
{
    public bool Exists(string path) => Directory.Exists(path);
    public void Move(string source, string destination) => Directory.Move(source, destination);
    public void Delete(string path, bool recursive) => Directory.Delete(path, recursive);
}

internal sealed class TransactionalDirectoryInstaller
{
    private readonly IDirectoryOperations _directories;

    public TransactionalDirectoryInstaller()
        : this(new PhysicalDirectoryOperations())
    {
    }

    internal TransactionalDirectoryInstaller(IDirectoryOperations directories)
    {
        _directories = directories;
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
