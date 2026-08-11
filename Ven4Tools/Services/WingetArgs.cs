using System.Linq;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Флаги, обязательные для КАЖДОГО неинтерактивного вызова winget в проекте
    /// (--disable-interactivity — железное правило, см. agent_context.md), раньше
    /// размазанные по 15 вызовам в 12 файлах копипастой. Доказанный Shotgun Surgery:
    /// коммит ec2aaae (round 32, «WingetVersionsService не передавал
    /// --accept-source-agreements») потребовал правки 7 файлов ради одного флага,
    /// и даже после него набор совпадал только в 4 из 15 мест.
    /// </summary>
    internal static class WingetArgs
    {
        /// <summary>Обязательны для любого неинтерактивного вызова (запрос и изменение).</summary>
        public static readonly string[] NonInteractive = { "--accept-source-agreements", "--disable-interactivity" };

        /// <summary>Дополнительно — для команд, которые ставят/обновляют пакеты.</summary>
        public static readonly string[] AcceptPackage = { "--accept-package-agreements" };

        /// <summary>Для вызовов через ArgumentList (WingetRunner.RunAsync(string[])/CreateStartInfo).</summary>
        public static string[] Query(params string[] head) => head.Concat(NonInteractive).ToArray();

        /// <summary>Query() + AcceptPackage — для install/upgrade/import.</summary>
        public static string[] Modify(params string[] head) => head.Concat(AcceptPackage).Concat(NonInteractive).ToArray();

        /// <summary>Строковые константы — для мест, ещё не переведённых на ArgumentList.</summary>
        public const string NonInteractiveLine = "--accept-source-agreements --disable-interactivity";
        public const string ModifyLine = "--accept-package-agreements --accept-source-agreements --disable-interactivity";
    }
}
