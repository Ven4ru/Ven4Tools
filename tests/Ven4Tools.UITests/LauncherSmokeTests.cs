using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Diagnostics;
using Xunit;

namespace Ven4Tools.UITests;

public sealed class LauncherSmokeTests : IDisposable
{
    private readonly Application _application;
    private readonly UIA3Automation _automation;
    private readonly Window _window;

    public LauncherSmokeTests()
    {
        string executable = FindLauncher();
        var startInfo = new ProcessStartInfo(executable);
        startInfo.Environment["VEN4TOOLS_UI_TEST"] = "1";
        _application = Application.Launch(startInfo);
        _automation = new UIA3Automation();
        _window = Retry.WhileNull(
            () => _application.GetMainWindow(_automation),
            timeout: TimeSpan.FromSeconds(20),
            interval: TimeSpan.FromMilliseconds(250)).Result
            ?? throw new InvalidOperationException("Главное окно лаунчера не появилось.");
        Retry.WhileFalse(
            () => _window.FindFirstDescendant(
                condition => condition.ByAutomationId("btnSelectFolder"))?.IsEnabled == true,
            timeout: TimeSpan.FromSeconds(20),
            interval: TimeSpan.FromMilliseconds(250));
        Thread.Sleep(TimeSpan.FromSeconds(1)); // Дождаться завершения системной анимации появления окна.
    }

    [Fact]
    public void MainWindow_IsVisibleUsableAndMatchesSnapshot()
    {
        Assert.Contains("Ven4Tools", _window.Title, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(WindowVisualState.Minimized, _window.Patterns.Window.PatternOrDefault?.WindowVisualState.Value);
        Assert.True(_window.BoundingRectangle.Width >= 600);
        Assert.True(_window.BoundingRectangle.Height >= 400);

        string root = FindRepositoryRoot();
        string snapshotDirectory = Path.Combine(root, "tests", "Ven4Tools.UITests", "Snapshots");
        string resultDirectory = Path.Combine(root, "TestResults", "Snapshots");
        Directory.CreateDirectory(snapshotDirectory);
        Directory.CreateDirectory(resultDirectory);
        string actual = Path.Combine(resultDirectory, "launcher-main.actual.png");
        string baseline = Path.Combine(snapshotDirectory, "launcher-main.png");

        Capture.Element(_window).ToFile(actual);
        using (Image<Rgba32> captured = Image.Load<Rgba32>(actual))
        {
            const int frameMargin = 10;
            captured.Mutate(operation => operation.Crop(new Rectangle(
                frameMargin,
                frameMargin,
                captured.Width - (frameMargin * 2),
                captured.Height - (frameMargin * 2))));
            captured.Save(actual);
        }

        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
        {
            File.Copy(actual, baseline, overwrite: true);
        }

        Assert.True(File.Exists(baseline),
            $"Эталон отсутствует. Запустите тест с UPDATE_SNAPSHOTS=1 и проверьте {actual}.");
        using Image<Rgba32> expected = Image.Load<Rgba32>(baseline);
        using Image<Rgba32> observed = Image.Load<Rgba32>(actual);
        Assert.Equal(expected.Size, observed.Size);
        Assert.True(expected.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> expectedPixels));
        Assert.True(observed.DangerousTryGetSinglePixelMemory(out Memory<Rgba32> observedPixels));

        const int channelTolerance = 10;
        int compared = 0;
        int changed = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                int index = (y * expected.Width) + x;
                Rgba32 expectedPixel = expectedPixels.Span[index];
                Rgba32 observedPixel = observedPixels.Span[index];
                compared++;
                if (Math.Abs(expectedPixel.R - observedPixel.R) > channelTolerance ||
                    Math.Abs(expectedPixel.G - observedPixel.G) > channelTolerance ||
                    Math.Abs(expectedPixel.B - observedPixel.B) > channelTolerance ||
                    Math.Abs(expectedPixel.A - observedPixel.A) > channelTolerance)
                {
                    changed++;
                }
            }
        }

        double changedRatio = (double)changed / compared;
        Assert.True(changedRatio <= 0.001,
            $"Изменилось {changedRatio:P3} пикселей клиентской области; допустимо не более 0.100%.");
    }

    public void Dispose()
    {
        _application.Close();
        if (!_application.HasExited)
        {
            _application.Kill();
        }
        _automation.Dispose();
        _application.Dispose();
    }

    private static string FindLauncher()
    {
        string root = FindRepositoryRoot();
        string path = Path.Combine(
            root,
            "Ven4Tools.Launcher",
            "bin",
            "Release",
            "net8.0-windows",
            "win-x64",
            "Ven4Tools.Launcher.exe");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Сначала соберите solution в Release.", path);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ven4Tools.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Корень репозитория не найден.");
    }
}
