using System;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Редакция подписанного version.json. Обычный манифест (cdn.ven4tools.ru/version.json)
    /// и манифест версии для Windows 10 без обновлений (cdn.ven4tools.ru/legacy/version.json)
    /// подписаны одним ключом — подпись их не различает. Поэтому каждый адрес принимает только
    /// манифест своей редакции: обычный по ошибке выкладки в /legacy/ (или наоборот) отвергается
    /// так же, как неподписанный, и не сдвигает номер выпуска.
    /// </summary>
    internal static class ManifestEdition
    {
        /// <summary>Обычная редакция; в манифесте поля нет.</summary>
        public const string Regular = "regular";

        /// <summary>Версия для Windows 10 без обновлений.</summary>
        public const string Win10Legacy = "win10legacy";

        /// <summary>Какую редакцию ждёт манифест по этому адресу.</summary>
        public static string ExpectedFor(string manifestUrl) =>
            manifestUrl.EndsWith("/legacy/version.json", StringComparison.OrdinalIgnoreCase) ? Win10Legacy : Regular;

        /// <summary>Редакция манифеста; нет поля — обычная.</summary>
        public static string Of(string? edition) =>
            string.IsNullOrWhiteSpace(edition) ? Regular : edition.Trim().ToLowerInvariant();

        public static bool Matches(string? edition, string expected) =>
            string.Equals(Of(edition), expected, StringComparison.OrdinalIgnoreCase);
    }
}
