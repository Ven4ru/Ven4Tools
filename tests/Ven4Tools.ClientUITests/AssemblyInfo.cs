using Microsoft.VisualStudio.TestTools.UnitTesting;

// Тесты этой сборки запускают реальный Ven4Tools.exe и делят один и тот же
// %LOCALAPPDATA%\Ven4Tools (profile.json, source_order.json, дисковый кэш
// каталога) между классами — параллельный запуск классов вызывает гонки
// (один тест перезаписывает настройки другого прямо во время его выполнения).
[assembly: DoNotParallelize]

namespace Ven4Tools.ClientUITests
{
    [TestClass]
    public static class TestRunLanguage
    {
        /// <summary>
        /// Проверки ищут элементы по русским подписям, а клиент на нерусской Windows
        /// сам переключается на английский. Язык закрепляется на время прогона —
        /// клиент наследует переменную окружения. Явно заданное значение не трогаем:
        /// так делаются снимки английского интерфейса.
        /// </summary>
        [AssemblyInitialize]
        public static void PinLanguage(TestContext context)
        {
            if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("VEN4TOOLS_LANG")))
                System.Environment.SetEnvironmentVariable("VEN4TOOLS_LANG", "ru");
        }
    }
}
