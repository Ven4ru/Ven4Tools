using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Ven4Tools.Launcher.Helpers;

/// <summary>
/// Каталог для скачанного установщика компонента (VC++, WebView2, Windows App
/// Runtime), который лаунчер запускает со СВОИМИ правами. Тот же приём, что у
/// клиентского Ven4Tools.Helpers.InstallerTempDirectory: сборки разные, механизм один.
///
/// Лаунчер штатно оказывается elevated — сам предлагает «Перезапустить с правами
/// администратора» ради VC++ Redistributable. Установщик из общего %TEMP% тогда
/// запускался напрямую с правами администратора, а каталог рядом с ним оставался
/// доступным на запись непривилегированному процессу того же пользователя:
/// подложенная туда DLL (version.dll и т.п.) грузится установщиком по порядку поиска
/// DLL. Подпись Microsoft и хендл FileShare.Read защищают сам exe, но не соседей.
///
/// В elevated-процессе каталог создаётся в %SystemRoot%\Temp (там обычный
/// пользователь не может удалить или переименовать чужой подкаталог — в своём
/// %TEMP% у него FILE_DELETE_CHILD) с DACL без наследования: полный доступ SYSTEM
/// и Администраторам, пользователю — чтение и запуск, владелец — Администраторы.
/// Без elevated-прав защищать нечего: установщик, запущенный через UAC, всё равно
/// стартует из каталога, созданного обычным процессом, — используется подкаталог %TEMP%.
/// </summary>
internal static class InstallerRunDirectory
{
    /// <summary>Новый пустой каталог со случайным именем. Удаляет вызывающий (<see cref="TryDelete"/>).</summary>
    public static string Create()
    {
        bool elevated = IsElevated();
        string root = elevated
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp")
            : Path.GetTempPath();
        string path = Path.Combine(root, $"Ven4Tools.Launcher.Installers.{Guid.NewGuid():N}");

        // Directory.CreateDirectory молча «успевает» на уже существующем каталоге —
        // и тогда наш DACL не применяется. Имя случайное, но проверяем явно.
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException($"Временный каталог установщика уже существует: {path}");

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
        // Владелец — Администраторы, а не пользователь: владелец неявно получает
        // WRITE_DAC, и непривилегированный процесс вернул бы себе запись.
        security.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));

        new DirectoryInfo(path).Create(security);

        if (FileHelper.IsReparsePoint(path))
            throw new IOException($"Временный каталог установщика подменён ссылкой: {path}");

        return path;
    }

    /// <summary>
    /// Удаление без исключений: установщик, не дождавшийся конца из-за отмены, ещё
    /// держит свой exe — тогда каталог остаётся до очистки временных файлов системой.
    /// </summary>
    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // см. комментарий выше
        }
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
