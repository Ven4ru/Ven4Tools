using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Единая валидация идентификаторов пакетов и поисковых запросов перед
    /// подстановкой в командную строку (winget/choco). Защищает от
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

        private static readonly Regex _safeArgsPattern =
            new(@"^[A-Za-z0-9/\\\-=\s""'.,_+:]+$", RegexOptions.Compiled);

        /// <summary>
        /// Проверяет аргументы тихой установки: пусто допустимо, длина до 300,
        /// только безопасные символы (без shell-метасимволов &amp; | ; и др.).
        /// </summary>
        public static bool ValidateSilentArgs(string? args)
        {
            if (string.IsNullOrWhiteSpace(args)) return true;
            if (args.Length > 300) return false;
            return _safeArgsPattern.IsMatch(args);
        }

        /// <summary>
        /// Проверяет путь установки: пусто допустимо, UNC-пути (\\server\share)
        /// запрещены, путь должен быть абсолютным.
        /// </summary>
        public static bool ValidateInstallFolder(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            if (path.StartsWith(@"\\")) return false;
            // Кавычка/невалидные символы пути — защита в глубину: аргументы winget теперь
            // строятся через ArgumentList (не подставляются в кавыченную строку), но
            // валидатор не должен полагаться только на это.
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('"')) return false;
            return Path.IsPathRooted(path);
        }
    }
}
