using System.Security.Cryptography;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    public static class HashHelper
    {
        public static async Task<string> ComputeSha256Async(string filePath)
        {
            await using var stream = File.OpenRead(filePath);
            return await ComputeSha256Async(stream);
        }

        public static async Task<string> ComputeSha256Async(Stream stream)
        {
            using var sha256 = SHA256.Create();

            var hashBytes = await sha256.ComputeHashAsync(stream);

            return BitConverter
                .ToString(hashBytes)
                .Replace("-", "")
                .ToLowerInvariant();
        }

        /// <summary>
        /// Проверяет, указан ли ожидаемый SHA256-хеш в каталоге.
        /// </summary>
        public static bool HasExpectedHash(string? expectedHash)
            => expectedHash?.Length == 64 && expectedHash.All(Uri.IsHexDigit);

        /// <summary>
        /// Проверяет SHA256 файла. При пустом/отсутствующем ожидаемом хеше
        /// возвращает false — отсутствие хеша НЕ считается успешной проверкой.
        /// Вызывающий код должен отдельно обрабатывать случай отсутствия хеша
        /// через <see cref="HasExpectedHash"/>.
        /// </summary>
        public static async Task<bool> VerifyHashAsync(
            string filePath,
            string expectedHash)
        {
            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                // SHA256 отсутствует — это НЕ успешная проверка
                Debug.WriteLine($"[HashHelper] SHA256 не указан для файла — проверка не пройдена");
                return false;
            }

            string computedHash = await ComputeSha256Async(filePath);

            return computedHash.Equals(
                expectedHash,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Проверяет SHA256 уже открытого потока. Используется там, где хендл файла
        /// нужно держать открытым непрерывно от верификации до запуска elevated-процесса
        /// (TOCTOU-защита) — в отличие от <see cref="VerifyHashAsync(string, string)"/>,
        /// который открывает и закрывает свой временный хендл, оставляя окно между
        /// проверкой и последующим открытием защитного хендла.
        /// </summary>
        public static async Task<bool> VerifyHashAsync(
            Stream stream,
            string expectedHash)
        {
            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                Debug.WriteLine($"[HashHelper] SHA256 не указан для файла — проверка не пройдена");
                return false;
            }

            string computedHash = await ComputeSha256Async(stream);

            return computedHash.Equals(
                expectedHash,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Короткий хеш идентификатора сеанса для отправки на сервер: обезличивает
        /// SessionId, оставаясь достаточным для группировки отзывов/крашей одного
        /// сеанса. Общая реализация для FeedbackService и CrashReportService (клиент) —
        /// та же логика, что GitHubService.HashSessionId в лаунчере (отдельная сборка).
        /// </summary>
        public static string HashSessionId(string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return "";
            byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sessionId));
            return Convert.ToHexString(hash)[..8].ToLowerInvariant();
        }
    }
}
