using Ven4Tools.Services;
using Ven4Tools.ViewModels;
using Ven4Tools.Views.Tabs;

namespace Ven4Tools.Tests;

/// <summary>
/// Группа «Реклама и ИИ» во вкладке «Очистка»: целостность таблиц твиков, выбор по
/// версии Windows, откат на том же движке и проверка папок перед удалением OneDrive.
/// Реестр подменён — настоящая система не трогается.
/// </summary>
public sealed class DebloatAdsTweaksTests : IDisposable
{
    private sealed class FakeSystem : IDebloatSystemState
    {
        public Dictionary<string, int> Registry { get; } = new();

        private static string Key(string path, string name) => path + "|" + name;

        public (bool Exists, int Value) ReadDword(string path, string name) =>
            Registry.TryGetValue(Key(path, name), out int value) ? (true, value) : (false, 0);

        public bool WriteDword(string path, string name, int value) { Registry[Key(path, name)] = value; return true; }
        public bool DeleteValue(string path, string name) { Registry.Remove(Key(path, name)); return true; }
        public int? ReadServiceStartMode(string service) => null;
        public Task<bool> SetServiceStartModeAsync(string service, int mode, CancellationToken ct) => Task.FromResult(true);

        public bool Has(string path, string name) => Registry.ContainsKey(Key(path, name));
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-ads-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── Целостность каталога и таблиц ────────────────────────────────────────

    [Fact]
    public void Идентификаторы_твиков_уникальны_во_всём_каталоге()
    {
        var ids = DebloatCatalog.BuildItems().Select(i => i.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Прежние_идентификаторы_твиков_не_изменились()
    {
        // Они лежат в снапшотах конфигурации пользователей.
        var ids = DebloatCatalog.BuildItems().Select(i => i.Id).ToHashSet();
        foreach (var id in new[] { "telemetry", "activity_history", "advertising_id", "content_delivery",
                                   "cortana_registry", "input_tracking", "diag_track",
                                   "svc_diagtrack", "svc_sysmain", "svc_dmwappushsvc", "Microsoft.XboxApp" })
            Assert.Contains(id, ids);
    }

    [Fact]
    public void Каждый_твик_рекламы_чем_то_исполняется()
    {
        foreach (var item in DebloatCatalog.BuildItems().Where(i => i.Category == "ads"))
        {
            bool handled = DebloatTweakExecutor.RegistryChangesOf("ads", item.Id).Count > 0
                           || DebloatTweakExecutor.IsAppxTweak("ads", item.Id)
                           || item.Id == DebloatTweakExecutor.OneDriveId;
            Assert.True(handled, $"Твик «{item.Id}» есть в каталоге, но исполнитель о нём не знает.");
        }
    }

    [Fact]
    public void Каждая_запись_таблиц_реестра_есть_в_каталоге()
    {
        var catalog = DebloatCatalog.BuildItems().ToDictionary(i => i.Id, i => i.Category);
        foreach (var (category, id, _) in DebloatTweakExecutor.AllRegistryTweaks())
        {
            Assert.True(catalog.TryGetValue(id, out var inCatalog), $"«{id}» нет в каталоге");
            Assert.Equal(category, inCatalog);
        }
    }

    [Fact]
    public void Одно_значение_реестра_меняет_не_больше_одного_твика()
    {
        // Иначе откат одного твика вернул бы значение, которое держит другой.
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, id, changes) in DebloatTweakExecutor.AllRegistryTweaks())
        {
            foreach (var change in changes)
            {
                string key = change.Path + "|" + change.Name;
                Assert.False(seen.TryGetValue(key, out var other), $"{key} меняют «{other}» и «{id}»");
                seen[key] = id;
            }
        }
    }

