using System;
using System.IO;

namespace Ven4Tools.Launcher.Services
{
    // Защита от полной замены каталога клиента, если путь установки совпадает
    // с папкой локальных данных (%LOCALAPPDATA%\Ven4Tools) или вложен в неё —
    // TransactionalDirectoryInstaller удалит всё содержимое target при обновлении.
    internal static class InstallPathGuard
    {
        public static bool IsClientPathSafe(string clientPath, string dataFolderPath)
        {
            string client = Path.GetFullPath(clientPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string data = Path.GetFullPath(dataFolderPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.Equals(client, data, StringComparison.OrdinalIgnoreCase)) return false;
            if (client.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            if (data.StartsWith(client + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }
    }
}
