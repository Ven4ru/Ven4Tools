using System.Text.RegularExpressions;
using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Гарантии обещания версии 5.0 «переключатель темы красит весь интерфейс без
/// исключений». Проверяется два класса регрессий:
/// <list type="bullet">
/// <item>палитра — все темы описывают один и тот же набор ключей и остаются
/// читаемыми (тема без ключа или с нечитаемой парой «заливка/надпись» —
/// это и есть «белое по белому», которое сборка не ловит);</item>
/// <item>разметка — клиент не ссылается на нетемизируемый фирменный зелёный и
/// не берёт темизируемые ключи через StaticResource (StaticResource
/// запоминает цвет на этапе загрузки XAML, и элемент навсегда остаётся
/// цвета той темы, что была активна при первом показе).</item>
/// </list>
/// </summary>
public sealed class ThemePaletteTests
{
    /// <summary>Порог WCAG AA для обычного текста.</summary>
    private const double NormalTextContrast = 4.5;

    /// <summary>Порог WCAG AA для крупного/жирного текста и графических элементов.</summary>
    private const double LargeTextContrast = 3.0;

    private static readonly string[] Surfaces =
    {
        "WindowBackground", "SidebarBackground", "ContentBackground",
        "CardBackground", "SurfaceRaised",
    };

    public static TheoryData<string> Themes()
    {
        var data = new TheoryData<string>();
        foreach (string theme in ThemeService.AllThemes) data.Add(theme);
        return data;
    }

    [Fact]
    public void ВсеТемыОписываютОдинаковыйНаборКлючей()
    {
        string[] reference = ThemeService.BuildPalette(ThemeService.AllThemes[0])
            .Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(reference);

        foreach (string theme in ThemeService.AllThemes.Skip(1))
        {
            string[] actual = ThemeService.BuildPalette(theme)
                .Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
            Assert.Equal(reference, actual);
        }
    }

