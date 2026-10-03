using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Очередь установки, переживающая перезагрузку: что остаётся в файле, когда клиент
/// не дошёл до конца пачки, и что предлагается при следующем запуске.
/// </summary>
public sealed class PendingInstallQueueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-queue-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;
    private readonly PendingInstallQueue _queue;
    private static readonly DateTime Now = DateTime.UtcNow;

    public PendingInstallQueueTests()
    {
        _path = Path.Combine(_dir, "pending_install.json");
        _queue = new PendingInstallQueue(_path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void БезФайла_ПродолжатьНечего() => Assert.Null(_queue.Load(Now));

    [Fact]
    public void ПрерваннаяПачка_ОставляетНеустановленное()
    {
        _queue.Begin(new[] { "firefox", "7zip", "vlc" }, "D:");
        _queue.MarkDone("FIREFOX");

        // «Перезагрузка»: тот же файл читает уже другой экземпляр.
        var pending = new PendingInstallQueue(_path).Load(DateTime.UtcNow);

        Assert.NotNull(pending);
        Assert.Equal(new[] { "7zip", "vlc" }, pending!.AppIds);
        Assert.Equal("D:", pending.InstallDrive);
    }

    [Fact]
    public void ВсёУстановлено_ФайлИсчезает()
    {
        _queue.Begin(new[] { "firefox", "7zip" }, null);
        _queue.MarkDone("firefox");
        _queue.MarkDone("7zip");

        Assert.False(File.Exists(_path));
        Assert.Null(_queue.Load(DateTime.UtcNow));
    }

    [Fact]
    public void КонецПачки_ОчищаетОчередь_ДажеЕслиБылиНеудачи()
    {
        _queue.Begin(new[] { "firefox", "7zip" }, null);
        _queue.MarkDone("firefox");

        _queue.Clear();

        Assert.Null(_queue.Load(DateTime.UtcNow));
    }

    [Fact]
    public void УстаревшаяОчередь_НеПредлагаетсяИУбирается()
    {
        _queue.Begin(new[] { "firefox" }, null);

        Assert.Null(_queue.Load(DateTime.UtcNow + PendingInstallQueue.MaxAge + TimeSpan.FromHours(1)));
        Assert.False(File.Exists(_path));
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"AppIds\":[\"\",\"  \"],\"StartedUtc\":\"2026-10-03T00:00:00Z\"}")]
    public void ИспорченныйИлиПустойФайл_НеРоняетИУбирается(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path, content);

        Assert.Null(_queue.Load(new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc)));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Повторы_ВОчередиСхлопываются()
    {
        _queue.Begin(new[] { "vlc", "VLC", "firefox" }, null);

        Assert.Equal(new[] { "vlc", "firefox" }, _queue.Load(DateTime.UtcNow)!.AppIds);
    }

    [Fact]
    public void ОписаниеОстатка_НеРастягиваетВопросНаВесьЭкран()
    {
        Assert.Equal("a, b", App.DescribeIds(new[] { "a", "b" }));
        Assert.Equal("a, b, c, d, e и ещё 2", App.DescribeIds(new[] { "a", "b", "c", "d", "e", "f", "g" }));
    }
}
