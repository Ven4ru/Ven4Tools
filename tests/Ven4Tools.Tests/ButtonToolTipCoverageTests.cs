using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Ven4Tools.Tests;

public sealed class ButtonToolTipCoverageTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AllFunctionalXamlButtonsHaveExplanations()
    {
        string repositoryRoot = FindRepositoryRoot();
        List<string> missing = EnumerateApplicationXaml(repositoryRoot)
            .SelectMany(ReadButtons)
            .Where(IsFunctional)
            .Where(button => string.IsNullOrWhiteSpace(button.ToolTip))
            .Select(button => button.Diagnostic)
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Функциональные кнопки без пояснения:" + Environment.NewLine +
            string.Join(Environment.NewLine, missing));
    }

    [Theory]
    [InlineData("Ven4Tools/Views/PinsStripController.cs", "installBtn")]
    [InlineData("Ven4Tools/Views/PinsStripController.cs", "unpinBtn")]
    [InlineData("Ven4Tools/Views/Tabs/DiagnosticsTab.RebootHistory.cs", "fixBtn")]
    public void DynamicButtonsHaveExplanations(string relativePath, string variableName)
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        string source = File.ReadAllText(path);
        string initializerPattern =
            $@"\b{Regex.Escape(variableName)}\s*=\s*new\s+Button\s*\{{(?<body>.*?)\n\s*\}}";
        Match initializer = Regex.Match(
            source,
            initializerPattern,
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.True(initializer.Success, $"Не найден инициализатор кнопки {variableName} в {relativePath}.");
        Assert.Matches(
            new Regex(@"\bToolTip\s*=\s*\$?""[^""]+""", RegexOptions.CultureInvariant),
            initializer.Groups["body"].Value);
    }

    [Theory]
    [InlineData("Ven4Tools/App.xaml")]
    [InlineData("Ven4Tools.Launcher/App.xaml")]
    public void ToolTipStyleKeepsLongExplanationsReadable(string relativePath)
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        XDocument document = XDocument.Load(path);
        XElement style = document.Descendants(Presentation + "Style")
            .Single(element => (string?)element.Attribute("TargetType") == "ToolTip");
        Dictionary<string, string?> setters = style.Elements(Presentation + "Setter")
            .ToDictionary(
                element => (string)element.Attribute("Property")!,
                element => (string?)element.Attribute("Value"),
                StringComparer.Ordinal);
        XElement text = style.Descendants(Presentation + "TextBlock").Single();

        Assert.Equal("380", setters["MaxWidth"]);
        Assert.Equal("10,7", setters["Padding"]);
        Assert.Equal("15000", setters["ToolTipService.ShowDuration"]);
        Assert.Equal("Wrap", (string?)text.Attribute("TextWrapping"));
        Assert.Equal("360", (string?)text.Attribute("MaxWidth"));
    }

    [Theory]
    [InlineData("Ven4Tools/App.xaml")]
    [InlineData("Ven4Tools.Launcher/App.xaml")]
    public void ButtonStyleUsesCalmToolTipTiming(string relativePath)
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        XDocument document = XDocument.Load(path);
        XElement style = document.Descendants(Presentation + "Style")
            .Single(element =>
                (string?)element.Attribute("TargetType") == "Button" &&
                element.Attribute(Xaml + "Key") is null);
        Dictionary<string, string?> setters = style.Elements(Presentation + "Setter")
            .ToDictionary(
                element => (string)element.Attribute("Property")!,
                element => (string?)element.Attribute("Value"),
                StringComparer.Ordinal);

        Assert.Equal("450", setters["ToolTipService.InitialShowDelay"]);
        Assert.Equal("100", setters["ToolTipService.BetweenShowDelay"]);
        Assert.Equal("15000", setters["ToolTipService.ShowDuration"]);
    }

    // Проверка присутствия подсказки (тест выше) не ловит расхождение стиля:
    // кнопки, у которых подсказка была ещё до перехода на пояснения, остались
    // с коротким «Удалить пресет» вместо «что произойдёт»-формулировки и не
    // попали ни в один список пропущенных. Требуем завершённое предложение —
    // это объективный признак принятой формулировки.
    [Fact]
    public void FunctionalXamlButtonExplanationsAreCompleteSentences()
    {
        string repositoryRoot = FindRepositoryRoot();
        List<string> terse = EnumerateApplicationXaml(repositoryRoot)
            .SelectMany(ReadButtons)
            .Where(IsFunctional)
            .Where(button => !string.IsNullOrWhiteSpace(button.ToolTip))
            // Привязки вычисляются во время выполнения — текст здесь недоступен.
            .Where(button => !button.ToolTip!.TrimStart().StartsWith("{", StringComparison.Ordinal))
            .Where(button => !button.ToolTip!.TrimEnd().EndsWith(".", StringComparison.Ordinal))
            .Select(button => $"{button.Diagnostic}; ToolTip={button.ToolTip}")
            .OrderBy(diagnostic => diagnostic, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            terse.Count == 0,
            "Подсказки не в формате завершённого предложения:" + Environment.NewLine +
            string.Join(Environment.NewLine, terse));
    }

    // Стиль с x:Key не наследует сеттеры неявного стиля Button, поэтому тайминги
    // подсказок нужно держать в нём явно — иначе у кнопок деструктивных действий
    // подсказка закрывается через 5 секунд вместо 15.
    [Theory]
    [InlineData("Ven4Tools/App.xaml", "DangerButtonStyle")]
    public void KeyedButtonStylesKeepCalmToolTipTiming(string relativePath, string styleKey)
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        XDocument document = XDocument.Load(path);
        XElement style = document.Descendants(Presentation + "Style")
            .Single(element =>
                (string?)element.Attribute("TargetType") == "Button" &&
                (string?)element.Attribute(Xaml + "Key") == styleKey);
        Dictionary<string, string?> setters = style.Elements(Presentation + "Setter")
            .ToDictionary(
                element => (string)element.Attribute("Property")!,
                element => (string?)element.Attribute("Value"),
                StringComparer.Ordinal);

        Assert.Equal("450", setters["ToolTipService.InitialShowDelay"]);
        Assert.Equal("100", setters["ToolTipService.BetweenShowDelay"]);
        Assert.Equal("15000", setters["ToolTipService.ShowDuration"]);
    }

    private static IEnumerable<string> EnumerateApplicationXaml(string repositoryRoot)
    {
        foreach (string directoryName in new[] { "Ven4Tools", "Ven4Tools.Launcher" })
        {
            string directory = Path.Combine(repositoryRoot, directoryName);
            foreach (string path in Directory.EnumerateFiles(directory, "*.xaml", SearchOption.AllDirectories))
            {
                if (!path.Contains(
                        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase))
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<ButtonInfo> ReadButtons(string path)
    {
        XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
        foreach (XElement button in document.Descendants(Presentation + "Button"))
        {
            string? name = (string?)button.Attribute(Xaml + "Name");
            string? content = (string?)button.Attribute("Content");
            string? click = (string?)button.Attribute("Click");
            string? command = (string?)button.Attribute("Command");
            string? toolTip = (string?)button.Attribute("ToolTip");
            int line = ((IXmlLineInfo)button).LineNumber;

            yield return new ButtonInfo(path, line, name, content, click, command, toolTip);
        }
    }

    private static bool IsFunctional(ButtonInfo button)
    {
        bool hasAction =
            !string.IsNullOrWhiteSpace(button.Click) ||
            !string.IsNullOrWhiteSpace(button.Command) ||
            !string.IsNullOrWhiteSpace(button.Name);
        if (!hasAction)
        {
            return false;
        }

        if (button.Click?.StartsWith("NavigateTo", StringComparison.Ordinal) == true ||
            button.Name?.EndsWith("Tab", StringComparison.Ordinal) == true)
        {
            return false;
        }

        if (button.Name?.StartsWith("btnClose", StringComparison.Ordinal) == true ||
            string.Equals(button.Name, "btnExit", StringComparison.Ordinal) ||
            Regex.IsMatch(button.Name ?? "", "^star[1-5]$", RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (button.Content is "Закрыть" or "Позже" or "Готово" or "Отклонить")
        {
            return false;
        }

        if (button.Content is "Отмена" &&
            button.Name is not "btnCancelDownload" and not "btnCancelOffice" and not "btnCancelInstall")
        {
            return false;
        }

        if (button.Content is "Пропустить" &&
            !button.Path.EndsWith(
                $"{Path.DirectorySeparatorChar}SplashWindow.xaml",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Ven4Tools.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Не найден корень репозитория с Ven4Tools.sln.");
    }

    private sealed record ButtonInfo(
        string Path,
        int Line,
        string? Name,
        string? Content,
        string? Click,
        string? Command,
        string? ToolTip)
    {
        public string Diagnostic =>
            $"{System.IO.Path.GetRelativePath(FindRepositoryRoot(), Path)}:{Line} " +
            $"Name={Name ?? "—"}; Content={Content ?? "—"}; Click={Click ?? "—"}; Command={Command ?? "—"}";
    }
}
