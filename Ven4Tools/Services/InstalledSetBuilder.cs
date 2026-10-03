using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Ven4Tools.Services
{
    /// <summary>
    /// «Набор из установленного»: что из каталога уже стоит на этом компьютере — в виде
    /// кода набора и файла ответа. С ними тот же состав ставится на другой машине или
    /// после переустановки Windows: код принимает кнопка «Набор с сайта», файл —
    /// <c>Ven4Tools.exe --answer-file …</c>.
    /// </summary>
    public static class InstalledSetBuilder
    {
        public sealed record InstalledSet(IReadOnlyList<string> Names, IReadOnlyList<string> AppIds, string Code);

        /// <param name="apps">
        /// Программы каталога: идентификатор, название и признак «установлена». Добавленные
        /// вручную сюда не передаются — на другой машине их в каталоге не будет.
        /// </param>
        public static InstalledSet Build(IEnumerable<(string Id, string Name, bool Installed)> apps)
        {
            var installed = apps.Where(a => a.Installed).ToList();
            string code = SitePresetService.BuildCode(installed.Select(a => a.Id));

            // В код попадают только идентификаторы, которые разбор кода принимает, —
            // список названий должен соответствовать ему, а не обещать лишнего.
            var accepted = new HashSet<string>(
                SitePresetService.Parse(code).AppIds, StringComparer.OrdinalIgnoreCase);
            var kept = installed.Where(a => accepted.Contains(a.Id)).ToList();

            return new InstalledSet(kept.Select(a => a.Name).ToList(), kept.Select(a => a.Id).ToList(), code);
        }

        /// <summary>
        /// Файл ответа для тихой установки этого набора. Точка восстановления включена:
        /// файл рассчитан на запуск без человека, спросить будет некого.
        /// </summary>
        public static string BuildAnswerFile(IReadOnlyList<string> appIds) =>
            JsonSerializer.Serialize(
                new { apps = appIds, silent = true, restorePoint = true, allowPackageManagers = true },
                new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }
}
