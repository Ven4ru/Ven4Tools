using System.Linq;
using System.Text.RegularExpressions;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Единая валидация идентификаторов пакетов и поисковых запросов перед
    /// подстановкой в командную строку (winget/choco/scoop). Защищает от
    /// внедрения посторонних аргументов через ручной ввод пользователя.
    /// Допустимые символы: буквы, цифры, точка, дефис, плюс, подчёркивание, пробел.
    /// </summary>
    public static class CommandLineGuard
    {
        private static readonly Regex _allowed =
            new(@"^[A-Za-z0-9.\-+_ ]+$", RegexOptions.Compiled);

        private static readonly Regex _stripChars =
            new(@"[^A-Za-z0-9.\-+_ ]", RegexOptions.Compiled);

        /// <summary>
        /// Проверяет идентификатор пакета: длина 1–200, только допустимые символы.
        /// </summary>
        public static bool ValidateId(string? id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 200) return false;
            return _allowed.IsMatch(id);
        }

        /// <summary>
        /// Проверяет поисковый запрос: длина 1–100, только допустимые символы.
        /// </summary>
        public static bool ValidateQuery(string? query)
        {
            if (string.IsNullOrEmpty(query) || query.Length > 100) return false;
            return _allowed.IsMatch(query);
        }

        /// <summary>
        /// Удаляет из запроса все недопустимые символы и обрезает до 100 символов.
        /// </summary>
        public static string SanitizeQuery(string? query)
        {
            if (string.IsNullOrEmpty(query)) return "";
            var cleaned = _stripChars.Replace(query, "");
            return cleaned.Length > 100 ? cleaned.Substring(0, 100) : cleaned;
        }
    }
}
