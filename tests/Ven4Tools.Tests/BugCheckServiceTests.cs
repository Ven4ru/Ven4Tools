using Ven4Tools.Services.Diagnostics;

namespace Ven4Tools.Tests;

public sealed class BugCheckServiceTests
{
    [Fact]
    public void ParseWerParam1_КодИЧетыреПараметра()
    {
        var parsed = BugCheckService.ParseWerParam1(
            "0x0000000a (0xffffb38c4d0f6000, 0x0000000000000002, 0x0000000000000000, 0xfffff8017d2a1b3c)");

        Assert.NotNull(parsed);
        Assert.Equal(0xAu, parsed!.Value.Code);
        Assert.Equal(new ulong[] { 0xffffb38c4d0f6000, 2, 0, 0xfffff8017d2a1b3c }, parsed.Value.Parameters);
    }

    [Fact]
    public void ParseWerParam1_КодСоСтаршимБитом()
    {
        var parsed = BugCheckService.ParseWerParam1("0xc000021a (0x0, 0x0, 0x0, 0x0)");
        Assert.Equal(0xC000021Au, parsed!.Value.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Компьютер был перезагружен")]
    [InlineData("0xZZ (1, 2)")]
    public void ParseWerParam1_НеКод_Null(string? text)
    {
        Assert.Null(BugCheckService.ParseWerParam1(text));
    }

    [Fact]
    public void ParseWerParam1_БитыйПараметр_КодЕстьПараметровНет()
    {
        var parsed = BugCheckService.ParseWerParam1("0x00000133 (0x1, xyz, 0x2, 0x3)");
        Assert.Equal(0x133u, parsed!.Value.Code);
        Assert.Empty(parsed.Value.Parameters);
    }

    [Fact]
    public void ParseKernelPowerParameters_ДесятичныеИШестнадцатеричные()
    {
        var data = new Dictionary<string, string>
        {
            ["BugcheckParameter1"] = "0x10",
            ["BugcheckParameter2"] = "2",
            ["BugcheckParameter3"] = "0x0",
            ["BugcheckParameter4"] = "18446735277616529408"
        };
        Assert.Equal(new ulong[] { 16, 2, 0, 18446735277616529408 }, BugCheckService.ParseKernelPowerParameters(data));
    }

    [Fact]
    public void ParseKernelPowerParameters_НетОдногоПараметра_Пусто()
    {
        var data = new Dictionary<string, string> { ["BugcheckParameter1"] = "1", ["BugcheckParameter2"] = "2" };
        Assert.Empty(BugCheckService.ParseKernelPowerParameters(data));
    }

    [Fact]
    public void FormatParameters_ШестнадцатьЗнаков()
    {
        Assert.Equal("0x0000000000000002, 0xFFFFF8017D2A1B3C",
            BugCheckService.FormatParameters(new ulong[] { 2, 0xfffff8017d2a1b3c }));
    }

    private static WerBugCheckReport Report(DateTime time, uint code) => new()
    {
        TimeCreated = time, Code = code, Parameters = Array.Empty<ulong>()
    };

    [Fact]
    public void FindMatchingReport_БлижайшийВОкнеСТемЖеКодом()
    {
        var crash = new DateTime(2026, 10, 20, 10, 0, 0);
        var near = Report(crash.AddMinutes(1), 0xA);
        var reports = new[]
        {
            Report(crash.AddMinutes(2), 0xD1),   // другой код
            Report(crash.AddMinutes(5), 0xA),
            near,
            Report(crash.AddHours(2), 0xA)       // вне окна
        };
        Assert.Same(near, BugCheckService.FindMatchingReport(crash, 0xA, reports));
    }

    [Fact]
    public void FindMatchingReport_КодНеизвестен_БерётсяЛюбойВОкне()
    {
        var crash = new DateTime(2026, 10, 20, 10, 0, 0);
        var report = Report(crash.AddMinutes(3), 0x133);
        Assert.Same(report, BugCheckService.FindMatchingReport(crash, 0, new[] { report }));
    }

    [Fact]
    public void FindMatchingReport_ВнеОкна_Null()
    {
        var crash = new DateTime(2026, 10, 20, 10, 0, 0);
        Assert.Null(BugCheckService.FindMatchingReport(crash, 0xA, new[] { Report(crash.AddMinutes(30), 0xA) }));
    }

    [Fact]
    public void ParseSetupApiLog_ИмпортИDiInstallDriverВОкне_ТолькоИмяInf()
    {
        var lines = new[]
        {
            ">>>  [Setup Import Driver Package - C:\\Users\\Иван\\AppData\\Local\\Temp\\nv\\nvlt.inf]",
            ">>>  Section start 2026/10/18 10:45:38.828",
            "     inf: something",
            "<<<  Section end 2026/10/18 10:45:40.000",
            ">>>  [Device Install (Hardware initiated) - USB\\VID_1234]",
            ">>>  Section start 2026/10/18 11:00:00.000",
            ">>>  [Device Install (DiInstallDriver) - C:\\Program Files\\Vendor\\drivers\\gameflt.inf]",
            ">>>  Section start 2026/10/19 07:16:33.919",
            ">>>  [Setup Import Driver Package - C:\\old\\old.inf]",
            ">>>  Section start 2026/09/01 07:16:33.919",
            ">>>  [Setup Import Driver Package - C:\\Office\\OneNote\\\\prnms006.inf]",
            ">>>  Section start 2026/10/18 12:00:00.000"
        };
        var result = BugCheckService.ParseSetupApiLog(lines,
            new DateTime(2026, 10, 17), new DateTime(2026, 10, 20));

        Assert.Equal(new[] { "nvlt.inf", "gameflt.inf", "prnms006.inf" }, result.Select(r => r.Name).ToArray());
        Assert.Equal(new DateTime(2026, 10, 18, 10, 45, 38), result[0].Time);
        // Путь профиля (имя пользователя) в подпись не попадает.
        Assert.DoesNotContain(result, r => r.Name.Contains("Иван"));
    }

    [Fact]
    public void DescribeKernelPnpConfigured_ОбновлённыйСторонний_ИмяПакетаПоставщикВерсия()
    {
        var data = new Dictionary<string, string>
        {
            ["DriverName"] = "oem146.inf",
            ["DriverPackageId"] = "xvdd.inf_amd64_8f15a2927d7ab02d",
            ["DriverProvider"] = "Xbox",
            ["DriverVersion"] = "10.0.22030.1",
            ["DriverInbox"] = "false",
            ["DeviceUpdated"] = "true"
        };
        Assert.Equal("xvdd.inf (Xbox, 10.0.22030.1)", BugCheckService.DescribeKernelPnpConfigured(data));
    }

    [Theory]
    [InlineData("false", "false")]
    [InlineData("true", "true")]
    public void DescribeKernelPnpConfigured_НеОбновлялсяИлиВстроенный_Null(string updated, string inbox)
    {
        var data = new Dictionary<string, string>
        {
            ["DriverName"] = "volume.inf", ["DeviceUpdated"] = updated, ["DriverInbox"] = inbox
        };
        Assert.Null(BugCheckService.DescribeKernelPnpConfigured(data));
    }

    [Fact]
    public void DescribeUserPnpInstall_ОписаниеПоставщикВерсияInf()
    {
        var data = new Dictionary<string, string>
        {
            ["DriverDescription"] = "Realtek PCIe GbE Family Controller",
            ["DriverProvider"] = "Realtek",
            ["DriverVersion"] = "10.68.0",
            ["DriverName"] = "C:\\Windows\\INF\\oem42.inf"
        };
        // Поставщик уже есть в описании — повторно не пишется.
        Assert.Equal("Realtek PCIe GbE Family Controller (10.68.0, oem42.inf)", BugCheckService.DescribeUserPnpInstall(data));
    }

    [Fact]
    public void DescribeUserPnpInstall_БезИмени_Null()
    {
        Assert.Null(BugCheckService.DescribeUserPnpInstall(new Dictionary<string, string> { ["DriverVersion"] = "1.0" }));
    }

    [Fact]
    public void MergeDriverInstalls_ОдинДрайверИзДвухИсточников_ОднаСтрокаПодробнееИПозже()
    {
        var t = new DateTime(2026, 10, 19, 7, 16, 0);
        var merged = BugCheckService.MergeDriverInstalls(new[]
        {
            new DriverInstallRecord { Time = t, Name = "xvdd.inf" },
            new DriverInstallRecord { Time = t.AddSeconds(2), Name = "xvdd.inf (Xbox, 10.0.22030.1)" },
            new DriverInstallRecord { Time = t.AddDays(-1), Name = "nvlt.inf" }
        });

        Assert.Equal(2, merged.Count);
        Assert.Equal("xvdd.inf (Xbox, 10.0.22030.1)", merged[0].Name);
        Assert.Equal(t.AddSeconds(2), merged[0].Time);
        Assert.Equal("nvlt.inf", merged[1].Name);
    }
}
