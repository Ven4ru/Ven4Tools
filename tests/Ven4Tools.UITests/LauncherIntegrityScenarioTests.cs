using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using System.Diagnostics;
using System.Linq;
using Xunit;

namespace Ven4Tools.UITests;

/// <summary>
/// Живые сценарии кнопки «Проверить и восстановить клиент» (Настройки → «Диагностика
/// клиента»): настоящий процесс лаунчера, настоящая папка установки на диске, клик
/// через UI Automation.
///
/// Отличие от юнит-тестов ClientIntegrityCheckerTests: там подменены и диск, и сеть, и
/// проверяется выбор вердикта. Здесь проверяется то, чего юнит увидеть не может, —
/// что вердикт доезжает до пользователя нужным текстом и нужным состоянием кнопки
/// «Исправить». Ровно на этом уровне и жил баг ложного «сервер недоступен»: логика
/// принимала решение по фиктивной версии «0.0.0», подставленной в UI-слое.
///
/// Папка клиента — синтетическая: лаунчер в тестовом режиме берёт её из
/// VEN4TOOLS_UI_TEST_ROOT (см. MainWindow.xaml.cs), поэтому настоящая установка
/// пользователя (Documents\Ven4Tools_Client) не затрагивается ни при одном исходе.
/// </summary>
public sealed class LauncherIntegrityScenarioTests : IDisposable
{
    // Проверка успевает сходить за списком версий (CDN 5 с + GitHub до 20 с) прежде,
    // чем покажет вердикт. Запас взят с большим верхом: тест не должен краснеть из-за
    // медленной сети раннера, у него другая тема.
    private static readonly TimeSpan VerdictTimeout = TimeSpan.FromSeconds(120);

    private const string InProgressStatus = "Проверка";

    private readonly string _testRoot;
    private Application? _application;
    private UIA3Automation? _automation;

    public LauncherIntegrityScenarioTests()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            $"Ven4Tools.UI.Integrity-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Папка клиента ровно там, где её ищет лаунчер в тестовом режиме: настроек ещё
    /// нет, поэтому InstallPath = &lt;корень теста&gt;\Install (см. MainWindow.xaml.cs).
    /// </summary>
    private string ClientPath => Path.Combine(_testRoot, "Install", "Ven4Tools_Client");

