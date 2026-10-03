using System;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Какая оболочка главного окна показана: новая или прежняя.
    ///
    /// Новая оболочка меняет привычную раскладку меню, поэтому прежняя не удалена:
    /// пользователь возвращается к ней одной кнопкой слева внизу и так же уходит
    /// обратно. Выбор хранится в профиле и переживает перезапуск.
    /// </summary>
    public static class UiModeService
    {
        public const string Modern = "modern";
        public const string Classic = "classic";

        /// <summary>
        /// Принудительно прежняя оболочка — для UI-тестов: существующий набор написан
        /// под прежнюю раскладку меню и должен проверять именно её, что бы ни было
        /// записано в профиле.
        /// </summary>
        private static bool ForcedClassic =>
            Environment.GetEnvironmentVariable("VEN4TOOLS_UI_CLASSIC") == "1";

        public static bool IsModern =>
            !ForcedClassic &&
            !string.Equals(ProfileService.Current.UiMode, Classic, StringComparison.OrdinalIgnoreCase);

        public static void Set(bool modern)
        {
            ProfileService.Current.UiMode = modern ? Modern : Classic;
            ProfileService.Save();
        }
    }
}
