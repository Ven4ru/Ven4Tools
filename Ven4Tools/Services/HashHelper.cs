using System.Security.Cryptography;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Ven4Tools.Services
{
    public static class HashHelper
    {
        public static async Task<string> ComputeSha256Async(string filePath)
        {
            using var sha256 = SHA256.Create();

            await using var stream = File.OpenRead(filePath);

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
            => !string.IsNullOrWhiteSpace(expectedHash);

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
    }
}