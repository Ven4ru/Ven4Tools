using System.Text.Json;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Набор «Перед переустановкой»: что складывается на флешку и как это возвращается.
/// pnputil и netsh подменены — они «выгружают» файлы-пустышки, настоящая система не трогается.
/// </summary>
public sealed class ReinstallKitTests : IDisposable
{
    private sealed class FakeRunner : IKitCommandRunner
    {
        public List<(string File, string[] Args)> Calls { get; } = new();
        public int DriverPackages { get; set; } = 2;
        public int WifiProfiles { get; set; } = 1;
        public int PnpUtilExitCode { get; set; }
        public Func<string[], int>? WifiAddExitCode { get; set; }

        public Task<(int ExitCode, string Output)> RunAsync(
            string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            var args = arguments.ToArray();
            Calls.Add((Path.GetFileName(fileName), args));

            if (args[0] == "/export-driver")
            {
                for (int i = 0; i < DriverPackages; i++)
                {
                    string package = Path.Combine(args[2], $"oem{i}.inf_amd64");
                    Directory.CreateDirectory(package);
                    File.WriteAllText(Path.Combine(package, $"oem{i}.inf"), "[Version]");
                    File.WriteAllText(Path.Combine(package, $"oem{i}.sys"), "binary");
                }
                return Task.FromResult((PnpUtilExitCode, "Driver package exported."));
            }
            if (args[0] == "wlan" && args[1] == "export")
            {
                string folder = args.Single(a => a.StartsWith("folder=")).Substring("folder=".Length);
                for (int i = 0; i < WifiProfiles; i++)
                    File.WriteAllText(Path.Combine(folder, $"Wi-Fi-net{i}.xml"), "<WLANProfile/>");
                return Task.FromResult((WifiProfiles > 0 ? 0 : 1, ""));
            }
            if (args[0] == "/add-driver") return Task.FromResult((PnpUtilExitCode, ""));
            if (args[0] == "wlan" && args[1] == "add")
                return Task.FromResult((WifiAddExitCode?.Invoke(args) ?? 0, ""));

            return Task.FromResult((1, "неизвестная команда"));
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-kit-" + Guid.NewGuid().ToString("N"));
    private readonly string _kit;
    private readonly FakeRunner _runner = new();

    private static readonly (string Id, string Name, string WingetId)[] CatalogInstalled =
    {
        ("7zip", "7-Zip", "7zip.7zip"),
        ("firefox", "Firefox", "Mozilla.Firefox")
    };

    private static readonly KitInstalledProgram[] Installed =
    {
        new("7-Zip", "7zip.7zip", "winget"),
        new("Firefox", "Mozilla.Firefox", "winget"),
        new("Obsidian", "Obsidian.Obsidian", "winget"),
        new("Драйвер сканера", "ARP\\Machine\\X64\\ScannerDriver", ""),
        new("1С:Предприятие", "{A1B2C3D4-0000-0000-0000-000000000000}", ""),
        new("Калькулятор", "MSIX\\Microsoft.WindowsCalculator_11.2405_x64__8wekyb3d8bbwe", ""),
        new("Игра из магазина", "9NBLGGH4R32N", "msstore")
    };

    public ReinstallKitTests() => _kit = Path.Combine(_dir, "kit with space");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Task<ReinstallKitBuilder.Result> BuildAsync(
        bool drivers = true, bool wifi = true, bool apps = true, Func<string, Task<bool>>? exportWinget = null) =>
        ReinstallKitBuilder.BuildAsync(
            new ReinstallKitBuilder.Options(_kit, drivers, wifi, apps, CopyClient: false),
            CatalogInstalled, Installed, clientDirectory: _dir, _runner, exportWinget: exportWinget);

    [Fact]
    public async Task Полный_набор_содержит_драйверы_WiFi_файл_ответа_и_файл_запуска()
    {
        var result = await BuildAsync();

        Assert.Equal(2, result.Drivers);
        Assert.True(result.DriversBytes > 0);
        Assert.Equal(1, result.WifiProfiles);
        Assert.Equal(2, result.CatalogApps);
        Assert.Empty(result.Warnings);

        Assert.True(File.Exists(Path.Combine(_kit, ReinstallKitBuilder.LauncherFileName)));
        Assert.True(File.Exists(Path.Combine(_kit, ReinstallKitBuilder.ReadmeFileName)));
        Assert.True(File.Exists(Path.Combine(_kit, ReinstallKitBuilder.ManualListFileName)));

        using var answer = JsonDocument.Parse(File.ReadAllText(Path.Combine(_kit, ReinstallKitBuilder.AnswerFileName)));
        Assert.Equal(new[] { "7zip", "firefox" },
            answer.RootElement.GetProperty("apps").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Drivers", answer.RootElement.GetProperty("drivers").GetString());
        Assert.Equal("WiFi", answer.RootElement.GetProperty("wifi").GetString());
        Assert.False(answer.RootElement.GetProperty("silent").GetBoolean());
    }

    [Fact]
    public async Task Утилиты_вызываются_из_System32_с_ожидаемыми_аргументами()
    {
        await BuildAsync();

        var export = _runner.Calls.Single(c => c.Args[0] == "/export-driver");
        Assert.Equal("pnputil.exe", export.File, ignoreCase: true);
        Assert.Equal(new[] { "/export-driver", "*", Path.Combine(_kit, "Drivers") }, export.Args);

        var wifi = _runner.Calls.Single(c => c.Args[0] == "wlan");
        Assert.Equal("netsh.exe", wifi.File, ignoreCase: true);
        Assert.Contains("key=clear", wifi.Args);
        Assert.Contains("folder=" + Path.Combine(_kit, "WiFi"), wifi.Args);

        Assert.StartsWith(Environment.SystemDirectory, KitCommands.PnpUtil, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Компьютер_без_WiFi_это_не_ошибка_а_в_файле_ответа_нет_ссылки_на_профили()
    {
        _runner.WifiProfiles = 0;
        var result = await BuildAsync();

        Assert.Equal(0, result.WifiProfiles);
        Assert.Empty(result.Warnings);
        Assert.False(Directory.Exists(Path.Combine(_kit, "WiFi")));

        using var answer = JsonDocument.Parse(File.ReadAllText(Path.Combine(_kit, ReinstallKitBuilder.AnswerFileName)));
        Assert.False(answer.RootElement.TryGetProperty("wifi", out _));
        Assert.DoesNotContain("открытым текстом", File.ReadAllText(Path.Combine(_kit, ReinstallKitBuilder.ReadmeFileName)));
    }

    [Fact]
    public async Task Невыгруженные_драйверы_попадают_в_предупреждения()
    {
        _runner.DriverPackages = 0;
        _runner.PnpUtilExitCode = 5;
        var result = await BuildAsync();

        Assert.Equal(0, result.Drivers);
        Assert.Contains(result.Warnings, w => w.Contains("Драйверы не выгружены") && w.Contains("5"));
        using var answer = JsonDocument.Parse(File.ReadAllText(Path.Combine(_kit, ReinstallKitBuilder.AnswerFileName)));
        Assert.False(answer.RootElement.TryGetProperty("drivers", out _));
    }

    [Fact]
    public async Task Невыбранные_части_не_запрашиваются_и_не_создаются()
    {
        var result = await BuildAsync(drivers: false, wifi: false);

        Assert.Null(result.Drivers);
        Assert.Null(result.WifiProfiles);
        Assert.Empty(_runner.Calls);
        Assert.False(Directory.Exists(Path.Combine(_kit, "Drivers")));
    }

    [Fact]
    public async Task Пустой_выбор_отклоняется()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(drivers: false, wifi: false, apps: false));
    }

    [Fact]
    public void Вне_каталога_winget_пакеты_отделяются_от_ручных_а_системные_и_магазинные_не_попадают()
    {
        var (wingetOnly, manual) = ReinstallKitBuilder.SplitOutsideCatalog(CatalogInstalled, Installed);

        Assert.Equal(new[] { "Obsidian" }, wingetOnly.Select(p => p.Name));
        Assert.Equal(new[] { "1С:Предприятие", "Драйвер сканера" }, manual.Select(p => p.Name));
    }

    [Fact]
    public async Task Список_вручную_называет_программы_и_упоминает_экспорт_winget_только_если_он_удался()
    {
        string exportPath = "";
        var result = await BuildAsync(exportWinget: path =>
        {
            exportPath = path;
            File.WriteAllText(path, "{}");
            return Task.FromResult(true);
        });

        Assert.Equal(1, result.WingetOnly);
        Assert.Equal(2, result.Manual);
        Assert.Equal(Path.Combine(_kit, ReinstallKitBuilder.WingetExportFileName), exportPath);

        string list = File.ReadAllText(Path.Combine(_kit, ReinstallKitBuilder.ManualListFileName));
        Assert.Contains("winget install --id Obsidian.Obsidian", list);
        Assert.Contains("1С:Предприятие", list);
        Assert.Contains(ReinstallKitBuilder.WingetExportFileName, list);
        Assert.DoesNotContain("Калькулятор", list);

        string withoutExport = ReinstallKitBuilder.BuildManualList(new[] { "7-Zip" },
            Array.Empty<KitInstalledProgram>(), Array.Empty<KitInstalledProgram>());
        Assert.DoesNotContain(ReinstallKitBuilder.WingetExportFileName, withoutExport);
    }

    [Fact]
    public void Файл_запуска_только_ASCII_и_ссылается_на_клиент_и_файл_ответа_рядом()
    {
        Assert.All(ReinstallKitBuilder.LauncherScript, c => Assert.True(c < 128));
        Assert.Contains("%~dp0Ven4Tools\\Ven4Tools.exe", ReinstallKitBuilder.LauncherScript);
        Assert.Contains("--answer-file \"%~dp0" + ReinstallKitBuilder.AnswerFileName + "\"", ReinstallKitBuilder.LauncherScript);
    }

    // ── Возврат на свежей системе ────────────────────────────────────────────

    [Fact]
    public async Task Возврат_ставит_драйверы_раньше_WiFi_и_добавляет_каждый_профиль()
    {
        _runner.WifiProfiles = 2;
        await BuildAsync();
        _runner.Calls.Clear();

        var outcome = await ReinstallKitRestorer.RestoreAsync(
            Path.Combine(_kit, "Drivers"), Path.Combine(_kit, "WiFi"), _runner);

        Assert.Equal(2, outcome.DriverPackages);
        Assert.Equal(2, outcome.WifiAdded);
        Assert.Equal(0, outcome.WifiFailed);
        Assert.False(outcome.DriversRebootNeeded);

        Assert.Equal("/add-driver", _runner.Calls[0].Args[0]);
        Assert.Equal(Path.Combine(_kit, "Drivers", "*.inf"), _runner.Calls[0].Args[1]);
        Assert.Contains("/subdirs", _runner.Calls[0].Args);
        Assert.Contains("/install", _runner.Calls[0].Args);
        Assert.Equal(2, _runner.Calls.Count(c => c.Args[0] == "wlan" && c.Args[1] == "add"));
        Assert.All(_runner.Calls.Skip(1), c => Assert.Contains("user=all", c.Args));
    }

    [Fact]
    public async Task Код_3010_от_pnputil_это_успех_с_перезагрузкой()
    {
        await BuildAsync(wifi: false);
        _runner.PnpUtilExitCode = 3010;

        var outcome = await ReinstallKitRestorer.RestoreAsync(Path.Combine(_kit, "Drivers"), null, _runner);

        Assert.True(outcome.DriversRebootNeeded);
        Assert.Contains(outcome.Log, line => line.Contains("нужна перезагрузка"));
    }

    [Fact]
    public async Task Не_добавленный_профиль_WiFi_считается_отдельно_и_не_прерывает_остальные()
    {
        _runner.WifiProfiles = 3;
        await BuildAsync(drivers: false);
        _runner.WifiAddExitCode = args => args.Any(a => a.EndsWith("net1.xml")) ? 1 : 0;

        var outcome = await ReinstallKitRestorer.RestoreAsync(null, Path.Combine(_kit, "WiFi"), _runner);

        Assert.Null(outcome.DriverPackages);
        Assert.Equal(2, outcome.WifiAdded);
        Assert.Equal(1, outcome.WifiFailed);
    }

    [Fact]
    public async Task Пустые_или_отсутствующие_папки_ничего_не_запускают()
    {
        var outcome = await ReinstallKitRestorer.RestoreAsync(
            Path.Combine(_dir, "нет такой"), Path.Combine(_dir, "и такой"), _runner);

        Assert.Empty(_runner.Calls);
        Assert.Empty(outcome.Log);
        Assert.Equal((0, 0), ReinstallKitRestorer.Describe(null, null));
    }

    // ── Файл ответа с драйверами и Wi-Fi ────────────────────────────────────

    private static UnattendedCommandLine.ParseStatus Parse(
        string json, out UnattendedRequest? request, out string error, string answerFile = @"E:\kit\ven4tools-restore.json") =>
        UnattendedCommandLine.Parse(new[] { "--answer-file", answerFile }, _ => json, out request, out error);

    [Fact]
    public void Файл_ответа_набора_разбирается_в_пути_рядом_с_файлом()
    {
        string json = ReinstallKitBuilder.BuildAnswerFile(new[] { "7zip" }, drivers: true, wifi: true);

        Assert.Equal(UnattendedCommandLine.ParseStatus.Ok, Parse(json, out var request, out _));
        Assert.Equal(@"E:\kit\Drivers", request!.RestoreDriversPath);
        Assert.Equal(@"E:\kit\WiFi", request.RestoreWifiPath);
        Assert.True(request.HasRestore);
        Assert.False(request.Silent);
        Assert.Equal(new[] { "7zip" }, request.AppIds);
    }

    [Fact]
    public void Набор_без_программ_но_с_драйверами_допустим()
    {
        string json = ReinstallKitBuilder.BuildAnswerFile(Array.Empty<string>(), drivers: true, wifi: false);

        Assert.Equal(UnattendedCommandLine.ParseStatus.Ok, Parse(json, out var request, out _));
        Assert.Empty(request!.AppIds);
        Assert.Null(request.RestoreWifiPath);
    }

    [Theory]
    [InlineData(@"C:\\Windows\\System32\\DriverStore")]
    [InlineData(@"..\\чужая папка")]
    [InlineData(@"Drivers\\..\\..\\outside")]
    public void Папка_драйверов_вне_папки_файла_ответа_отклоняется(string path)
    {
        string json = "{\"apps\":[\"7zip\"],\"drivers\":\"" + path + "\"}";

        Assert.Equal(UnattendedCommandLine.ParseStatus.Error, Parse(json, out _, out string error));
        Assert.Contains("относительно самого файла", error);
    }

    [Fact]
    public void Обычный_файл_ответа_без_драйверов_работает_как_раньше()
    {
        Assert.Equal(UnattendedCommandLine.ParseStatus.Ok, Parse("{\"apps\":[\"7zip\"]}", out var request, out _));
        Assert.False(request!.HasRestore);

        Assert.Equal(UnattendedCommandLine.ParseStatus.Error, Parse("{\"apps\":[]}", out _, out string error));
        Assert.Contains("нет ни одного приложения", error);
    }
}
