using Ven4Tools.Helpers;

namespace Ven4Tools.Tests;

/// <summary>
/// Чтение списка перед «изменить → записать»: непрочитанный файл не должен
/// превращаться в пустой список, который следующая запись сохранит поверх данных.
/// </summary>
public sealed class JsonListFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Ven4Tools.JsonListFileTests." + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "list.json");

    public JsonListFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void TryLoad_НетФайла_ПустойСписок()
    {
        var list = JsonListFile.TryLoad<int>(FilePath, "[test]");
        Assert.NotNull(list);
        Assert.Empty(list!);
    }

    [Fact]
    public void TryLoad_ВалидныйФайл_Содержимое()
    {
        File.WriteAllText(FilePath, "[1,2,3]");
        Assert.Equal(new[] { 1, 2, 3 }, JsonListFile.TryLoad<int>(FilePath, "[test]"));
    }

    [Fact]
    public void TryLoad_ФайлЗанят_Null()
    {
        File.WriteAllText(FilePath, "[1]");
        using var locked = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Null(JsonListFile.TryLoad<int>(FilePath, "[test]"));
    }

    [Fact]
    public void TryLoad_ПовреждённыйФайл_ОткладываетсяИПустойСписок()
    {
        File.WriteAllText(FilePath, "{ не json");
        var list = JsonListFile.TryLoad<int>(FilePath, "[test]");

        Assert.NotNull(list);
        Assert.Empty(list!);
        Assert.False(File.Exists(FilePath));
        var backup = Assert.Single(Directory.GetFiles(_dir, "list.json.corrupt-*"));
        Assert.Equal("{ не json", File.ReadAllText(backup));
    }
}
