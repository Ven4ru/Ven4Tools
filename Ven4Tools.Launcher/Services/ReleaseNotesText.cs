using System;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Заметки к выпуску на языке интерфейса.
    ///
    /// Заметки берутся из описания релиза на GitHub — одного на всех. Чтобы в нём уместились
    /// два языка, описание делится строкой-меткой <c>&lt;!-- en --&gt;</c>: до неё русский
    /// текст, после — английский. На странице релиза метка не видна (это комментарий), а
    /// лаунчер показывает только свою половину. Описание без метки показывается целиком —
    /// так выглядят все прежние релизы.
    /// </summary>
    internal static class ReleaseNotesText
    {
        public const string EnglishMarker = "<!-- en -->";

        public static string? ForLanguage(string? notes, string language)
        {
            if (string.IsNullOrEmpty(notes)) return notes;

            int marker = notes.IndexOf(EnglishMarker, StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return notes;

            string russian = notes[..marker].Trim();
            string english = notes[(marker + EnglishMarker.Length)..].Trim();
            bool wantEnglish = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);

            // Половина нужного языка пуста (метка в самом начале или в самом конце) —
            // показываем другую: текст на чужом языке лучше пустого окна.
            if (wantEnglish) return english.Length > 0 ? english : russian;
            return russian.Length > 0 ? russian : english;
        }
    }
}
