using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Единственный источник цветов интерфейса клиента.
    /// <para>
    /// До версии 5.0 переключатель темы красил только фоны и текст, а акцентные
    /// элементы (логотип, кнопки главных действий, подписи категорий) были жёстко
    /// закреплены за фирменным зелёным <c>BrandGreen</c> из общего словаря
    /// <c>Shared/DesignTokens.xaml</c> — независимо от выбранной темы. В 5.0 это
    /// поведение отменено: фирменный акцент клиента — это <c>AccentColor</c>
    /// текущей темы, и он же красит логотип, главные кнопки и подписи категорий.
    /// В лаунчере <c>BrandGreen</c> остаётся как был: там переключателя тем нет,
    /// а его <c>AccentColor</c> и так равен фирменному зелёному.
    /// </para>
    /// <para>
    /// Каждая тема описывается одной записью <see cref="ThemePalette"/> из
    /// 13 базовых цветов, остальные ключи выводятся из них расчётом
    /// (<see cref="BuildPalette"/>). Поэтому «ключ определён в одной теме и забыт
    /// в трёх остальных» здесь невозможен по построению — набор ключей у всех тем
    /// один и тот же, это же проверяет юнит-тест <c>ThemePaletteTests</c>.
    /// </para>
    /// </summary>
    public static class ThemeService
    {
        public const string ThemeDark = "dark";
        public const string ThemeLight = "light";
        public const string ThemeTeal = "teal";
        public const string ThemeWeb = "web";

        /// <summary>Все темы клиента в том же порядке, что в списке на вкладке «Настройки».</summary>
        public static IReadOnlyList<string> AllThemes { get; } =
            new[] { ThemeWeb, ThemeTeal, ThemeDark, ThemeLight };

        /// <summary>
        /// Тёмный текст поверх светлой заливки. Тот же оттенок, что использовался
        /// у кнопок с фирменной зелёной заливкой до 5.0 — чтобы «зелёная кнопка
        /// с почти чёрной надписью» осталась узнаваемой в темах, где акцент светлый.
        /// </summary>
        private static readonly Color OnLight = Color.FromRgb(0x06, 0x13, 0x0D);

        private static readonly Color OnDark = Colors.White;

        /// <summary>
        /// Базовые цвета одной темы. Всё остальное (контрастные надписи на заливке,
        /// полупрозрачные оттенки акцента, цвет нажатой кнопки, подложка оверлея)
        /// считается из них, а не задаётся вручную — так тема не может «поехать»
        /// из-за того, что производный оттенок забыли обновить вместе с базовым.
        /// </summary>
        private sealed record ThemePalette(
            Color Window,
            Color Sidebar,
            Color Content,
            Color Card,
            Color Raised,
            Color TextPrimary,
            Color TextSecondary,
            Color Border,
            Color Accent,
            Color Success,
            Color Warning,
            Color Danger,
            Color Info);

        private static ThemePalette PaletteFor(string? theme) => theme switch
        {
            ThemeWeb => new ThemePalette(
                Window: Rgb(0x0A, 0x16, 0x28),
                Sidebar: Rgb(0x0D, 0x1F, 0x35),
                Content: Rgb(0x08, 0x12, 0x20),
                Card: Rgb(0x10, 0x1E, 0x34),
                Raised: Rgb(0x16, 0x29, 0x4A),
                TextPrimary: Rgb(0xE8, 0xF0, 0xFE),
                TextSecondary: Rgb(0x8A, 0x9B, 0xB5),
                Border: Rgb(0x1E, 0x32, 0x50),
                // Ровно BrandGreen из Shared/DesignTokens.xaml. Тема «Как на
                // ven4tools.ru» — тема по умолчанию, и именно она несёт фирменный
                // цвет: логотип, главные кнопки и подписи категорий в ней выглядят
                // в точности так же, как до перевода акцента на тему.
                Accent: Rgb(0x4A, 0xDE, 0x80),
                Success: Rgb(0x4A, 0xDE, 0x80),
                Warning: Rgb(0xFB, 0xBF, 0x24),
                Danger: Rgb(0xF8, 0x71, 0x71),
                Info: Rgb(0x38, 0xBD, 0xF8)),

            // Тема называется «Бирюзовая» (SystemTab.xaml, Tag="teal") — акцент
            // бирюзовый/тил, а не зелёный, как у темы «Как на ven4tools.ru».
            ThemeTeal => new ThemePalette(
                Window: Rgb(0x0A, 0x0A, 0x14),
                Sidebar: Rgb(0x0D, 0x10, 0x18),
                Content: Rgb(0x0A, 0x0A, 0x14),
                Card: Rgb(0x11, 0x16, 0x1F),
                Raised: Rgb(0x15, 0x1C, 0x27),
                TextPrimary: Rgb(0xE2, 0xE8, 0xF0),
                TextSecondary: Rgb(0xAA, 0xB8, 0xCE),
                Border: Rgb(0x1E, 0x2A, 0x38),
                Accent: Rgb(0x00, 0xBC, 0xD4),
                Success: Rgb(0x4A, 0xDE, 0x80),
                Warning: Rgb(0xFB, 0xBF, 0x24),
                Danger: Rgb(0xF8, 0x71, 0x71),
                Info: Rgb(0x38, 0xBD, 0xF8)),

            // Светлая — единственная тема со светлыми подложками. Цвета статусов
            // здесь свои: пастельные #4ADE80/#FBBF24/#F87171 тёмных тем на белой
            // карточке дают контраст около 1.5:1, то есть текст не читается.
            ThemeLight => new ThemePalette(
                Window: Rgb(0xF0, 0xF0, 0xF0),
                Sidebar: Rgb(0xF8, 0xF8, 0xF8),
                Content: Rgb(0xF5, 0xF5, 0xF5),
                Card: Rgb(0xFF, 0xFF, 0xFF),
                Raised: Rgb(0xE9, 0xE9, 0xE9),
                TextPrimary: Rgb(0x1E, 0x1E, 0x1E),
                TextSecondary: Rgb(0x64, 0x64, 0x64),
                Border: Rgb(0xDC, 0xDC, 0xDC),
                Accent: Rgb(0x00, 0x78, 0xD4),
                Success: Rgb(0x15, 0x7F, 0x35),
                Warning: Rgb(0xA4, 0x5A, 0x00),
                Danger: Rgb(0xC6, 0x28, 0x28),
                Info: Rgb(0x03, 0x69, 0xA1)),

            // Тёмная — нейтрально-серая тема Windows, значение по умолчанию для
            // любого неизвестного значения настройки (как и до 5.0).
            _ => new ThemePalette(
                Window: Rgb(0x1E, 0x1E, 0x1E),
                Sidebar: Rgb(0x2D, 0x2D, 0x2D),
                Content: Rgb(0x25, 0x25, 0x26),
                Card: Rgb(0x2D, 0x2D, 0x2D),
                Raised: Rgb(0x3A, 0x3A, 0x3A),
                TextPrimary: Rgb(0xFF, 0xFF, 0xFF),
                TextSecondary: Rgb(0xCC, 0xCC, 0xCC),
                Border: Rgb(0x3D, 0x3D, 0x3D),
                // Светлее системного #0078D4: на почти чёрной подложке «Тёмной»
                // темы синий Windows даёт около 3:1 — до 5.0 подписи категорий и
                // слово «Tools» в логотипе были зелёными и читались заметно лучше,
                // терять это при переводе акцента на тему нельзя.
                Accent: Rgb(0x4C, 0xC2, 0xFF),
                Success: Rgb(0x4A, 0xDE, 0x80),
                Warning: Rgb(0xFB, 0xBF, 0x24),
                Danger: Rgb(0xF8, 0x71, 0x71),
                Info: Rgb(0x38, 0xBD, 0xF8)),
        };

        /// <summary>
        /// Полный набор цветов темы: базовые плюс производные. Чистая функция без
        /// обращения к <c>Application.Current</c> — именно её проверяют юнит-тесты
        /// (в тестах приложения WPF нет, ресурсы читать неоткуда).
        /// </summary>
        public static IReadOnlyDictionary<string, Color> BuildPalette(string? theme)
        {
            ThemePalette p = PaletteFor(theme);

            return new Dictionary<string, Color>(StringComparer.Ordinal)
            {
                // Подложки и текст
                ["WindowBackground"] = p.Window,
                ["SidebarBackground"] = p.Sidebar,
                ["ContentBackground"] = p.Content,
                ["CardBackground"] = p.Card,
                // Приподнятая поверхность: фон обычной кнопки. До 5.0 брался
                // StaticResource SurfaceRaised из DesignTokens — тёмный во всех
                // темах, из-за чего в «Светлой» все кнопки оставались тёмными.
                ["SurfaceRaised"] = p.Raised,
                ["TextPrimary"] = p.TextPrimary,
                ["TextSecondary"] = p.TextSecondary,
                ["BorderBrush"] = p.Border,
                ["HeaderForeground"] = p.TextPrimary,
                // Подложка модального оверлея (перетаскивание установщика в окно).
                ["OverlayBackground"] = WithAlpha(p.Window, 0xE6),

                // Акцент темы. Он же — фирменный акцент клиента с 5.0.
                ["AccentColor"] = p.Accent,
                ["AccentForeground"] = ReadableOn(p.Accent),
                ["AccentPressed"] = Darken(p.Accent),
                ["AccentHoverBackground"] = WithAlpha(p.Accent, 0x14),
                ["AccentSoftBackground"] = WithAlpha(p.Accent, 0x24),
                ["AccentSoftBorder"] = WithAlpha(p.Accent, 0x40),

                // Статусы. Смысл цвета от темы не зависит (успех зелёный, ошибка
                // красная), но оттенок зависит: см. комментарий у «Светлой».
                ["StatusSuccess"] = p.Success,
                ["StatusWarning"] = p.Warning,
                ["StatusDanger"] = p.Danger,
                ["StatusInfo"] = p.Info,
                ["StatusSuccessForeground"] = ReadableOn(p.Success),
                ["StatusWarningForeground"] = ReadableOn(p.Warning),
                ["StatusDangerForeground"] = ReadableOn(p.Danger),
                // Info-заливка используется наравне с тремя остальными статусами
                // (значок «установлено» в каталоге, плашки журнала), поэтому пара
                // «заливка/читаемая надпись» у неё должна быть такой же полной.
                // Пропуск этого ключа не ломал ничего сегодня, но следующий, кто
                // возьмёт StatusInfo под заливку, не нашёл бы надписи к ней.
                ["StatusInfoForeground"] = ReadableOn(p.Info),
            };
        }

        /// <summary>
        /// Тема применена к ресурсам приложения: словарь уже подменён, можно
        /// перечитывать цвета.
        /// <para>
        /// Нужно тем местам, где кисть НЕ берётся биндингом на
        /// <c>DynamicResource</c>, а один раз вычисляется в C# (<c>BrushResolver</c>
        /// делает разовый <c>TryFindResource</c>) и запоминается — такие места
        /// сами по себе о смене темы не узнают и остаются в цветах той темы,
        /// что была активна в момент вычисления. Событие статическое, потому что
        /// смена темы — событие приложения, а не вкладки: подписчик не обязан
        /// иметь ссылку на <c>SystemViewModel</c>, который её переключил.
        /// </para>
        /// <para>
        /// Отписка обязательна только у объектов, которые живут короче приложения
        /// (окна). ViewModel вкладок создаются по одному экземпляру на сеанс и
        /// живут до выхода — им отписываться не от чего и негде.
        /// </para>
        /// </summary>
        public static event Action? ThemeChanged;

        /// <summary>Применяет тему к ресурсам приложения.</summary>
        public static void Apply(string? theme)
        {
            if (Application.Current is null) return;

            ResourceDictionary r = Application.Current.Resources;
            foreach (KeyValuePair<string, Color> entry in BuildPalette(theme))
            {
                r[entry.Key] = Frozen(entry.Value);
            }

            // Свечение вокруг логотипа и главной кнопки установки — не кисть, а
            // эффект, поэтому пересобирается отдельно. Без этого свечение осталось
            // бы зелёным поверх, например, синего акцента «Тёмной» темы.
            r["BrandAuraEffect"] = FrozenAura(PaletteFor(theme).Accent);

            // Строго после подмены словаря: подписчики в обработчике перечитывают
            // цвета и должны увидеть уже новые.
            ThemeChanged?.Invoke();
        }

        private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        private static Color WithAlpha(Color color, byte alpha) =>
            Color.FromArgb(alpha, color.R, color.G, color.B);

        /// <summary>Цвет нажатой кнопки: тот же акцент, притемнённый.</summary>
        private static Color Darken(Color color) => Color.FromRgb(
            (byte)(color.R * 0.82), (byte)(color.G * 0.82), (byte)(color.B * 0.82));

        /// <summary>
        /// Читаемая надпись поверх сплошной заливки: белая или почти чёрная —
        /// та из двух, у которой контраст по WCAG выше. Расчёт, а не таблица
        /// вручную: заливок (акцент + три статуса) четыре штуки на четыре темы,
        /// и подбирать шестнадцать пар на глаз — верный способ получить
        /// «белое по светло-зелёному» в одной из них.
        /// </summary>
        private static Color ReadableOn(Color background)
        {
            double bg = RelativeLuminance(background);
            double withWhite = Contrast(bg, RelativeLuminance(OnDark));
            double withDark = Contrast(bg, RelativeLuminance(OnLight));
            return withDark >= withWhite ? OnLight : OnDark;
        }

        /// <summary>Контраст двух яркостей по формуле WCAG 2.1.</summary>
        public static double Contrast(double firstLuminance, double secondLuminance)
        {
            double lighter = Math.Max(firstLuminance, secondLuminance);
            double darker = Math.Min(firstLuminance, secondLuminance);
            return (lighter + 0.05) / (darker + 0.05);
        }

        /// <summary>Относительная яркость цвета по формуле WCAG 2.1.</summary>
        public static double RelativeLuminance(Color color) =>
            0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);

        private static double Channel(byte value)
        {
            double v = value / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static DropShadowEffect FrozenAura(Color color)
        {
            var effect = new DropShadowEffect
            {
                Color = color,
                BlurRadius = 24,
                ShadowDepth = 0,
                Opacity = 0.22,
            };
            effect.Freeze();
            return effect;
        }
    }
}
