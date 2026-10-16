using System;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class InstallStallDetectorTests
{
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private static InstallStallDetector Create() =>
        new(T0, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(90));

    [Fact]
    public void Observe_АктивностьМеняется_НеПрерывает()
    {
        var d = Create();
        for (int i = 0; i < 80; i++)
            Assert.Null(d.Observe(T0.AddMinutes(i), i * 1000));
    }

    [Fact]
    public void Observe_НетАктивности15Минут_Прерывает()
    {
        var d = Create();
        Assert.Null(d.Observe(T0, 500));
        Assert.Null(d.Observe(T0.AddMinutes(14), 500));
        var reason = d.Observe(T0.AddMinutes(15), 500);
        Assert.NotNull(reason);
        Assert.Contains("без признаков работы", reason);
    }

    [Fact]
    public void Observe_АктивностьВозобновилась_СрокОтсчитываетсяЗаново()
    {
        var d = Create();
        d.Observe(T0, 1);
        d.Observe(T0.AddMinutes(10), 2);
        Assert.Null(d.Observe(T0.AddMinutes(24), 2));
        Assert.NotNull(d.Observe(T0.AddMinutes(25), 2));
    }

    [Fact]
    public void Observe_ДольшеОбщегоПредела_Прерывает()
    {
        var d = Create();
        var reason = d.Observe(T0.AddMinutes(90), 123456);
        Assert.NotNull(reason);
        Assert.Contains("90", reason);
    }

    [Fact]
    public void WingetErrorMapper_ЗависаниеКлиентом_ЧитаемаяПричина()
    {
        var message = WingetErrorMapper.MapExitCode(WingetErrorMapper.StalledByClient);
        Assert.Contains("завис", message);
    }

    [Fact]
    public void ProcessActivityProbe_ДеревоСодержитКорень()
    {
        int self = Environment.ProcessId;
        Assert.Contains(self, ProcessActivityProbe.TreeOf(self));
        Assert.True(ProcessActivityProbe.Sample(self, null) > 0);
    }
}
