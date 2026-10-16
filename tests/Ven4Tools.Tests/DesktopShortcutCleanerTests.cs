using System;
using System.IO;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class DesktopShortcutCleanerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "v4t-desktop-" + Guid.NewGuid().ToString("N"));

    public DesktopShortcutCleanerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void RemoveNew_УбираетТолькоПоявившиесяЯрлыки()
    {
        string old = Path.Combine(_dir, "Старый.lnk");
        File.WriteAllText(old, "");
        var snapshot = DesktopShortcutCleaner.Take(new[] { _dir });

        string fresh = Path.Combine(_dir, "Новая программа.lnk");
        string url = Path.Combine(_dir, "Сайт.url");
        string doc = Path.Combine(_dir, "заметка.txt");
        File.WriteAllText(fresh, "");
        File.WriteAllText(url, "");
        File.WriteAllText(doc, "");

        var removed = DesktopShortcutCleaner.RemoveNew(snapshot, new[] { _dir }, null);

        Assert.Equal(2, removed.Count);
        Assert.True(File.Exists(old));
        Assert.True(File.Exists(doc));
        Assert.False(File.Exists(fresh));
        Assert.False(File.Exists(url));
    }

    [Fact]
    public void RemoveNew_ЯрлыкСозданныйДоСнимка_НеТрогает()
    {
        var snapshot = DesktopShortcutCleaner.Take(new[] { _dir });
        string early = Path.Combine(_dir, "Ранний.lnk");
        File.WriteAllText(early, "");
        File.SetCreationTimeUtc(early, DateTime.UtcNow.AddHours(-1));

        var removed = DesktopShortcutCleaner.RemoveNew(snapshot, new[] { _dir }, null);

        Assert.Empty(removed);
        Assert.True(File.Exists(early));
    }

    [Fact]
    public void IsFreshlyInstalled_ПроцессИФайлПослеНачала_Истина()
    {
        var start = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(NewAppWindowCloser.IsFreshlyInstalled(start.AddSeconds(40), start.AddSeconds(20), start));
    }

    [Fact]
    public void IsFreshlyInstalled_СтараяПрограмма_Ложь()
    {
        var start = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
        // Окно открыто после начала установки, но сама программа стояла давно —
        // это не результат установки, закрывать нельзя.
        Assert.False(NewAppWindowCloser.IsFreshlyInstalled(start.AddSeconds(40), start.AddDays(-30), start));
        // Процесс запущен до начала установки.
        Assert.False(NewAppWindowCloser.IsFreshlyInstalled(start.AddSeconds(-5), start.AddSeconds(20), start));
    }
}
