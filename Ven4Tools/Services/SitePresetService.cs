using System;
using System.Collections.Generic;
using System.Linq;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Разбор кода набора, собранного на ven4tools.ru.
    /// <para>
    /// Код самодостаточен: он САМ содержит список приложений и не требует
    /// обращения к серверу. Сервер о наборах ничего не знает и ничего о них
    /// не хранит — нам не нужны данные о том, кто какие программы себе
    /// выбирает. Поэтому здесь нет ни одного сетевого вызова, и работает это
    /// в том числе без интернета.
    /// </para>
    /// <para>
    /// Формат: <c>V4T:id,id,id</c> — настоящие идентификаторы приложений, а не
    /// их позиции в каталоге. Благодаря этому код переживает любые правки
    /// каталога, а человек глазами видит, что именно передаёт.
    /// </para>
    /// </summary>
    public static class SitePresetService
    {
        public const string CodePrefix = "V4T:";

        /// <summary>Больше приложений, чем есть в каталоге, набор содержать не может.</summary>
        private const int MaxApps = 200;

        public sealed class PresetParseResult
        {
            public bool Success { get; init; }
            public List<string> AppIds { get; init; } = new();
            /// <summary>Готовое к показу сообщение, если код разобрать не удалось.</summary>
            public string Error { get; init; } = "";
        }

        /// <summary>
        /// Разбирает вставленный код. Принимает и сам код, и ссылку с сайта
        /// (<c>ven4tools.ru/?scene=catalog&amp;set=...</c>) — человек копирует
        /// то, что попалось под руку, и обе формы несут один и тот же список.
        /// </summary>
        public static PresetParseResult Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Fail("Код пуст. Скопируйте его на ven4tools.ru в разделе «Каталог».");
            }

            var text = raw.Trim();
            string payload;

            if (text.StartsWith(CodePrefix, StringComparison.OrdinalIgnoreCase))
            {
                payload = text.Substring(CodePrefix.Length);
            }
            else if (TryExtractFromUrl(text, out var fromUrl))
            {
                payload = fromUrl;
            }
            else
            {
                return Fail("Это не похоже на код набора. Код начинается с «V4T:» — " +
                            "например V4T:google-chrome,telegram,vlc.");
            }

            var ids = payload
                .Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => id.Trim())
                .Where(id => id.Length > 0 && IsPlausibleId(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxApps)
                .ToList();

            if (ids.Count == 0)
            {
                return Fail("В коде нет ни одного приложения. Соберите набор заново на ven4tools.ru.");
            }

            return new PresetParseResult { Success = true, AppIds = ids };
        }

        /// <summary>
        /// Идентификаторы каталога — латиница, цифры, дефис и точка. Всё прочее
        /// отсекаем здесь, чтобы из буфера обмена не приезжала произвольная
        /// строка: дальше по этим значениям идёт поиск в каталоге.
        /// </summary>
        private static bool IsPlausibleId(string id) =>
            id.Length <= 64 && id.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                                        || (c >= '0' && c <= '9') || c == '-' || c == '.' || c == '_');

        private static bool TryExtractFromUrl(string text, out string payload)
        {
            payload = "";
            if (text.IndexOf("set=", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (text.IndexOf("ven4tools.ru", StringComparison.OrdinalIgnoreCase) < 0) return false;

            var marker = text.IndexOf("set=", StringComparison.OrdinalIgnoreCase);
            var tail = text.Substring(marker + 4);
            var stop = tail.IndexOfAny(new[] { '&', '#', ' ' });
            if (stop >= 0) tail = tail.Substring(0, stop);

            payload = Uri.UnescapeDataString(tail);
            return payload.Length > 0;
        }

        private static PresetParseResult Fail(string error) => new() { Error = error };
    }
}
