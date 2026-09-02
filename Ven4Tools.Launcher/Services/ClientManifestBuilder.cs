using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Helpers;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services;

/// <summary>
/// Построение файлового манифеста по реальному содержимому папки на диске:
/// рекурсивный обход, SHA256 и размер каждого файла. Тот же формат, что у
/// подписанного client-manifest.json — то есть результат можно и сохранить как
/// локальный кэш (<see cref="InstalledManifestStore"/>), и сравнить с манифестом
/// новой версии (<see cref="ClientDeltaPlanner"/>).
///
/// Вызывается после успешной ПОЛНОЙ установки клиента: только так следующее
/// обновление сможет пойти дельтой. Без этого шага кэш состава установленной
/// версии появлялся бы лишь после дельта-обновления, которое само без кэша
/// невозможно — и дельта не сработала бы никогда.
///
/// Тот же обход — основа будущей функции «проверить и починить установленный
/// клиент»: там результат сравнивается с подписанным манифестом, а не пишется
/// в кэш.
/// </summary>
internal static class ClientManifestBuilder
{
    public static async Task<ClientFileManifest> BuildFromDirectoryAsync(
        string directoryPath,
        string version,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Каталог для построения манифеста не найден: {root}");
        }

        var files = new List<ClientManifestFileEntry>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            // Служебные остатки прерванной дельты (см. TransactionalDirectoryInstaller)
            // не являются частью публикации и в манифест попадать не должны.
            if (TransactionalDirectoryInstaller.IsTransientArtifactName(Path.GetFileName(file))) continue;
            // Кэш каталога, который сам клиент пишет в СВОЮ папку установки во время
            // работы (Ven4Tools/Services/CatalogLoaderService.cs — Data/master.json +
            // .sig, путь через AppDomain.CurrentDomain.BaseDirectory, безусловно, не
            // только в портативном режиме). Он не входит в публикацию и в подписанном
            // client-manifest.json никогда не будет — сразу после установки, пока
            // клиент ни разу не запускался, его и на диске ещё нет, поэтому кэш
            // InstalledManifestStore (вызывается сразу после установки) этот файл
            // никогда не видел. Но «Проверка и восстановление клиента» строит манифест
            // по РЕАЛЬНОМУ состоянию папки в произвольный момент — после того как
            // клиент уже поработал и скачал каталог. Без этого исключения проверка
            // видела бы легитимный кэш как «лишний файл» и чинила бы работающую
            // установку удалением её собственного кеша у каждого пользователя.
            if (string.Equals(relative, "Data/master.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relative, "Data/master.json.sig", StringComparison.OrdinalIgnoreCase))
                continue;

            files.Add(new ClientManifestFileEntry
            {
                Path = relative,
                Sha256 = await FileHashHelper.ComputeSha256Async(file, cancellationToken).ConfigureAwait(false),
                Size = new FileInfo(file).Length,
            });
        }

        files.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        return new ClientFileManifest
        {
            Version = version,
            GeneratedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            Files = files,
        };
    }
}
