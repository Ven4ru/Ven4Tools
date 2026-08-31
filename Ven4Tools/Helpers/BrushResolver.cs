using System.Windows;
using System.Windows.Media;

namespace Ven4Tools.Helpers
{
    /// <summary>
    /// Поиск темизированной кисти в ресурсах приложения с фолбэком. Один и тот же
    /// TryFindResource-с-фолбэком был написан заново четыре раза при MVVM-миграции
    /// (ActivationViewModel, NetworkCheckResult, DiagnosticsViewModel,
    /// BenchmarkViewModel) плюс дважды в конвертерах кистей.
    ///
    /// Фолбэк — ПАРАМЕТР, а не константа: у ViewModel он белый (кисти статусов
    /// рисуются поверх тёмного фона вкладки), у конвертеров прогресса установки —
    /// серый. Разница осознанная, зашивать один цвет на всех нельзя: белый текст
    /// на белом фоне светлой темы — ровно тот баг, ради которого фолбэк вообще есть.
    ///
    /// <c>Application.Current</c> проверяется на null: в юнит-тестах приложения нет,
    /// и без проверки обращение к ресурсам падало бы.
    /// </summary>
    internal static class BrushResolver
    {
        /// <summary>Кисть по ключу ресурса; при отсутствии — <paramref name="fallback"/>.</summary>
        public static Brush Resolve(string resourceKey, Brush fallback) =>
            (Application.Current?.TryFindResource(resourceKey) as Brush) ?? fallback;

        /// <summary>Кисть по ключу ресурса; при отсутствии — белая (фолбэк ViewModel-ей).</summary>
        public static Brush Resolve(string resourceKey) => Resolve(resourceKey, Brushes.White);

        /// <summary>
        /// Кисть по ключу ресурса; при отсутствии — кисть по запасному ключу, а если
        /// нет и её — <paramref name="fallback"/>. Двухступенчатый вариант конвертеров
        /// кистей: у них запасной ключ — нейтральный цвет палитры, а константа нужна
        /// только на случай, если словарь ресурсов вообще не подключён.
        /// </summary>
        public static Brush Resolve(string resourceKey, string fallbackResourceKey, Brush fallback) =>
            (Application.Current?.TryFindResource(resourceKey) as Brush)
            ?? (Application.Current?.TryFindResource(fallbackResourceKey) as Brush)
            ?? fallback;
    }
}
