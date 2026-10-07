using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Файл ответов для установки Windows (<c>autounattend.xml</c>) в корень установочной
    /// флешки. Делает три вещи: создаёт локальную учётную запись, пропускает экраны
    /// первой настройки (вход в учётную запись Microsoft, лицензия, Wi-Fi) и при первом
    /// входе запускает набор «Перед переустановкой».
    ///
    /// Сознательно НЕ делает остального. Образ Windows не изменяется и не скачивается.
    /// Выбор языка, редакции и диска остаётся за человеком: в файле нет прохода
    /// <c>windowsPE</c>, поэтому ни один диск не будет размечен без вопроса. Проверки
    /// TPM и Secure Boot не обходятся. Пароля в файле нет: учётная запись создаётся без
    /// него, чтобы на флешке не лежал пароль открытым текстом.
    /// </summary>
    public static class WindowsAnswerFileBuilder
    {
        public const string FileName = "autounattend.xml";

        private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
        private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

        /// <param name="AccountName">Имя локальной учётной записи (администратор).</param>
        /// <param name="KitRelativePath">Путь к папке набора от корня флешки; пусто — набор в корне.</param>
        /// <param name="UserLocale">Регион и формат («ru-RU»).</param>
        /// <param name="InputLocales">Раскладки клавиатуры в порядке предпочтения («ru-RU», «en-US»).</param>
        /// <param name="TimeZoneId">Идентификатор часового пояса Windows («Russian Standard Time»).</param>
        /// <param name="Architecture">Архитектура устанавливаемой Windows: «amd64» или «arm64».</param>
        public sealed record Options(
            string AccountName,
            string KitRelativePath,
            string UserLocale,
            IReadOnlyList<string> InputLocales,
            string TimeZoneId,
            string Architecture = "amd64");

        // Символы, которые Windows не принимает в имени учётной записи.
        private static readonly char[] ForbiddenAccountChars = "\"/\\[]:;|=,+*?<>@".ToCharArray();

        // Имена, занятые самой системой: учётная запись с таким именем не создаётся.
        private static readonly string[] ReservedAccountNames =
        {
            "Administrator", "Администратор", "Guest", "Гость", "DefaultAccount", "WDAGUtilityAccount",
            "SYSTEM", "NETWORK SERVICE", "LOCAL SERVICE", "defaultuser0", "NONE", "CON", "PRN", "AUX", "NUL"
        };

        /// <summary>Проверяет имя учётной записи; null — имя годится.</summary>
        public static string? ValidateAccountName(string? name)
        {
            string text = (name ?? "").Trim();
            if (text.Length == 0) return "Укажите имя учётной записи.";
            if (text.Length > 20) return "Имя учётной записи — не длиннее 20 символов.";
            if (text.IndexOfAny(ForbiddenAccountChars) >= 0 || text.Any(char.IsControl))
                return "В имени учётной записи нельзя использовать символы \" / \\ [ ] : ; | = , + * ? < > @";
            if (text.EndsWith('.')) return "Имя учётной записи не может заканчиваться точкой.";
            if (ReservedAccountNames.Contains(text, StringComparer.OrdinalIgnoreCase))
                return "Это имя занято самой Windows — выберите другое.";
            return null;
        }

        /// <summary>Строит текст файла ответов.</summary>
        /// <exception cref="ArgumentException">Имя учётной записи или путь к набору недопустимы.</exception>
        public static string Build(Options options)
        {
            if (ValidateAccountName(options.AccountName) is { } accountError)
                throw new ArgumentException(accountError, nameof(options));
            if (options.Architecture is not ("amd64" or "arm64"))
                throw new ArgumentException("Архитектура — amd64 или arm64.", nameof(options));

            string account = options.AccountName.Trim();
            string kitPath = NormalizeKitPath(options.KitRelativePath);

            var international = Component("Microsoft-Windows-International-Core", options.Architecture,
                new XElement(Ns + "InputLocale", string.Join(";", options.InputLocales)),
                new XElement(Ns + "SystemLocale", options.UserLocale),
                new XElement(Ns + "UserLocale", options.UserLocale));

            var shell = Component("Microsoft-Windows-Shell-Setup", options.Architecture,
                new XElement(Ns + "OOBE",
                    new XElement(Ns + "HideEULAPage", "true"),
                    new XElement(Ns + "HideOnlineAccountScreens", "true"),
                    new XElement(Ns + "HideWirelessSetupInOOBE", "true"),
                    // 3 — не включать «рекомендуемые» параметры отправки данных.
                    new XElement(Ns + "ProtectYourPC", "3")),
                new XElement(Ns + "UserAccounts",
                    new XElement(Ns + "LocalAccounts",
                        new XElement(Ns + "LocalAccount",
                            new XAttribute(Wcm + "action", "add"),
                            new XElement(Ns + "Name", account),
                            new XElement(Ns + "DisplayName", account),
                            new XElement(Ns + "Group", "Administrators"),
                            new XElement(Ns + "Password",
                                new XElement(Ns + "Value", ""),
                                new XElement(Ns + "PlainText", "true"))))),
                new XElement(Ns + "TimeZone", options.TimeZoneId),
                new XElement(Ns + "FirstLogonCommands",
                    new XElement(Ns + "SynchronousCommand",
                        new XAttribute(Wcm + "action", "add"),
                        new XElement(Ns + "Order", "1"),
                        new XElement(Ns + "Description", "Ven4Tools: restore drivers, Wi-Fi and programs"),
                        new XElement(Ns + "CommandLine", BuildFirstLogonCommand(kitPath)))));

            var document = new XDocument(
                new XDeclaration("1.0", "utf-8", null),
                new XElement(Ns + "unattend",
                    new XAttribute(XNamespace.Xmlns + "wcm", Wcm),
                    new XElement(Ns + "settings",
                        new XAttribute("pass", "oobeSystem"),
                        international,
                        shell)));

            var text = new StringBuilder();
            using (var writer = new Utf8StringWriter(text))
                document.Save(writer);
            return text.ToString();
        }

        /// <summary>
        /// Команда первого входа: ищет набор на всех дисках (после установки буква
        /// флешки заранее неизвестна) и запускает его <c>restore.cmd</c>.
        /// </summary>
        internal static string BuildFirstLogonCommand(string kitPath)
        {
            string target = "%d:\\" + (kitPath.Length == 0 ? "" : kitPath + "\\") + ReinstallKitBuilder.LauncherFileName;
            return "cmd.exe /c \"for %d in (D E F G H I J K L M N O P Q R S T U V W X Y Z C) do " +
                   $"if exist \"{target}\" start \"\" \"{target}\"\"";
        }

        // Путь попадает в командную строку cmd — допускаем только то, что не может её
        // изменить. Кавычки, знаки перенаправления и подстановки отклоняются.
        private static string NormalizeKitPath(string? path)
        {
            string text = (path ?? "").Trim().Replace('/', '\\').Trim('\\');
            if (text.Length == 0) return "";
            if (text.Contains(':') || text.Split('\\').Any(part => part is "" or "." or ".."))
                throw new ArgumentException("Путь к набору задаётся от корня флешки, без буквы диска и переходов вверх.");
            if (text.IndexOfAny("\"%&|<>^!()".ToCharArray()) >= 0 || text.Any(char.IsControl))
                throw new ArgumentException("В пути к набору нельзя использовать символы \" % & | < > ^ ! ( )");
            return text;
        }

        private static XElement Component(string name, string architecture, params object[] content) =>
            new(Ns + "component",
                new XAttribute("name", name),
                new XAttribute("processorArchitecture", architecture),
                new XAttribute("publicKeyToken", "31bf3856ad364e35"),
                new XAttribute("language", "neutral"),
                new XAttribute("versionScope", "nonSxS"),
                content);

        /// <summary>
        /// Записывает файл ответов в корень диска, на котором лежит набор. Уже лежащий
        /// там файл (например, от программы записи образа) сохраняется рядом с
        /// расширением <c>.bak</c>.
        /// </summary>
        /// <returns>Полный путь записанного файла.</returns>
        /// <param name="systemDriveRoot">Корень системного диска; null — определить самим (параметр нужен тестам).</param>
        public static string WriteToDriveRoot(
            string kitRoot, Func<string, Options> optionsForKitPath, string? systemDriveRoot = null)
        {
            string full = Path.GetFullPath(kitRoot);
            string driveRoot = Path.GetPathRoot(full)
                ?? throw new ArgumentException("У папки набора нет корня диска.", nameof(kitRoot));
            if (driveRoot.StartsWith(@"\\", StringComparison.Ordinal))
                throw new ArgumentException("Файл ответов кладётся в корень флешки, а не сетевой папки.", nameof(kitRoot));

            // Корень системного диска Windows тоже просматривает в поисках файла ответов:
            // оставленный там файл сработал бы при следующей подготовке или сбросе этой
            // системы, а не при установке с флешки.
            systemDriveRoot ??= Path.GetPathRoot(Environment.SystemDirectory);
            if (string.Equals(driveRoot, systemDriveRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "Файл ответов кладётся в корень установочной флешки. Выберите папку набора на флешке, а не на системном диске.",
                    nameof(kitRoot));

            string relative = Path.GetRelativePath(driveRoot, full);
            if (relative == ".") relative = "";

            string content = Build(optionsForKitPath(relative));
            string target = Path.Combine(driveRoot, FileName);
            if (File.Exists(target))
                // Прежняя копия не перезаписывается: при повторной сборке в .bak лежал бы
                // уже наш собственный файл, а исходный пропал бы.
                if (!File.Exists(target + ".bak")) File.Copy(target, target + ".bak");
            File.WriteAllText(target, content, new UTF8Encoding(false));
            return target;
        }

        // XDocument.Save в StringWriter пишет в объявлении utf-16; файл же сохраняется в UTF-8.
        private sealed class Utf8StringWriter : StringWriter
        {
            public Utf8StringWriter(StringBuilder builder) : base(builder) { }
            public override Encoding Encoding => Encoding.UTF8;
        }
    }
}