    // Значение настройки приходит из profile.json и может быть каким угодно —
    // до 5.0 неизвестное значение молча давало тёмную тему, это поведение сохранено.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не-существующая-тема")]
    public void НеизвестнаяТемаДаётТёмную(string? theme)
    {
        Assert.Equal(
            ThemeService.BuildPalette(ThemeService.ThemeDark),
            ThemeService.BuildPalette(theme));
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void НадписьНаЦветнойЗаливкеЧитаема(string theme)
    {
        IReadOnlyDictionary<string, Color> palette = ThemeService.BuildPalette(theme);

        (string Fill, string Foreground)[] pairs =
        {
            ("AccentColor", "AccentForeground"),
            ("StatusSuccess", "StatusSuccessForeground"),
            ("StatusWarning", "StatusWarningForeground"),
            ("StatusDanger", "StatusDangerForeground"),
            ("StatusInfo", "StatusInfoForeground"),
        };

        foreach ((string fill, string foreground) in pairs)
        {
            double contrast = Contrast(palette[fill], palette[foreground]);
            Assert.True(
                contrast >= NormalTextContrast,
                $"Тема «{theme}»: надпись {foreground} на заливке {fill} — контраст {contrast:F2}, нужен {NormalTextContrast}.");
        }
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void ТекстЧитаемНаВсехПодложкахТемы(string theme)
    {
        IReadOnlyDictionary<string, Color> palette = ThemeService.BuildPalette(theme);

        foreach (string surface in Surfaces)
        {
            double primary = Contrast(palette["TextPrimary"], palette[surface]);
            Assert.True(
                primary >= NormalTextContrast,
                $"Тема «{theme}»: TextPrimary на {surface} — контраст {primary:F2}, нужен {NormalTextContrast}.");

            double secondary = Contrast(palette["TextSecondary"], palette[surface]);
            Assert.True(
                secondary >= LargeTextContrast,
                $"Тема «{theme}»: TextSecondary на {surface} — контраст {secondary:F2}, нужен {LargeTextContrast}.");
        }
    }

    // Акцент и статусы рисуются не только заливкой, но и текстом/значками поверх
    // подложки: подписи категорий, «● Активен», доступность строки каталога.
    [Theory]
    [MemberData(nameof(Themes))]
    public void АкцентИСтатусыВидныКакТекстНаПодложках(string theme)
    {
        IReadOnlyDictionary<string, Color> palette = ThemeService.BuildPalette(theme);
        string[] inks = { "AccentColor", "StatusSuccess", "StatusWarning", "StatusDanger", "StatusInfo" };

        foreach (string ink in inks)
        {
            foreach (string surface in Surfaces)
            {
                double contrast = Contrast(palette[ink], palette[surface]);
                Assert.True(
                    contrast >= LargeTextContrast,
                    $"Тема «{theme}»: {ink} поверх {surface} — контраст {contrast:F2}, нужен {LargeTextContrast}.");
            }
        }
    }

    /// <summary>
    /// Общий с лаунчером словарь: клиент подключает его как <c>Resources/DesignTokens.xaml</c>
    /// (Ven4Tools.csproj), поэтому он входит в область проверок наравне с <c>Ven4Tools/</c>.
    /// Здесь же объявлены BrandGreen/BrandGreenDeep — они нужны ЛАУНЧЕРУ, у которого
    /// переключателя тем нет, и само объявление ссылкой клиента не является.
    /// </summary>
    private const string SharedTokensFile = "Shared/DesignTokens.xaml";

    [Fact]
    public void РазметкаКлиентаНеСсылаетсяНаФирменныйЗелёный()
    {
        // BrandGreen/BrandGreenDeep остаются в Shared/DesignTokens.xaml ради
        // лаунчера — у него переключателя тем нет. В клиенте ссылка на них
        // означала бы элемент, который темой не красится.
        List<string> hits = new();
        foreach (string path in EnumerateClientSources())
        {
            string relative = Relative(path);
            // Комментарии не считаются ссылкой: и здесь, и в ThemeService они как раз
            // объясняют, почему от BrandGreen ушли (у .cs комментарии не вырезаются —
            // поэтому ThemeService.cs исключён целиком, см. EnumerateClientSources).
            foreach ((string text, int number) in LinesWithoutComments(path))
            {
                if (!text.Contains("BrandGreen", StringComparison.Ordinal)) continue;
                if (relative.Equals(SharedTokensFile, StringComparison.OrdinalIgnoreCase)
                    && text.Contains("x:Key=", StringComparison.Ordinal))
                {
                    continue;
                }

                hits.Add($"{relative}:{number}");
            }
        }

        Assert.True(
            hits.Count == 0,
            "Ссылки на нетемизируемый BrandGreen в клиенте:" + Environment.NewLine + string.Join(Environment.NewLine, hits));
    }

    [Fact]
    public void ТемизируемыеКлючиБерутсяТолькоЧерезDynamicResource()
    {
        // BrandAuraEffect тоже пересобирается ThemeService под акцент темы,
        // поэтому попадает в тот же список, хотя это эффект, а не кисть.
        var themed = new HashSet<string>(ThemeService.BuildPalette(ThemeService.ThemeDark).Keys, StringComparer.Ordinal)
        {
            "BrandAuraEffect",
        };
        var reference = new Regex(@"\{StaticResource\s+(?<key>[A-Za-z0-9_]+)\s*\}", RegexOptions.CultureInvariant);

        List<string> hits = new();
        foreach (string path in EnumerateClientXaml())
        {
            foreach ((string text, int number) in Lines(path))
            {
                foreach (Match match in reference.Matches(text))
                {
                    string key = match.Groups["key"].Value;
                    if (themed.Contains(key)) hits.Add($"{Relative(path)}:{number} — {key}");
                }
            }
        }

        Assert.True(
            hits.Count == 0,
            "Темизируемый ключ взят через StaticResource (цвет застынет на теме, активной при загрузке разметки):"
                + Environment.NewLine + string.Join(Environment.NewLine, hits));
    }

    // Реестр осознанных исключений из «красим темой всё». Каждая запись — цвет,
    // который остаётся постоянным во всех темах, и причина. Новый цветовой литерал
    // в разметке клиента уронит тест: либо он должен стать ссылкой на ключ темы,
    // либо его надо добавить сюда с обоснованием.
    private static readonly Dictionary<string, string[]> AllowedLiterals = new(StringComparer.OrdinalIgnoreCase)
    {
        // Контур и заливка кнопки деструктивного действия при наведении. Тёмно-красная
        // заливка с белой надписью читается одинаково во всех четырёх темах, а брать
        // сюда StatusDanger нельзя: в тёмных темах он светло-красный, и десятки
        // строк списка превратились бы в «стену предупреждений» (см. App.xaml).
        ["Ven4Tools/App.xaml"] = new[] { "#7A2B2B", "#C42B2B", "White" },

        // Значки уровня риска твика (безопасно/умеренно/осторожно). Тёмная заливка
        // с белой надписью, тот же смысл и та же читаемость в любой теме.
        ["Ven4Tools/Views/Tabs/DebloaterTab.xaml"] = new[] { "#1B5E20", "#E65100", "#B71C1C", "White" },

        // Подсказка в пустом поле поиска. Средне-серый одинаково читается и на белой
        // карточке «Светлой» темы, и на почти чёрной подложке тёмных; взять отсюда
        // TextSecondary нельзя надёжно — TextBlock живёт внутри VisualBrush.Visual,
        // вне дерева, и наследование ресурсов там не работает как обычно.
        ["Ven4Tools/Views/Tabs/InstalledTab.xaml"] = new[] { "Gray" },

        // Общий с лаунчером словарь. Объявления кистей палитры отсекаются отдельно
        // (см. PaletteDefinitionFiles), сюда попадают только два эффекта, у которых
        // литерал стоит на второй строке объявления: свечение фирменного зелёного —
        // акцент ЛАУНЧЕРА (в клиенте BrandAuraEffect пересобирает ThemeService.Apply
        // под акцент темы), чёрная тень панели одинакова в любой теме.
        [SharedTokensFile] = new[] { "#4ADE80", "#000000" },
    };

    /// <summary>
    /// Файлы, чья работа — ОБЪЯВЛЯТЬ палитру: стартовые значения App.xaml (до первого
    /// <c>ThemeService.Apply()</c>) и общий с лаунчером словарь. Цветовой литерал в
    /// объявлении ресурса там уместен по назначению файла.
    /// <para>
    /// Проверка по ПУТИ, а не по одному виду строки: прежний безусловный пропуск
    /// строк с «<c>&lt;SolidColorBrush x:Key=</c>» освобождал от проверки такую строку
    /// в ЛЮБОМ файле разметки клиента, хотя задумывался только под App.xaml.
    /// </para>
    /// </summary>
    private static readonly string[] PaletteDefinitionFiles =
    {
        "Ven4Tools/App.xaml",
        SharedTokensFile,
    };

    private const string PaletteBrushDeclaration = "<SolidColorBrush x:Key=";

    /// <summary>
    /// Именованные цвета WPF. «Прозрачный» в список не входит — он не цвет, а
    /// отсутствие заливки, и от темы не зависит.
    /// </summary>
    private static readonly string[] NamedColors =
    {
        "White", "Black", "Gray", "Red", "Green", "Blue", "Orange", "Yellow",
        "LightGreen", "LightCoral", "DarkRed", "DarkGreen", "Lime", "Silver",
    };

    [Fact]
    public void ВРазметкеКлиентаНетНеучтённыхЦветовыхЛитералов()
    {
        // Три и четыре разряда тоже: сокращённая запись #444 однажды уже прошла
        // мимо проверки на шесть разрядов (погашенные звёзды в окне отзыва).
        // Именованные цвета ловятся только в виде значения атрибута (Foreground="White"),
        // иначе в улов попадали бы слова из русского текста подсказок.
        var literal = new Regex(
            @"#(?:[0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{3})\b"
                + @"|=""(?<named>" + string.Join("|", NamedColors) + @")""",
            RegexOptions.CultureInvariant);
        List<string> hits = new();

        foreach (string path in EnumerateClientXaml())
        {
            string relative = Relative(path);
            AllowedLiterals.TryGetValue(relative, out string[]? allowed);
            bool isPaletteFile = PaletteDefinitionFiles.Contains(relative, StringComparer.OrdinalIgnoreCase);

            foreach ((string text, int number) in LinesWithoutComments(path))
            {
                // Объявление ключа палитры — но только в файле, который её и объявляет.
                if (isPaletteFile && text.Contains(PaletteBrushDeclaration, StringComparison.Ordinal)) continue;

                foreach (Match match in literal.Matches(text))
                {
                    string value = match.Groups["named"].Success ? match.Groups["named"].Value : match.Value;
                    if (allowed?.Contains(value, StringComparer.OrdinalIgnoreCase) == true) continue;
                    hits.Add($"{relative}:{number} — {value}");
                }
            }
        }

        Assert.True(
            hits.Count == 0,
            "Цвет зашит в разметку мимо темы (заменить на DynamicResource или внести в AllowedLiterals с обоснованием):"
                + Environment.NewLine + string.Join(Environment.NewLine, hits));
    }

    private static double Contrast(Color first, Color second) =>
        ThemeService.Contrast(
            ThemeService.RelativeLuminance(first),
            ThemeService.RelativeLuminance(second));

    /// <summary>
    /// Строки файла с вырезанными XML-комментариями (нумерация сохраняется).
    /// Комментарии в разметке цитируют старые цвета, объясняя, почему от них ушли, —
    /// принимать эти цитаты за зашитый цвет нельзя.
    /// </summary>
    private static IEnumerable<(string Text, int Number)> LinesWithoutComments(string path)
    {
        string content = File.ReadAllText(path);
        string stripped = Regex.Replace(
            content,
            "<!--.*?-->",
            match => Regex.Replace(match.Value, "[^\r\n]", " "),
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        string[] lines = stripped.Split('\n');
        for (int i = 0; i < lines.Length; i++) yield return (lines[i], i + 1);
    }

    private static IEnumerable<(string Text, int Number)> Lines(string path)
    {
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++) yield return (lines[i], i + 1);
    }

    private static string Relative(string path) =>
        Path.GetRelativePath(FindRepositoryRoot(), path).Replace(Path.DirectorySeparatorChar, '/');

    private static IEnumerable<string> EnumerateClientXaml() =>
        EnumerateClientFiles("*.xaml");

    private static IEnumerable<string> EnumerateClientSources() =>
        EnumerateClientFiles("*.xaml").Concat(EnumerateClientFiles("*.cs"))
            // Комментарии самого ThemeService объясняют, почему BrandGreen больше
            // не используется, — это не ссылка на ресурс.
            .Where(path => !path.EndsWith("ThemeService.cs", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Файлы, из которых собирается клиент. Кроме собственного каталога сюда входит
    /// <c>Shared/</c>: клиент подключает оттуда и разметку (<c>DesignTokens.xaml</c>
    /// как <c>Resources/DesignTokens.xaml</c>), и код — см. Ven4Tools.csproj. Пока
    /// <c>Shared/</c> был вне проверки, StaticResource на темизируемый ключ или
    /// новый цветовой литерал в общем словаре проходили в клиент незамеченными.
    /// </summary>
    private static readonly string[] ClientDirectories = { "Ven4Tools", "Shared" };

    private static IEnumerable<string> EnumerateClientFiles(string pattern)
    {
        foreach (string name in ClientDirectories)
        {
            string directory = Path.Combine(FindRepositoryRoot(), name);
            foreach (string path in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
            {
                if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return path;
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Ven4Tools.sln"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Не найден корень репозитория с Ven4Tools.sln.");
    }
}
