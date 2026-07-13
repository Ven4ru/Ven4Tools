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
using System.Linq;
using System.Runtime.InteropServices;
using Xunit;

namespace Ven4Tools.UITests;

public sealed class LauncherSmokeTests : IDisposable
{
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;

    private readonly string _testRoot;
    private readonly Application _application;
    private readonly UIA3Automation _automation;
    private readonly Window _window;

    public LauncherSmokeTests()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            $"Ven4Tools.UI.Tests-{Guid.NewGuid():N}");
        string executable = FindLauncher();
        var startInfo = new ProcessStartInfo(executable);
        startInfo.Environment["VEN4TOOLS_UI_TEST"] = "1";
        startInfo.Environment["VEN4TOOLS_UI_TEST_ROOT"] = _testRoot;
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
        AssertPrimaryControlsAreAvailable();

        string root = FindRepositoryRoot();
        string snapshotDirectory = Path.Combine(root, "tests", "Ven4Tools.UITests", "Snapshots");
        string resultDirectory = Path.Combine(root, "TestResults", "Snapshots");
        Directory.CreateDirectory(snapshotDirectory);
        Directory.CreateDirectory(resultDirectory);
        string actual = Path.Combine(resultDirectory, "launcher-main.actual.png");
        string baseline = Path.Combine(snapshotDirectory, "launcher-main.png");

        IntPtr windowHandle = new(_window.Properties.NativeWindowHandle.Value);
        Assert.NotEqual(IntPtr.Zero, windowHandle);
        var originalBounds = _window.BoundingRectangle;
        Assert.True(SetWindowPos(
            windowHandle,
            HwndTopmost,
            100,
            100,
            0,
            0,
            SwpNoSize | SwpShowWindow));
        try
        {
            _window.Focus();
            Thread.Sleep(TimeSpan.FromMilliseconds(250));
            // Первый захват после смены foreground иногда получает предыдущую
            // поверхность DWM. Повторный кадр снимается уже с активного HWND.
            Capture.Element(_window);
            Thread.Sleep(TimeSpan.FromMilliseconds(250));
            Capture.Element(_window).ToFile(actual);
        }
        finally
        {
            SetWindowPos(
                windowHandle,
                HwndNotTopmost,
                (int)originalBounds.X,
                (int)originalBounds.Y,
                0,
                0,
                SwpNoSize | SwpShowWindow);
        }
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
        // GitHub runner использует программный рендеринг WPF, поэтому сглаживание
        // текста и градиентов отличается от локального GPU при неизменном layout.
        // Размер окна, наличие контролов и их wiring проверяются отдельно выше.
        Assert.True(changedRatio <= 0.08,
            $"Изменилось {changedRatio:P3} пикселей клиентской области; допустимо не более 8.000%.");

        ExercisePrimaryControlBindings();
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
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Остаток тестового sandbox не должен маскировать результат UI-теста.
        }
    }

    private static string FindLauncher()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("LAUNCHER_UNDER_TEST");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string resolvedPath = Path.GetFullPath(explicitPath);
            return File.Exists(resolvedPath)
                ? resolvedPath
                : throw new FileNotFoundException(
                    "Указанный launcher для UI-теста не найден.",
                    resolvedPath);
        }

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

    private void AssertPrimaryControlsAreAvailable()
    {
        string[] requiredEnabledControls =
        [
            "btnSelectFolder",
            "btnFindClient",
            "btnCheckUpdates",
            "btnLaunchApp",
            "btnChangelog",
            "btnOpenSettings",
            "btnDeleteClient",
            "btnExit"
        ];

        foreach (string automationId in requiredEnabledControls)
        {
            AutomationElement? element = _window.FindFirstDescendant(
                condition => condition.ByAutomationId(automationId));
            Assert.True(element is not null, $"Не найден обязательный контрол {automationId}.");
            Assert.True(element.IsEnabled, $"Контрол {automationId} недоступен.");
        }

        foreach (string automationId in new[] { "txtInstalledVersion", "txtClientVersion" })
        {
            Assert.True(
                _window.FindFirstDescendant(
                    condition => condition.ByAutomationId(automationId)) is not null,
                $"Не найден обязательный контрол {automationId}.");
        }
    }

    private void ExercisePrimaryControlBindings()
    {
        foreach (string automationId in new[]
        {
            "btnSelectFolder",
            "btnFindClient",
            "btnCheckUpdates",
            "btnLaunchApp",
            "btnDeleteClient"
        })
        {
            _window.FindFirstDescendant(
                    condition => condition.ByAutomationId(automationId))!
                .AsButton()
                .Invoke();
            Assert.False(_application.HasExited, $"Кнопка {automationId} завершила launcher.");
        }

        Button changelog = _window.FindFirstDescendant(
                condition => condition.ByAutomationId("btnChangelog"))!
            .AsButton();
        changelog.Invoke();
        Button closeDetails = Retry.WhileNull(
            () => _window.FindFirstDescendant(
                    condition => condition.ByAutomationId("btnCloseDetails"))?
                .AsButton(),
            timeout: TimeSpan.FromSeconds(3)).Result
            ?? throw new InvalidOperationException("Кнопка закрытия истории изменений не появилась.");
        closeDetails.Invoke();

        ExerciseSettingsWindow();
    }

    private void ExerciseSettingsWindow()
    {
        _window.FindFirstDescendant(condition => condition.ByAutomationId("btnOpenSettings"))!
            .AsButton()
            .Invoke();

        // Окно настроек создаётся с Owner = главное окно, поэтому UIA размещает его
        // как потомка главного окна, а не как самостоятельное верхнеуровневое окно.
        // Ищем его среди потомков-окон главного окна, а не через GetAllTopLevelWindows.
        Window settingsWindow = Retry.WhileNull(
            () => _window.FindAllDescendants(condition => condition.ByControlType(ControlType.Window))
                .Select(element => element.AsWindow())
                .FirstOrDefault(w => w.Title.Contains("Настройки", StringComparison.OrdinalIgnoreCase)),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(250)).Result
            ?? throw new InvalidOperationException("Окно «Настройки» не открылось.");

        foreach (string automationId in new[]
        {
            "chkBackgroundUpdates",
            "chkStartMinimized",
            "chkAutostart"
        })
        {
            CheckBox checkBox = settingsWindow.FindFirstDescendant(
                    condition => condition.ByAutomationId(automationId))!
                .AsCheckBox();
            ToggleState initialState = checkBox.ToggleState;
            checkBox.Toggle();
            checkBox.Toggle();
            Assert.Equal(initialState, checkBox.ToggleState);
        }

        // Переключатель режима обновления клиента переживает переключение туда-обратно.
        RadioButton manual = settingsWindow.FindFirstDescendant(
                condition => condition.ByAutomationId("rbAutoUpdateManual"))!
            .AsRadioButton();
        RadioButton auto = settingsWindow.FindFirstDescendant(
                condition => condition.ByAutomationId("rbAutoUpdateAuto"))!
            .AsRadioButton();
        bool wasManual = manual.IsChecked == true;
        auto.Click();
        Assert.True(auto.IsChecked);
        manual.Click();
        Assert.True(manual.IsChecked);
        if (!wasManual) auto.Click();

        settingsWindow.FindFirstDescendant(condition => condition.ByAutomationId("btnCloseSettings"))!
            .AsButton()
            .Invoke();
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
