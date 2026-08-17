using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ven4Tools.Launcher.Services
{
    // Защита от полной замены/удаления каталога клиента, если путь установки
    // совпадает с папкой локальных данных (%LOCALAPPDATA%\Ven4Tools) или вложен
    // в неё, ЛИБО указывает целиком на известную пользовательскую папку
    // (Downloads/Documents/Desktop/Program Files) — TransactionalDirectoryInstaller
    // переносит весь target в backup и удаляет backup после установки, а
    // "Удалить клиента" делает Directory.Delete(_clientPath, recursive: true) —
    // в обоих случаях реальное содержимое такой папки будет уничтожено.
    //
    // Конкретный сценарий (аудит безопасности 2026-07-13): BtnFindClient_Click
    // ищет Ven4Tools.exe рекурсивно в Downloads/Documents/Desktop и присваивает
    // _clientPath = родительская папка найденного файла — если exe лежит прямо
    // в корне Downloads (типовой случай "распаковал сюда"), _clientPath
    // становится самим Downloads. Подпапки ВНУТРИ этих корней (например
    // Downloads\Ven4Tools_Client) остаются легитимными и не блокируются.
    internal static class InstallPathGuard
    {
        public static bool IsClientPathSafe(string clientPath, string dataFolderPath)
        {
            string client = Normalize(clientPath);
            string data = Normalize(dataFolderPath);

            if (string.Equals(client, data, StringComparison.OrdinalIgnoreCase)) return false;
            if (IsSameOrSubPath(client, data)) return false;
            if (IsSameOrSubPath(data, client)) return false;
            if (IsProtectedUserRoot(client)) return false;

            return true;
        }

        private static bool IsProtectedUserRoot(string normalizedClientPath)
        {
            var roots = new List<string?>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            };
            roots.AddRange(DownloadsFolderResolver.GetExistingCandidates());

            return roots
                .Where(r => !string.IsNullOrEmpty(r))
                .Any(r => string.Equals(normalizedClientPath, Normalize(r!), StringComparison.OrdinalIgnoreCase));
        }

        private static string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static bool IsSameOrSubPath(string path, string root) =>
            path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
