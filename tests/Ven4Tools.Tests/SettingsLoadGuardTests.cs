using Newtonsoft.Json;
using Ven4Tools.Helpers;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Файл настроек, не прочитанный при старте (занят антивирусом, облачной
/// синхронизацией), не должен затираться значениями по умолчанию при первом же
/// сохранении.
/// </summary>
public sealed class SettingsLoadGuardTests
{
    [Fact]
    public void TryRead_НетФайла_Missing()
    {
        using var dir = new TemporaryDirectory();

        var result = SettingsFileReader.TryRead(Path.Combine(dir.Path, "нет.json"), "[test]", out string text);

        Assert.Equal(SettingsReadResult.Missing, result);
        Assert.Equal("", text);
    }

    [Fact]
    public void TryRead_ФайлЗанят_Unreadable()
    {
        using var dir = new TemporaryDirectory();
        string path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllText(path, "{}");

        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(SettingsReadResult.Unreadable, SettingsFileReader.TryRead(path, "[test]", out _));
    }

    [Fact]
    public void СкрытыеПриложения_ФайлБылЗанятПриСтарте_СохранениеЕгоНеЗатирает()
    {
        using var dir = new TemporaryDirectory();
        string path = Path.Combine(dir.Path, "hidden.json");
        File.WriteAllText(path, "[\"a\",\"b\"]");

        HiddenAppsStore store;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            store = new HiddenAppsStore(path);
        }
        Assert.Equal(0, store.Count);

        // Файл уже свободен, но хранилище его так и не прочитало — писать нельзя.
        store.Save();

        Assert.Equal(new[] { "a", "b" },
            JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path))!.OrderBy(x => x));
    }

    [Fact]
    public void СкрытыеПриложения_ПослеОсвобожденияФайла_ИзменениеЛожитсяПоверхПрежних()
    {
        using var dir = new TemporaryDirectory();
        string path = Path.Combine(dir.Path, "hidden.json");
        File.WriteAllText(path, "[\"a\",\"b\"]");

        HiddenAppsStore store;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            store = new HiddenAppsStore(path);
        }

        store.Hide("c");

        Assert.Equal(new[] { "a", "b", "c" },
            JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path))!.OrderBy(x => x));
    }

    [Fact]
    public void СкрытыеПриложения_ПовреждённыйФайл_ОткладываетсяРядом()
    {
        using var dir = new TemporaryDirectory();
        string path = Path.Combine(dir.Path, "hidden.json");
        File.WriteAllText(path, "{ не json");

        var store = new HiddenAppsStore(path);
        store.Hide("a");

        Assert.Equal(new[] { "a" }, JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(path)));
        string backup = Assert.Single(Directory.GetFiles(dir.Path, "hidden.json.corrupt-*"));
        Assert.Equal("{ не json", File.ReadAllText(backup));
    }

    [Fact]
    public void ПользовательскиеПриложения_ФайлБылЗанятПриСтарте_ПрежниеНеТеряются()
    {
        using var dir = new TemporaryDirectory();
        string path = Path.Combine(dir.Path, "apps.json");
        var saved = new AppInfo { Id = "User.old", DisplayName = "Старое", IsUserAdded = true };
        new UserAppsStore(path, protect: false).Save(new List<AppInfo> { saved });

        var store = new UserAppsStore(path, protect: false);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Empty(store.Load());
        }

        // Владелец хранилища ничего не знает о «Старом» и сохраняет только новое.
        store.Save(new List<AppInfo>
        {
            new() { Id = "User.new", DisplayName = "Новое", IsUserAdded = true }
        });

        var onDisk = new UserAppsStore(path, protect: false).Load();
        Assert.Equal(new[] { "User.new", "User.old" }, onDisk.Select(a => a.Id).OrderBy(x => x));
    }

    [Fact]
    public void ПользовательскиеПриложения_ОбычноеСохранение_НеВозвращаетУдалённое()
    {
        using var dir = new TemporaryDirectory();
        string path = Path.Combine(dir.Path, "apps.json");
        var store = new UserAppsStore(path, protect: false);
        store.Save(new List<AppInfo>
        {
            new() { Id = "User.a", DisplayName = "A", IsUserAdded = true },
            new() { Id = "User.b", DisplayName = "B", IsUserAdded = true }
        });
        Assert.Equal(2, store.Load().Count);

        store.Save(new List<AppInfo> { new() { Id = "User.a", DisplayName = "A", IsUserAdded = true } });

        Assert.Equal(new[] { "User.a" }, store.Load().Select(a => a.Id));
    }
}
