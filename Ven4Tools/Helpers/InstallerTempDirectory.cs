using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Ven4Tools.Helpers;

/// <summary>
/// Каталог для скачанных установщиков, которые затем запускаются elevated.
///
/// Раньше установщики клались прямо в %TEMP%. Содержимое самого файла защищено
/// (SHA256 из подписанного каталога или Authenticode, хендл с FileShare.Read
/// держится до конца запуска), но каталог запуска оставался доступным на запись
/// непривилегированному процессу того же пользователя: подложенная рядом
/// DLL (version.dll, dbghelp.dll и т.п.) грузится установщиком по порядку поиска
/// DLL — то есть исполняется с правами администратора, которые пользователь дал
/// клиенту, без какого-либо дополнительного запроса UAC.
///
/// Здесь — отдельный подкаталог со случайным именем, созданный elevated-процессом
/// с явным DACL без наследования: полный доступ только SYSTEM и Администраторам,
/// текущему пользователю — чтение и запуск. Непривилегированный процесс того же
/// пользователя ничего туда положить не может. Каталог один на процесс клиента.
///
/// Если процесс НЕ elevated (запуск из среды разработки/тестов), ограничивать
/// нечего — установщик тогда и так запускается с тем же уровнем прав, что и
/// потенциальный подменщик; используется обычный подкаталог.
/// </summary>
internal static class InstallerTempDirectory
{
    private static readonly object _lock = new();
    private static string? _path;

    /// <summary>Полный путь для нового временного файла установщика.</summary>
    public static string NewFilePath(string fileName) => Path.Combine(Get(), fileName);

    public static string Get()
    {
        lock (_lock)
        {
            if (_path != null && Directory.Exists(_path) && !PathHelper.IsReparsePoint(_path))
                return _path;

            _path = Create();
            return _path;
        }
    }

    private static string Create()
    {
        bool elevated = IsElevated();

        // Elevated — не в пользовательском %TEMP%: у пользователя там полный доступ к
        // самой папке, включая FILE_DELETE_CHILD, а с ним любой его процесс может
        // переименовать наш защищённый каталог вопреки его DACL и создать на том же
        // пути свой — Get() увидел бы «существующий каталог» и положил установщик
        // рядом с подложенной DLL. В %SystemRoot%\Temp обычные пользователи могут
        // только создавать свои подкаталоги, удалять и переименовывать чужие — нет.
        string root = elevated
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
            : Path.GetTempPath();
        string path = Path.Combine(root, $"Ven4Tools.Installers.{Guid.NewGuid():N}");

        // Имя случайное, но проверяем явно: Directory.CreateDirectory молча
        // «успевает» на уже существующем каталоге и тогда наш DACL не применяется.
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException($"Временный каталог установщиков уже существует: {path}");

        if (!elevated)
        {
            Directory.CreateDirectory(path);
            return path;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User != null)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    identity.User, FileSystemRights.ReadAndExecute, inherit,
                    PropagationFlags.None, AccessControlType.Allow));
            }
        }
        // Владелец — Администраторы, а не пользователь: иначе владелец неявно
        // получает WRITE_DAC и непривилегированный процесс вернул бы себе запись.
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        new DirectoryInfo(path).Create(security);

        if (PathHelper.IsReparsePoint(path))
            throw new IOException($"Временный каталог установщиков подменён ссылкой: {path}");

        return path;
    }

    // Свой каталог и каталоги установщиков лаунчера (InstallerRunDirectory): из
    // %SystemRoot%\Temp их может убрать только elevated-процесс, а клиент — как раз он.
    private static readonly string[] StalePatterns =
    {
        "Ven4Tools.Installers.*",
        "Ven4Tools.Launcher.Installers.*",
    };

    /// <summary>
    /// Убирает каталоги установщиков прошлых сеансов. Каталог живёт до конца
    /// процесса и сам не удаляется (установщик может ещё работать), поэтому после
    /// каждого сеанса с установкой в %SystemRoot%\Temp оставалась пустая папка.
    /// Трогаются только наши шаблоны имён, не ссылки, не каталог текущего процесса
    /// и только старше <paramref name="minAge"/> — запущенный недавно установщик
    /// может ещё выполняться из своего каталога. Занятое пропускается.
    /// </summary>
    /// <returns>Сколько каталогов удалено.</returns>
    public static int CleanupStale(DateTime nowUtc, TimeSpan minAge)
    {
        string? current;
        lock (_lock) { current = _path; }

        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
            Path.GetTempPath(),
        };
        return CleanupStale(roots, current, nowUtc, minAge);
    }

    internal static int CleanupStale(string[] roots, string? current, DateTime nowUtc, TimeSpan minAge)
    {
        int removed = 0;
        foreach (string root in roots)
        {
            foreach (string pattern in StalePatterns)
            {
                string[] directories;
                try { directories = Directory.GetDirectories(root, pattern); }
                catch { continue; }

                foreach (string dir in directories)
                {
                    try
                    {
                        if (current != null && string.Equals(
                                Path.GetFullPath(dir), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
                            continue;
                        var info = new DirectoryInfo(dir);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (nowUtc - info.LastWriteTimeUtc < minAge) continue;
                        info.Delete(recursive: true);
                        removed++;
                    }
                    catch
                    {
                        // Занят установщиком или нет доступа — уберёт следующий запуск.
                    }
                }
            }
        }
        return removed;
    }

    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
