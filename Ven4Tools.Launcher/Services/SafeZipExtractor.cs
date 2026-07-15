using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Launcher.Services;

internal static class SafeZipExtractor
{
    // Self-contained клиент-zip сейчас в районе сотен МБ; лимит с большим запасом
    // на будущий рост, но конечный — защита от zip-бомбы (архив с маленьким
    // сжатым размером и огромным разжатым, способный исчерпать диск).
    private const long MaxTotalUncompressedBytes = 4L * 1024 * 1024 * 1024; // 4 ГБ

    public static async Task ExtractAsync(
        string archivePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        PrepareEmptyDestination(destinationPath);
        string destinationRoot = Path.GetFullPath(destinationPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);

        // Метаданные центрального каталога ZIP декларируют разжатый размер каждой
        // записи — проверяем декларацию заранее (дёшево), но не доверяем ей слепо:
        // ниже при копировании отдельно считаем реально записанные байты.
        long declaredTotal = 0;
        foreach (ZipArchiveEntry declared in archive.Entries)
        {
            declaredTotal += declared.Length;
            if (declaredTotal > MaxTotalUncompressedBytes)
                throw new InvalidDataException("Архив превышает допустимый разжатый размер (заявленный в метаданных).");
        }

        long actualTotal = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectSymbolicLink(entry);

            string targetPath = GetSafeDestinationPath(destinationRoot, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using Stream source = entry.Open();
            await using var destination = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            var buffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                actualTotal += read;
                if (actualTotal > MaxTotalUncompressedBytes)
                    throw new InvalidDataException("Архив превышает допустимый разжатый размер (фактический при распаковке).");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
    }

    internal static string GetSafeDestinationPath(string destinationRoot, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName) ||
            Path.IsPathRooted(entryName) ||
            entryName.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Архив содержит недопустимый путь.");
        }

        string normalizedRoot = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string targetPath = Path.GetFullPath(Path.Combine(normalizedRoot, entryName));
        if (!targetPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Архив пытается записать файл за пределами каталога установки.");
        }

        return targetPath;
    }

    private static void PrepareEmptyDestination(string destinationPath)
    {
        if (Directory.Exists(destinationPath) &&
            Directory.EnumerateFileSystemEntries(destinationPath).Any())
        {
            throw new InvalidOperationException("Каталог распаковки должен быть пустым.");
        }

        Directory.CreateDirectory(destinationPath);
    }

    private static void RejectSymbolicLink(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        int unixMode = (entry.ExternalAttributes >> 16) & unixFileTypeMask;
        if (unixMode == unixSymbolicLink)
        {
            throw new InvalidDataException("Архив содержит символическую ссылку.");
        }
    }
}
