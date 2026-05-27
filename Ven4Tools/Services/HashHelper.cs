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

        public static async Task<bool> VerifyHashAsync(
            string filePath,
            string expectedHash)
        {
            if (string.IsNullOrWhiteSpace(expectedHash))
            {
                // Логируем отсутствие хеша — не блокируем установку, но предупреждаем
                Debug.WriteLine($"[HashHelper] SHA256 не указан для файла, проверка пропущена");
                return true; // SHA256 отсутствует в каталоге — пропускаем проверку
            }

            string computedHash = await ComputeSha256Async(filePath);

            return computedHash.Equals(
                expectedHash,
                StringComparison.OrdinalIgnoreCase);
        }
    }
}