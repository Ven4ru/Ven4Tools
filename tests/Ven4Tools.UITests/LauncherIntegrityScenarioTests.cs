using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
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

    // --- Паттерны А и Б: живая публикация клиента с CDN ---
    //
    // Синтетическую публикацию для этих сценариев подписать нечем: приватный ключ
    // манифеста вне репозитория, а локальный сервер лаунчер не примет (только https и
    // доверенные хосты). Ослаблять ни подпись, ни список хостов ради теста нельзя.
    //
    // Поэтому эталон здесь настоящий: текущая опубликованная сборка клиента. Тест
    // скачивает её архив с CDN (сверяя SHA256 из version.json), раскладывает в
    // песочницу так же, как это делает установка, портит часть файлов и нажимает
    // «Проверить». Лаунчер сам берёт подписанный файловый манифест этой версии с CDN —
    // ровно тем путём, каким это происходит у пользователя. Нужна сеть.

    /// <summary>
    /// Паттерн А: испорчено около 40% файлов публикации (порог
    /// ClientDeltaPlanner.MinimumUnchangedShare = 0,5 пройден: целыми остаются ~60%). Проверка обязана показать
    /// «Найдены расхождения с опубликованной версией» и предложить «Исправить», а
    /// починка — докачать недостающее и закончиться статусом «✅ Исправлено —
    /// целостность клиента подтверждена».
    /// </summary>
    [Fact]
    public void RepairableDamage_IsFixedInPlace()
    {
        var damaged = InstallPublishedClient(damageShare: 0.40);

        Window window = StartLauncher();
        Window settings = OpenSettings(window);

        string status = RunIntegrityCheck(settings);
        Assert.Equal("Найдены расхождения с опубликованной версией", status);
        Assert.True(RepairButtonIsOffered(settings), "Кнопка «Исправить» не предложена для починимых расхождений.");

        settings.FindFirstDescendant(condition => condition.ByAutomationId("btnRepairIntegrity"))!
            .AsButton()
            .Invoke();
        string final = WaitForFinalStatus(settings, RepairTimeout, "Восстановление", InProgressStatus);

        Assert.False(_application!.HasExited, "Починка завершила лаунчер.");
        Assert.Equal("✅ Исправлено — целостность клиента подтверждена", final);
        foreach (var (relative, original) in damaged)
        {
            string path = Path.Combine(ClientPath, relative);
            Assert.True(File.Exists(path), $"После починки нет файла {relative}.");
            Assert.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(original), $"После починки файл {relative} не совпал с опубликованным.");
        }

        settings.FindFirstDescendant(condition => condition.ByAutomationId("btnCloseSettings"))!
            .AsButton()
            .Invoke();
    }

    /// <summary>
    /// Паттерн Б: испорчено 95% файлов, исполняемый файл цел. Пофайловая починка
    /// невыгодна, и лаунчер обязан НЕ предлагать её вовсе: статус «Слишком много
    /// расхождений с опубликованной версией — переустановите клиент полностью через
    /// обычное обновление», деталь с долей совпавших файлов, кнопка «Исправить»
    /// скрыта (Visibility.Collapsed, а не просто недоступна).
    /// </summary>
    [Fact]
    public void UnrepairableDamage_OffersFullReinstallOnly()
    {
        InstallPublishedClient(damageShare: 0.95);

        Window window = StartLauncher();
        Window settings = OpenSettings(window);

        string status = RunIntegrityCheck(settings);

        Assert.False(_application!.HasExited, "Проверка сильно повреждённого клиента завершила лаунчер.");
        Assert.Equal(
            "Слишком много расхождений с опубликованной версией — переустановите клиент полностью через обычное обновление",
            status);
        Assert.Contains("совпало", DetailText(settings), StringComparison.OrdinalIgnoreCase);
        Assert.False(
            RepairButtonIsOffered(settings),
            "Кнопка «Исправить» предложена, хотя пофайловая починка невыгодна.");

        settings.FindFirstDescendant(condition => condition.ByAutomationId("btnCloseSettings"))!
            .AsButton()
            .Invoke();
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
        Directory.CreateDirectory(_testRoot);
        var startInfo = new ProcessStartInfo(LauncherTestEnvironment.FindLauncher());
        startInfo.Environment["VEN4TOOLS_UI_TEST"] = "1";
        // Проверки ищут элементы по русским подписям; на нерусской Windows лаунчер выбрал бы английский.
        startInfo.Environment["VEN4TOOLS_LANG"] = Environment.GetEnvironmentVariable("VEN4TOOLS_LANG") ?? "ru";
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

    // Починка докачивает испорченные файлы с CDN и перепроверяет всю папку.
    private static readonly TimeSpan RepairTimeout = TimeSpan.FromMinutes(10);

    private const string VersionManifestUrl = "https://cdn.ven4tools.ru/version.json";

    private static readonly object PublishedArchiveLock = new();
    private static string? _publishedArchive;

    /// <summary>
    /// Раскладывает текущую опубликованную сборку клиента в папку клиента песочницы и
    /// портит указанную долю файлов (исполняемый файл не трогается): каждый четвёртый
    /// из выбранных удаляется, остальные переписываются. Возвращает исходное
    /// содержимое испорченных файлов — после починки оно обязано вернуться.
    /// </summary>
    private List<(string Relative, byte[] Original)> InstallPublishedClient(double damageShare)
    {
        string archive = PublishedClientArchive();
        Directory.CreateDirectory(ClientPath);
        using (ZipArchive zip = ZipFile.OpenRead(archive))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                // Запись подписи установка не распаковывает (SafeZipExtractor), и
                // проверка её не считает (ClientManifestBuilder) — песочница как у пользователя.
                if (entry.Name.Length == 0 || entry.FullName == "_ven4tools_signature.json")
                {
                    continue;
                }
                string target = Path.GetFullPath(Path.Combine(ClientPath, entry.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        string[] files = Directory.GetFiles(ClientPath, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(ClientPath, path))
            .Where(relative => !string.Equals(relative, "Ven4Tools.exe", StringComparison.OrdinalIgnoreCase))
            .OrderBy(relative => relative, StringComparer.Ordinal)
            .ToArray();
        int toDamage = (int)Math.Ceiling(files.Length * damageShare);
        var damaged = new List<(string, byte[])>();
        // Равномерно по списку, а не первые N: порча не должна собраться в одной папке.
        for (int index = 0; index < toDamage; index++)
        {
            string relative = files[(int)((long)index * files.Length / toDamage)];
            string path = Path.Combine(ClientPath, relative);
            byte[] original = File.ReadAllBytes(path);
            damaged.Add((relative, original));
            if (index % 4 == 0)
            {
                File.Delete(path);
            }
            else
            {
                File.WriteAllText(path, $"испорчено тестом целостности, файл №{index}");
            }
        }
        return damaged;
    }

    /// <summary>
    /// Архив текущей опубликованной версии клиента, один на весь прогон: скачивается
    /// с CDN и сверяется с zip_sha256 из version.json. Без сети тест падает с понятной
    /// причиной — проверять живой стык с эталоном не на чем.
    /// </summary>
    private static string PublishedClientArchive()
    {
        lock (PublishedArchiveLock)
        {
            if (_publishedArchive is not null && File.Exists(_publishedArchive))
            {
                return _publishedArchive;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using JsonDocument manifest = JsonDocument.Parse(http.GetStringAsync(VersionManifestUrl).GetAwaiter().GetResult());
            JsonElement client = manifest.RootElement.GetProperty("client");
            string version = client.GetProperty("version").GetString()!;
            string zipUrl = client.GetProperty("zip_url").GetString()!;
            string expectedSha = client.GetProperty("zip_sha256").GetString()!;

            string cacheDir = Path.Combine(Path.GetTempPath(), "Ven4Tools.UI.PublishedClient");
            Directory.CreateDirectory(cacheDir);
            string path = Path.Combine(cacheDir, $"Ven4Tools-Client-{version}.zip");
            if (!File.Exists(path) || !Sha256Matches(path, expectedSha))
            {
                string partial = path + ".part";
                using (Stream source = http.GetStreamAsync(zipUrl).GetAwaiter().GetResult())
                using (FileStream target = File.Create(partial))
                {
                    source.CopyTo(target);
                }
                File.Move(partial, path, overwrite: true);
            }
            Assert.True(Sha256Matches(path, expectedSha), $"Скачанный архив клиента {version} не совпал по SHA256 с version.json.");
            _publishedArchive = path;
            return path;
        }
    }

    private static bool Sha256Matches(string path, string expected)
    {
        using FileStream stream = File.OpenRead(path);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ждёт итогового статуса после действия, проходящего через промежуточные
    /// («Восстановление...», затем повторная «Проверка...»).
    /// </summary>
    private static string WaitForFinalStatus(Window settings, TimeSpan timeout, params string[] inProgressPrefixes)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        string status = "";
        // Обработчик меняет текст не мгновенно — без паузы прочитали бы прежний вердикт.
        Thread.Sleep(1000);
        while (DateTime.UtcNow < deadline)
        {
            status = StatusText(settings);
            if (status.Length > 0 && !inProgressPrefixes.Any(prefix => status.StartsWith(prefix, StringComparison.Ordinal)))
            {
                return status;
            }
            Thread.Sleep(500);
        }

        throw new InvalidOperationException(
            $"Действие не завершилось за {timeout.TotalMinutes:F0} мин (последний статус: «{status}»).");
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
            "net10.0-windows",
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
