using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Получение набора приложений по короткому коду с сайта.
    /// Человек собирает подборку на ven4tools.ru, получает код вида V4T-XXXXX
    /// и вводит его здесь — клиент отмечает те же приложения в каталоге.
    /// </summary>
    public static class SitePresetService
    {
        // Единый переиспользуемый HttpClient, как во всех прочих сервисах проекта:
        // пересоздание клиента на каждый вызов исчерпывает сокеты.
        private static readonly HttpClient _http =
            new() { Timeout = TimeSpan.FromSeconds(10) };

        private const string Endpoint = "https://ven4tools.ru/api/db.php?action=get_preset&code=";

        /// <summary>Длина кода, который выдаёт сайт (без префикса «V4T-»).</summary>
        private const int CodeLength = 5;

        // Алфавит кода на сайте — без символов, которые путают при переписывании
        // от руки (0/O, 1/I/L, 5/S, 8/B). Проверяем форму до сетевого запроса,
        // чтобы очевидную опечатку показать сразу, а не после таймаута.
        private static readonly Regex CodePattern =
            new("^[234679ACDEFGHJKMNPQRTUVWXYZ]{4,12}$", RegexOptions.Compiled);

        /// <summary>
        /// Приводит введённое к каноническому виду: убирает префикс V4T-,
        /// пробелы и дефисы, поднимает регистр. Человек может вписать код
        /// как угодно — «v4t-6crwk», «6CRWK», «V4T 6 CRWK».
        /// </summary>
        public static string NormalizeCode(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            // Сначала выбрасываем всё, что не буква и не цифра: дефисы и пробелы
            // человек ставит как придётся, и «V4T 6 CRWK» должно работать так же,
            // как «V4T-6CRWK».
            var clean = new string(raw.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

            // Префикс срезаем только если после него что-то остаётся сверх самого
            // кода. Буквы V, T и цифра 4 входят в алфавит кода, поэтому короткий
            // код вида «V4TQR» — это КОД, а не префикс с остатком, и трогать его
            // нельзя. Длина кода — 5 знаков, значит префикс есть только у строк
            // длиннее пяти.
            if (clean.Length > CodeLength && clean.StartsWith("V4T", StringComparison.Ordinal))
            {
                clean = clean.Substring(3);
            }

            // Один голый префикс — это не код: иначе он дошёл бы до текста ошибки
            // в виде «набор с кодом V4T-V4T не найден».
            if (clean == "V4T") return "";

            return clean;
        }

        public static bool LooksLikeCode(string? raw) => CodePattern.IsMatch(NormalizeCode(raw));

        public sealed class PresetFetchResult
        {
            public bool Success { get; init; }
            public string Code { get; init; } = "";
            public List<string> AppIds { get; init; } = new();
            /// <summary>Готовое к показу сообщение, если получить набор не удалось.</summary>
            public string Error { get; init; } = "";
        }

        /// <summary>
        /// Забирает состав набора с сайта. Сетевые ошибки не бросаются наружу —
        /// возвращается результат с текстом для пользователя.
        /// </summary>
        public static async Task<PresetFetchResult> FetchAsync(string rawCode)
        {
            var code = NormalizeCode(rawCode);
            if (!CodePattern.IsMatch(code))
            {
                return new PresetFetchResult
                {
                    Error = "Код выглядит неправильно. Он состоит из 5 знаков после «V4T-», " +
                            "например V4T-6CRWK. В коде не бывает цифр 0, 1, 5, 8 и букв O, I, L, S, B."
                };
            }

            try
            {
                using var response = await _http.GetAsync(Endpoint + Uri.EscapeDataString(code))
                                                .ConfigureAwait(false);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return new PresetFetchResult
                    {
                        Error = $"Набор с кодом V4T-{code} не найден. Проверьте код или соберите набор заново на ven4tools.ru."
                    };
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new PresetFetchResult { Error = $"Сервер ответил ошибкой ({(int)response.StatusCode}). Попробуйте позже." };
                }

                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var doc = JObject.Parse(body);
                var ids = doc["apps"]?.ToObject<List<string>>() ?? new List<string>();

                if (ids.Count == 0)
                {
                    return new PresetFetchResult { Error = "Набор пуст — в нём нет приложений." };
                }

                return new PresetFetchResult
                {
                    Success = true,
                    Code = code,
                    AppIds = ids
                };
            }
            catch (TaskCanceledException)
            {
                return new PresetFetchResult { Error = "Сайт не ответил вовремя. Проверьте подключение и попробуйте ещё раз." };
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[SitePresetService] {ex.Message}");
                return new PresetFetchResult { Error = "Не удалось связаться с сайтом. Проверьте подключение." };
            }
        }
    }
}
