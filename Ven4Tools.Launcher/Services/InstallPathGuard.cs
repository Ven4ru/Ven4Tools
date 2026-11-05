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
            // Путь приходит и из файла настроек, который пользователь может испортить:
            // пустая строка или недопустимые символы — не повод уронить вызывающего
            // ArgumentException, а отказ (fail-closed: по такому пути ничего не пишем
            // и не удаляем).
            try
            {
                string client = Normalize(clientPath);
                string data = Normalize(dataFolderPath);

                if (string.Equals(client, data, StringComparison.OrdinalIgnoreCase)) return false;
                if (IsSameOrSubPath(client, data)) return false;
                if (IsSameOrSubPath(data, client)) return false;
                if (IsProtectedUserRoot(client)) return false;
                if (IsInsideWindowsFolder(client)) return false;
                if (IsVolumeRoot(clientPath)) return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        // Папка Windows и всё внутри неё: она не предок папки данных, поэтому проверки
        // пересечения выше её не ловили, а лаунчер, запущенный от имени администратора,
        // смог бы перенести такую папку в резервную копию и удалить.
        private static bool IsInsideWindowsFolder(string normalizedClientPath)
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (string.IsNullOrEmpty(windows)) return false;
            string root = Normalize(windows);
            return string.Equals(normalizedClientPath, root, StringComparison.OrdinalIgnoreCase) ||
                   IsSameOrSubPath(normalizedClientPath, root);
        }

        // Корень диска или сетевой шары («D:\», «\\server\share»). Системный диск уже
        // отсекается проверкой выше (он предок папки данных), а любой другой — нет:
        // путь из отредактированного файла настроек «D:\» превращал «Удалить клиент»
        // в Directory.Delete всего диска, а установку — в перенос его содержимого.
        private static bool IsVolumeRoot(string path)
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            return !string.IsNullOrEmpty(root) &&
                   string.Equals(
                       full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
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
                // Остальные корни, содержимое которых целиком чужое: перемещаемые
                // «Изображения»/«Видео»/«Музыка» не обязаны лежать в профиле, а
                // AppData\Roaming и ProgramData — папки данных всех программ сразу.
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            };
            roots.AddRange(DownloadsFolderResolver.GetExistingCandidates());
            // Корень OneDrive целиком — это все облачные файлы пользователя.
            roots.AddRange(InstallLocationRisk.OneDriveRoots());

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
