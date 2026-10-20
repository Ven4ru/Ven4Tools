using Ven4Tools.Services;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Автозагрузка во вкладке «Очистка»: разбор отметок StartupApproved, выделение
/// исполняемого файла из команды, признаки «системное» и поведение списка.
/// Реестр, планировщик и файлы не трогаются.
/// </summary>
public class StartupItemsServiceTests
{
    // ── StartupApproved ──────────────────────────────────────────────────────

    [Fact]
    public void Нет_отметки_значит_включено()
    {
        Assert.True(StartupItemsService.IsApprovedEnabled(null));
        Assert.True(StartupItemsService.IsApprovedEnabled(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData(0x02, true)]
    [InlineData(0x06, true)]
    [InlineData(0x03, false)]
    [InlineData(0x07, false)]
    public void Первый_байт_отметки_решает_включено_ли(byte first, bool expected)
    {
        var data = new byte[12];
        data[0] = first;
        Assert.Equal(expected, StartupItemsService.IsApprovedEnabled(data));
    }

    [Fact]
    public void Выключение_пишется_как_у_Диспетчера_задач()
    {
        var when = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        var data = StartupItemsService.BuildApprovedValue(enabled: false, when);

        Assert.Equal(12, data.Length);
        Assert.Equal(0x03, data[0]);
        Assert.Equal(when.ToFileTimeUtc(), BitConverter.ToInt64(data, 4));
        Assert.False(StartupItemsService.IsApprovedEnabled(data));
    }

    [Fact]
    public void Включение_пишется_нулями_после_02()
    {
        var data = StartupItemsService.BuildApprovedValue(enabled: true, DateTime.UtcNow);
        Assert.Equal(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, data);
        Assert.True(StartupItemsService.IsApprovedEnabled(data));
    }

    [Theory]
    [InlineData(8, true)]   // при запуске системы
    [InlineData(9, true)]   // при входе
    [InlineData(1, false)]  // по времени
    [InlineData(2, false)]  // ежедневно
    [InlineData(0, false)]  // по событию
    public void Задача_с_автозагрузкой_только_при_входе_или_запуске(int type, bool expected)
    {
        Assert.Equal(expected, StartupItemsService.IsStartupTrigger(type));
    }

    // ── Исполняемый файл из команды ──────────────────────────────────────────

    private static readonly string[] Dirs = { @"C:\Windows\System32", @"C:\Windows" };

    private static string? Extract(string command, params string[] existing) =>
        StartupItemsService.ExtractExecutable(command,
            path => existing.Contains(path, StringComparer.OrdinalIgnoreCase), Dirs);

    [Fact]
    public void Путь_в_кавычках_берётся_целиком()
    {
        Assert.Equal(@"C:\Program Files\App\app.exe",
            Extract(@"""C:\Program Files\App\app.exe"" --minimized"));
    }

    [Fact]
    public void Путь_с_пробелами_без_кавычек_находится_по_существующему_файлу()
    {
        Assert.Equal(@"C:\Program Files\App Dir\app.exe",
            Extract(@"C:\Program Files\App Dir\app.exe -silent", @"C:\Program Files\App Dir\app.exe"));
    }

    [Fact]
    public void Расширение_exe_подставляется_если_его_нет_в_команде()
    {
        Assert.Equal(@"C:\Tools\tool.exe", Extract(@"C:\Tools\tool /start", @"C:\Tools\tool.exe"));
    }

    [Fact]
    public void Голое_имя_ищется_в_системных_папках()
    {
        Assert.Equal(@"C:\Windows\System32\rundll32.exe",
            Extract("rundll32.exe shell32.dll,Control_RunDLL", @"C:\Windows\System32\rundll32.exe"));
        Assert.Equal(@"C:\Windows\System32\cmd.exe", Extract("cmd /c echo", @"C:\Windows\System32\cmd.exe"));
    }

    [Fact]
    public void Несуществующий_файл_возвращается_первым_словом()
    {
        Assert.Equal(@"C:\Gone\app.exe", Extract(@"C:\Gone\app.exe /x"));
        Assert.Null(Extract("   "));
    }

    // ── Признаки ─────────────────────────────────────────────────────────────

    [Fact]
    public void Папка_Windows_не_путается_с_похожими_именами()
    {
        Assert.True(StartupItemsService.IsUnder(@"C:\Windows\System32\a.exe", @"C:\Windows"));
        Assert.True(StartupItemsService.IsUnder(@"c:\windows\a.exe", @"C:\Windows\"));
        Assert.False(StartupItemsService.IsUnder(@"C:\Windows2\a.exe", @"C:\Windows"));
        Assert.False(StartupItemsService.IsUnder(null, @"C:\Windows"));
    }

    [Theory]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US", "Microsoft Corporation")]
    [InlineData("CN=\"Example, Inc.\", O=\"Example, Inc.\", C=US", "Example, Inc.")]
    [InlineData("CN=Someone", null)]
    public void Организация_берётся_из_поля_O(string subject, string? expected)
    {
        Assert.Equal(expected, StartupItemsService.OrganizationOf(subject));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", true)]
    [InlineData(@"C:\Windows\System32\wscript.exe", true)]
    [InlineData(@"C:\Windows\System32\cmd.exe", true)]
    [InlineData(@"C:\Windows\System32\SecurityHealthSystray.exe", false)]
    [InlineData(null, false)]
    public void Посредники_для_скриптов_не_делают_запись_системной(string? path, bool expected)
    {
        Assert.Equal(expected, StartupItemsService.IsScriptHost(path));
    }

    [Fact]
    public void RunOnce_не_выключается()
    {
        Assert.False(new StartupEntry { Source = StartupSource.RegistryRunOnce }.CanToggle);
        Assert.True(new StartupEntry { Source = StartupSource.RegistryRun }.CanToggle);
        Assert.True(new StartupEntry { Source = StartupSource.ScheduledTask }.CanToggle);
    }

    // ── Список ───────────────────────────────────────────────────────────────

    private static StartupViewModel ViewModelWith(params StartupEntry[] entries) =>
        new() { Source = () => Task.FromResult<IReadOnlyList<StartupEntry>>(entries) };

    [Fact]
    public async Task Системные_по_умолчанию_скрыты_и_показываются_по_галочке()
    {
        var vm = ViewModelWith(
            new StartupEntry { Name = "Steam", Enabled = true },
            new StartupEntry { Name = "SecurityHealth", Enabled = true, IsSystem = true, IsWindowsComponent = true });

        await vm.LoadAsync();
        Assert.Equal(new[] { "Steam" }, vm.Items.Select(i => i.Name));
        Assert.Contains("скрыто: 1", vm.Summary);

        vm.ShowSystem = true;
        Assert.Equal(new[] { "Steam", "SecurityHealth" }, vm.Items.Select(i => i.Name));
    }

    [Fact]
    public void Сторонние_записи_идут_выше_системных_а_внутри_по_имени()
    {
        var ordered = StartupViewModel.Order(new[]
        {
            new StartupEntry { Name = "b-system", IsSystem = true },
            new StartupEntry { Name = "Zoom" },
            new StartupEntry { Name = "a-system", IsSystem = true },
            new StartupEntry { Name = "Discord" },
        }).Select(e => e.Name);

        Assert.Equal(new[] { "Discord", "Zoom", "a-system", "b-system" }, ordered);
    }

    [Fact]
    public async Task Сбой_чтения_не_роняет_список_а_пишет_причину()
    {
        var vm = new StartupViewModel { Source = () => throw new InvalidOperationException("нет доступа") };

        await vm.LoadAsync();

        Assert.Empty(vm.Items);
        Assert.False(vm.IsLoading);
        Assert.Contains("не удалось", vm.StatusText);
    }

    [Fact]
    public void Строка_показывает_состояние_и_действие()
    {
        var on = new StartupItemRow(new StartupEntry { Name = "a", Enabled = true });
        var off = new StartupItemRow(new StartupEntry { Name = "b", Enabled = false });
        var once = new StartupItemRow(new StartupEntry { Name = "c", Source = StartupSource.RegistryRunOnce, Enabled = true });

        Assert.Equal(("Включено", "Выключить"), (on.StateLabel, on.ToggleText));
        Assert.Equal(("Выключено", "Включить"), (off.StateLabel, off.ToggleText));
        Assert.Equal("Однократно", once.StateLabel);
        Assert.False(once.CanClick);
    }
}