    /// <summary>
    /// Паттерн В: исполняемый файл клиента физически на месте, но повреждён — версия
    /// из него не читается. Пользователь обязан увидеть локальный диагноз, а не
    /// «сервер недоступен, попробуйте позже»: сервер здесь ни при чём, и совет ждать
    /// увёл бы человека от единственного работающего действия (переустановить клиент).
    ///
    /// До фикса 02.09.2026 UI-слой подставлял вместо нечитаемой версии фиктивное
    /// «0.0.0», проверка шла в сеть за манифестом несуществующей версии и возвращала
    /// ManifestUnavailable — то самое ложное «сервер недоступен». Этот тест держит
    /// именно тот текст, который увидит пользователь.
    /// </summary>
    [Fact]
    public void CorruptedClientExecutable_ReportsLocalDamage_NotServerProblem()
    {
        // Число целых файлов вокруг exe на вердикт не влияет: проверка коротко
        // замыкается сразу после чтения версии, до хеширования папки и до сети.
        // Держим публикацию маленькой, чтобы тест не тратил время впустую.
        CreateSyntheticPublication(fileCount: 12);
        WriteCorruptedClientExecutable();

        Window window = StartLauncher();
        Window settings = OpenSettings(window);

        string status = RunIntegrityCheck(settings);

        Assert.False(_application!.HasExited, "Проверка повреждённого клиента завершила лаунчер.");
        Assert.Contains("Клиент повреждён", status, StringComparison.Ordinal);
        // Главная защита теста: локальная поломка не должна маскироваться под сетевую.
        Assert.DoesNotContain("сервер недоступен", status, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("версия не читается", DetailText(settings), StringComparison.Ordinal);
        // Чинить пофайлово тут нечего — кнопка «Исправить» не предлагается вовсе.
        Assert.False(
            RepairButtonIsOffered(settings),
            "Кнопка «Исправить» предложена для повреждённого исполняемого файла клиента.");

        settings.FindFirstDescendant(condition => condition.ByAutomationId("btnCloseSettings"))!
            .AsButton()
            .Invoke();
    }

    // --- Паттерны А и Б: подтверждены живым прогоном 02.09.2026, в наборе — заглушки ---
    //
    // Оба сценария требуют, чтобы лаунчер получил ПОДПИСАННЫЙ файловый манифест
    // синтетической публикации, которую создал тест. Чистого способа это сделать нет,
    // и обходить защиту ради теста нельзя — вот что мешает:
    //
    // 1. Подпись. ClientManifestVerifier несёт зашитый публичный ключ, приватная
    //    половина — вне репозитория (Tools/deploy-client-manifest.ps1). Подписать
    //    манифест тестовой публикации нечем, а подсунуть тесту приватный ключ автора
    //    значило бы связать набор тестов с ключом продакшена.
    // 2. Хост. ClientManifestFetcher отказывает ещё раньше подписи: манифест и файлы
    //    качаются только с https и только с доверенных хостов (DownloadValidator).
    //    Локальный HttpListener отбраковывается по обоим признакам сразу.
    //
    // Обойти это можно было бы только тестовым «окном» в самом лаунчере — переменной
    // окружения, подменяющей ключ или адрес манифеста. Такое окно уехало бы к
    // пользователям в том же самом exe и дало бы любому, кто может выставить
    // переменную окружения, право положить произвольные файлы внутрь папки клиента.
    // Существующий VEN4TOOLS_UI_TEST такой ценой не обходится: он только ОТКЛЮЧАЕТ
    // действия, а не включает опасные.
    //
    // Что покрыто вместо этого: решение «починимо / переустанавливать целиком»
    // проверяется на уровне юнитов на настоящем ClientDeltaPlanner
    // (ClientIntegrityCheckerTests.Check_DamagedFile_ReportsRepairable,
    // Check_TooManyDifferences_RecommendsFullReinstall,
    // Repair_AppliesPlanWhenFindingsAreRepairable, Repair_FullReinstallCase_DoesNothingItself).
    // Непокрытым остаётся именно живой стык: UI-текст и видимость кнопки «Исправить».
    //
    // Чтобы снять Skip, нужен способ отдать лаунчеру подписанный манифест тестовой
    // публикации, не ослабляя ни проверку подписи, ни список доверенных хостов в
    // поставляемом пользователям exe.

    /// <summary>
    /// Паттерн А: испорчено 45% файлов публикации (порог
    /// ClientDeltaPlanner.MinimumUnchangedShare пройден). Проверка обязана показать
    /// «Найдены расхождения с опубликованной версией» и предложить «Исправить», а
    /// починка — докачать недостающее и закончиться статусом «✅ Исправлено —
    /// целостность клиента подтверждена». Живьём пройдено 02.09.2026 на установленном
    /// клиенте 5.0.0 (210 из 467 файлов испорчено, после починки все 467 совпали по SHA256).
    /// </summary>
    [Fact(Skip = "Нужен подписанный манифест синтетической публикации; чистого пути нет — см. комментарий выше.")]
    public void RepairableDamage_IsFixedInPlace()
    {
        Assert.Fail("Заглушка: фикстура подписанного манифеста тестовой публикации недоступна.");
    }

    /// <summary>
    /// Паттерн Б: испорчено 95% файлов, исполняемый файл цел. Пофайловая починка
    /// невыгодна, и лаунчер обязан НЕ предлагать её вовсе: статус «Слишком много
    /// расхождений с опубликованной версией — переустановите клиент полностью через
    /// обычное обновление», деталь с долей совпавших файлов, кнопка «Исправить»
    /// скрыта (Visibility.Collapsed, а не просто недоступна). Живьём пройдено
    /// 02.09.2026: 443 из 467 файлов испорчено, «совпало лишь 24 из 467 файлов (5%)».
    /// </summary>
    [Fact(Skip = "Нужен подписанный манифест синтетической публикации; чистого пути нет — см. комментарий выше.")]
    public void UnrepairableDamage_OffersFullReinstallOnly()
    {
        Assert.Fail("Заглушка: фикстура подписанного манифеста тестовой публикации недоступна.");
    }

    public void Dispose()
    {
        if (_application is not null)
        {
            _application.Close();
            if (!_application.HasExited)
            {
                _application.Kill();
            }
            _application.Dispose();
        }

        _automation?.Dispose();

        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch
        {
            // Остаток тестовой песочницы в %TEMP% не должен маскировать результат теста.
        }
    }

    /// <summary>
    /// Синтетическая публикация клиента: файлы с предсказуемым содержимым, включая
    /// вложенную папку — состав важен только тем, что он есть на диске.
    /// </summary>
    private void CreateSyntheticPublication(int fileCount)
    {
        Directory.CreateDirectory(ClientPath);
        Directory.CreateDirectory(Path.Combine(ClientPath, "ru"));

        for (int index = 0; index < fileCount; index++)
        {
            string relative = index % 4 == 0
                ? Path.Combine("ru", $"resource{index}.dll")
                : $"library{index}.dll";
            File.WriteAllText(
                Path.Combine(ClientPath, relative),
                $"синтетический файл публикации №{index}");
        }
    }

    /// <summary>
    /// Так выглядит exe, у которого антивирус выкусил кусок или оборвалась запись:
    /// файл на месте и не пустой, но это уже не PE — сведений о версии в нём нет.
    /// </summary>
    private void WriteCorruptedClientExecutable()
    {
        var garbage = new byte[1600];
        new Random(20260902).NextBytes(garbage);
        File.WriteAllBytes(Path.Combine(ClientPath, "Ven4Tools.exe"), garbage);
    }

    private Window StartLauncher()
    {
        var startInfo = new ProcessStartInfo(LauncherTestEnvironment.FindLauncher());
        startInfo.Environment["VEN4TOOLS_UI_TEST"] = "1";
        startInfo.Environment["VEN4TOOLS_UI_TEST_ROOT"] = _testRoot;
        _application = Application.Launch(startInfo);
        _automation = new UIA3Automation();

        Window window = Retry.WhileNull(
            () => _application.GetMainWindow(_automation),
            timeout: TimeSpan.FromSeconds(20),
            interval: TimeSpan.FromMilliseconds(250)).Result
            ?? throw new InvalidOperationException("Главное окно лаунчера не появилось.");

        Retry.WhileFalse(
            () => window.FindFirstDescendant(
                condition => condition.ByAutomationId("btnOpenSettings"))?.IsEnabled == true,
            timeout: TimeSpan.FromSeconds(20),
            interval: TimeSpan.FromMilliseconds(250));

        return window;
    }

    private static Window OpenSettings(Window window)
    {
        window.FindFirstDescendant(condition => condition.ByAutomationId("btnOpenSettings"))!
            .AsButton()
            .Invoke();

        // Окно настроек создаётся с Owner = главное окно, поэтому UIA размещает его
        // как потомка главного окна, а не как самостоятельное верхнеуровневое окно.
        return Retry.WhileNull(
            () => window.FindAllDescendants(condition => condition.ByControlType(ControlType.Window))
                .Select(element => element.AsWindow())
                .FirstOrDefault(candidate =>
                    candidate.Title.Contains("Настройки", StringComparison.OrdinalIgnoreCase)),
            timeout: TimeSpan.FromSeconds(10),
            interval: TimeSpan.FromMilliseconds(250)).Result
            ?? throw new InvalidOperationException("Окно «Настройки» не открылось.");
    }

    /// <summary>
    /// Нажимает «Проверить и восстановить клиент» и возвращает итоговый текст статуса.
    /// Ждём именно смены текста, а не появления метки: сразу после клика в ней стоит
    /// «Проверка...», и тест, читающий её без ожидания, проверял бы только то, что
    /// обработчик вообще запустился.
    /// </summary>
    private static string RunIntegrityCheck(Window settings)
    {
        settings.FindFirstDescendant(condition => condition.ByAutomationId("btnCheckIntegrity"))!
            .AsButton()
            .Invoke();

        DateTime deadline = DateTime.UtcNow + VerdictTimeout;
        string status = "";
        while (DateTime.UtcNow < deadline)
        {
            status = StatusText(settings);
            if (status.Length > 0 && !status.StartsWith(InProgressStatus, StringComparison.Ordinal))
            {
                return status;
            }
            Thread.Sleep(500);
        }

        throw new InvalidOperationException(
            $"Проверка целостности не завершилась за {VerdictTimeout.TotalSeconds:F0} с " +
            $"(последний статус: «{status}»).");
    }

    private static string StatusText(Window settings) =>
        settings.FindFirstDescendant(condition => condition.ByAutomationId("txtIntegrityStatus"))?
            .AsLabel().Text ?? "";

    private static string DetailText(Window settings) =>
        settings.FindFirstDescendant(condition => condition.ByAutomationId("txtIntegrityDetail"))?
            .AsLabel().Text ?? "";

    /// <summary>
    /// Предложена ли пользователю починка. Скрытый элемент WPF отдаёт в UI Automation
    /// либо отсутствием в дереве, либо признаком offscreen — проверяем оба признака,
    /// иначе тест зависел бы от деталей построения дерева, а не от того, что видит
    /// пользователь.
    /// </summary>
    private static bool RepairButtonIsOffered(Window settings)
    {
        AutomationElement? repair = settings.FindFirstDescendant(
            condition => condition.ByAutomationId("btnRepairIntegrity"));
        return repair is not null && !repair.IsOffscreen;
    }
}

/// <summary>
/// Общее для UI-тестов лаунчера: где взять сам лаунчер и корень репозитория.
/// Вынесено сюда, чтобы второй набор тестов не заводил свою копию поиска exe —
/// разъехавшись, копии искали бы разные сборки и тесты проверяли бы разные бинарники.
/// </summary>
internal static class LauncherTestEnvironment
{
    /// <summary>
    /// Лаунчер под тестом. LAUNCHER_UNDER_TEST задаётся релизным workflow и указывает
    /// на РЕАЛЬНО установленный лаунчер — без него берётся сборка из Release.
    /// </summary>
    public static string FindLauncher()
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

    public static string FindRepositoryRoot()
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