    [Fact]
    public void Пути_реестра_только_HKLM_или_HKCU()
    {
        foreach (var (_, id, changes) in DebloatTweakExecutor.AllRegistryTweaks())
            foreach (var change in changes)
                Assert.True(change.Path.StartsWith(@"HKLM:\") || change.Path.StartsWith(@"HKCU:\"), $"{id}: {change.Path}");
    }

    [Fact]
    public void Перезапуск_Проводника_просят_только_известные_твики()
    {
        foreach (var id in new[] { "ads_widgets", "ads_copilot", "ads_explorer_sync", "ads_search_web" })
            Assert.True(DebloatTweakExecutor.NeedsExplorerRestart(id));
        Assert.False(DebloatTweakExecutor.NeedsExplorerRestart("telemetry"));
        Assert.False(DebloatTweakExecutor.NeedsExplorerRestart(DebloatTweakExecutor.OneDriveId));
    }

    // ── Версия Windows ───────────────────────────────────────────────────────

    [Fact]
    public void На_Windows_11_виджеты_и_Recall_есть_а_новостей_Windows_10_нет()
    {
        var ids = DebloatCatalog.BuildItemsFor(isWindows11: true).Select(i => i.Id).ToList();
        Assert.Contains("ads_widgets", ids);
        Assert.Contains("ads_recall", ids);
        Assert.DoesNotContain("ads_news_feeds", ids);
    }

    [Fact]
    public void На_Windows_10_новости_и_интересы_есть_а_виджетов_и_Recall_нет()
    {
        var ids = DebloatCatalog.BuildItemsFor(isWindows11: false).Select(i => i.Id).ToList();
        Assert.Contains("ads_news_feeds", ids);
        Assert.DoesNotContain("ads_widgets", ids);
        Assert.DoesNotContain("ads_recall", ids);
        Assert.Contains("ads_copilot", ids);
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, true)]
    [InlineData("win11", true, true)]
    [InlineData("win11", false, false)]
    [InlineData("win10", true, false)]
    [InlineData("win10", false, true)]
    public void AppliesTo_учитывает_версию(string? os, bool isWindows11, bool expected)
    {
        var item = new DebloatItem("x", "x", "ads", "safe", "x", os);
        Assert.Equal(expected, DebloatCatalog.AppliesTo(item, isWindows11));
    }

    // ── Откат и проверка применённого на том же движке ───────────────────────

    [Fact]
    public async Task Политика_Copilot_откатывается_удалением_значений_которых_не_было()
    {
        var system = new FakeSystem();
        var undo = new DebloatUndoService(Path.Combine(_dir, "undo.json"), system);
        var changes = DebloatTweakExecutor.RegistryChangesOf("ads", "ads_copilot");

        undo.Capture("ads_copilot", changes, null);
        foreach (var change in changes) system.WriteDword(change.Path, change.Name, change.Value);

        Assert.True(await undo.UndoAsync("ads_copilot"));
        foreach (var change in changes) Assert.False(system.Has(change.Path, change.Name));
    }

    [Fact]
    public async Task Откат_рекламного_твика_возвращает_прежнее_значение()
    {
        var system = new FakeSystem();
        var undo = new DebloatUndoService(Path.Combine(_dir, "undo.json"), system);
        var change = DebloatTweakExecutor.RegistryChangesOf("ads", "ads_explorer_sync").Single();
        system.WriteDword(change.Path, change.Name, 1);

        undo.Capture("ads_explorer_sync", new[] { change }, null);
        system.WriteDword(change.Path, change.Name, change.Value);

        Assert.True(await undo.UndoAsync("ads_explorer_sync"));
        Assert.Equal((true, 1), system.ReadDword(change.Path, change.Name));
    }

    [Fact]
    public void Вернувшаяся_реклама_видна_проверкой_применённого()
    {
        var system = new FakeSystem();
        var applied = new[] { ("ads", "ads_lock_screen") };
        Assert.Equal(new[] { "ads_lock_screen" }, DebloatDriftService.FindDrifted(applied, system, null));

        foreach (var c in DebloatTweakExecutor.RegistryChangesOf("ads", "ads_lock_screen"))
            system.WriteDword(c.Path, c.Name, c.Value);
        Assert.Empty(DebloatDriftService.FindDrifted(applied, system, null));
    }

    [Fact]
    public void Вернувшееся_приложение_Copilot_видно_проверкой_применённого()
    {
        var applied = new[] { ("ads", DebloatTweakExecutor.CopilotAppId) };
        Assert.Equal(new[] { DebloatTweakExecutor.CopilotAppId },
            DebloatDriftService.FindDrifted(applied, new FakeSystem(), new[] { "Microsoft.Copilot" }));
        Assert.Empty(DebloatDriftService.FindDrifted(applied, new FakeSystem(), new[] { "Microsoft.WindowsCalculator" }));
    }

    [Fact]
    public void OneDrive_не_считается_вернувшимся_по_реестру()
    {
        var applied = new[] { ("ads", DebloatTweakExecutor.OneDriveId) };
        Assert.Empty(DebloatDriftService.FindDrifted(applied, new FakeSystem(), null));
        Assert.False(DebloatTweakExecutor.IsUndoable("ads", DebloatTweakExecutor.OneDriveId));
    }

    // ── OneDrive: перенесённые папки и установщик ────────────────────────────

    [Fact]
    public void Папки_в_OneDrive_находятся_по_корню_и_по_имени_каталога()
    {
        var folders = new (string, string?)[]
        {
            ("Рабочий стол", @"C:\Users\u\OneDrive\Desktop"),
            ("Документы", @"D:\Work\OneDrive - Компания\Documents"),
            ("Документы", @"D:\Work\OneDrive - Компания\Documents"),
            ("Изображения", @"C:\Users\u\Pictures"),
            ("Музыка", null),
        };
        var found = OneDriveRemovalService.FindFoldersInOneDrive(folders, new[] { @"C:\Users\u\OneDrive" });
        Assert.Equal(new[] { "Рабочий стол", "Документы" }, found);
    }

    [Fact]
    public void Папка_под_корнем_OneDrive_с_другим_именем_тоже_найдена()
    {
        var folders = new (string, string?)[] { ("Документы", @"E:\Cloud\Docs") };
        Assert.Equal(new[] { "Документы" },
            OneDriveRemovalService.FindFoldersInOneDrive(folders, new[] { @"E:\Cloud\" }));
        Assert.Empty(OneDriveRemovalService.FindFoldersInOneDrive(folders, new string?[] { null, "" }));
    }

    [Fact]
    public void Обычные_папки_профиля_не_мешают_удалению()
    {
        var folders = new (string, string?)[]
        {
            ("Рабочий стол", @"C:\Users\u\Desktop"),
            ("Документы", @"C:\Users\u\Documents"),
        };
        Assert.Empty(OneDriveRemovalService.FindFoldersInOneDrive(folders, new[] { @"C:\Users\u\OneDrive" }));
    }

    [Theory]
    [InlineData(@"""C:\Users\u\AppData\Local\Microsoft\OneDrive\25.1\OneDriveSetup.exe""  /uninstall ", @"C:\Users\u\AppData\Local\Microsoft\OneDrive\25.1\OneDriveSetup.exe")]
    [InlineData(@"C:\Program Files\Microsoft OneDrive\25.1\OneDriveSetup.exe  /uninstall  /allusers ", @"C:\Program Files\Microsoft OneDrive\25.1\OneDriveSetup.exe")]
    [InlineData(@"C:\Windows\evil.exe /uninstall", null)]
    [InlineData("", null)]
    public void Путь_установщика_берётся_из_строки_удаления_без_аргументов(string uninstall, string? expected)
    {
        Assert.Equal(expected, OneDriveRemovalService.ParseSetupPath(uninstall));
    }

    [Fact]
    public void Установщик_из_профиля_запускается_только_без_прав_администратора()
    {
        string local = @"C:\Users\u\AppData\Local";
        var machine = new[] { @"C:\Program Files", @"C:\Windows\System32" };

        Assert.True(OneDriveRemovalService.IsPerUserSetup(@"C:\Users\u\AppData\Local\Microsoft\OneDrive\25.1\OneDriveSetup.exe", local, machine));
        Assert.False(OneDriveRemovalService.IsPerUserSetup(@"C:\Program Files\Microsoft OneDrive\25.1\OneDriveSetup.exe", local, machine));
        Assert.False(OneDriveRemovalService.IsPerUserSetup(@"C:\Windows\System32\OneDriveSetup.exe", local, machine));
        Assert.Null(OneDriveRemovalService.IsPerUserSetup(@"C:\Users\u\Downloads\OneDriveSetup.exe", local, machine));
        Assert.Null(OneDriveRemovalService.IsPerUserSetup(@"C:\Program Files\Microsoft OneDrive\other.exe", local, machine));
        Assert.Null(OneDriveRemovalService.IsPerUserSetup(@"C:\Program Files Evil\OneDriveSetup.exe", local, machine));
    }
}
