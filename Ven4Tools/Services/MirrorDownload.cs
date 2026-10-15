using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Запасная загрузка установщиков сторонних инструментов (драйвер датчиков PawnIO,
    /// счётчик FPS PresentMon) с нашего CDN, когда winget не может скачать их с GitHub —
    /// из России GitHub открывается через раз (проверено 15.10.2026: 0x80072EE2 у обоих
    /// пакетов, а копии на CDN скачиваются).
    ///
    /// Копии лежат в <c>cdn.ven4tools.ru/mirror/</c> — те же файлы, что в пакетах winget,
    /// с той же суммой. Сумма зашита сюда: файл с другой суммой не запускается, даже если
    /// CDN подменят. Подпись автора проверяет вызывающий.
    /// </summary>
    public static class MirrorDownload
    {
        public sealed record Item(string Url, string Sha256, string FileName);

        public static readonly Item PawnIo = new(
            "https://cdn.ven4tools.ru/mirror/pawnio/PawnIO_setup_2.2.0.exe",
            "1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032",
            "PawnIO_setup_2.2.0.exe");

        public static readonly Item PresentMon = new(
            "https://cdn.ven4tools.ru/mirror/presentmon/PresentMon-2.6.0-x64.exe",
            "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af",
            "PresentMon-2.6.0-x64.exe");

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

        /// <summary>Скачать в <paramref name="destination"/>; false — не скачалось или сумма не та (файл удалён).</summary>
        public static async Task<bool> DownloadAsync(Item item, string destination, CancellationToken ct = default)
        {
            string temp = destination + ".part";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using (var response = await Http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (!response.IsSuccessStatusCode || !DownloadValidator.ValidateAfterRedirect(response))
                    {
                        AppLogger.Write($"[Зеркало] {item.FileName}: HTTP {(int)response.StatusCode}");
                        return false;
                    }
                    await using var file = File.Create(temp);
                    await response.Content.CopyToAsync(file, ct);
                }

                string actual;
                await using (var file = File.OpenRead(temp))
                    actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
                if (actual != item.Sha256)
                {
                    AppLogger.Write($"[Зеркало] {item.FileName}: сумма не совпала ({actual[..12]}…) — файл удалён");
                    File.Delete(temp);
                    return false;
                }
                File.Move(temp, destination, overwrite: true);
                AppLogger.Write($"[Зеркало] {item.FileName}: скачан с CDN, сумма совпала");
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                AppLogger.Write(ex, $"MirrorDownload {item.FileName}");
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                return false;
            }
        }
    }
}
