using Ven4Tools.Services.Diagnostics;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

public sealed class Win11CpuMatcherTests
{
    [Theory]
    [InlineData("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", "Intel Core i7-8700K")]
    [InlineData("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", "11th Gen Intel Core i7-1165G7")]
    [InlineData("AMD Ryzen 5 1600 Six-Core Processor", "AMD Ryzen 5 1600")]
    [InlineData("AMD Ryzen 7 4800H with Radeon Graphics", "AMD Ryzen 7 4800H")]
    [InlineData("AMD FX(tm)-8350 Eight-Core Processor", "AMD FX-8350")]
    [InlineData("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU", "Snapdragon X Elite - X1E78100 - Qualcomm Oryon")]
    public void Normalize_УбираетЗнакиЧастотуИПодписи(string raw, string expected)
    {
        Assert.Equal(expected, Win11CpuMatcher.Normalize(raw));
    }

    [Theory]
    // В списке
    [InlineData("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", CpuSupport.Supported)]
    [InlineData("Intel(R) Core(TM) i5-8250U CPU @ 1.60GHz", CpuSupport.Supported)]
    [InlineData("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz", CpuSupport.Supported)]
    [InlineData("Intel(R) Core(TM) i5-1035G1 CPU @ 1.00GHz", CpuSupport.Supported)]
    [InlineData("12th Gen Intel(R) Core(TM) i5-12400F", CpuSupport.Supported)]
    [InlineData("12th Gen Intel(R) Core(TM) i7-1255U", CpuSupport.Supported)]
    [InlineData("13th Gen Intel(R) Core(TM) i9-13900K", CpuSupport.Supported)]
    [InlineData("Intel(R) Core(TM) i5-14600K", CpuSupport.Supported)]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", CpuSupport.Supported)]
    [InlineData("Intel(R) Core(TM) i7-7820HQ CPU @ 2.90GHz", CpuSupport.Supported)]
    [InlineData("Intel(R) Celeron(R) N4020 CPU @ 1.10GHz", CpuSupport.Supported)]
    [InlineData("Intel(R) Pentium(R) Gold G5400 CPU @ 3.70GHz", CpuSupport.Supported)]
    [InlineData("Intel(R) N100", CpuSupport.Supported)]
    [InlineData("Intel(R) Xeon(R) W-2245 CPU @ 3.90GHz", CpuSupport.Supported)]
    [InlineData("AMD Ryzen 5 2600 Six-Core Processor", CpuSupport.Supported)]
    [InlineData("AMD Ryzen 3 2200G with Radeon Vega Graphics", CpuSupport.Supported)]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor", CpuSupport.Supported)]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", CpuSupport.Supported)]
    [InlineData("AMD Ryzen AI 9 HX 370 w/ Radeon 890M", CpuSupport.Supported)]
    [InlineData("AMD Athlon Gold 3150U with Radeon Graphics", CpuSupport.Supported)]
    [InlineData("AMD Ryzen Threadripper 2950X 16-Core Processor", CpuSupport.Supported)]
    [InlineData("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU", CpuSupport.Supported)]
    [InlineData("Snapdragon (TM) 8cx Gen 3 @ 3.0 GHz", CpuSupport.Supported)]
    [InlineData("Microsoft SQ1 @ 3.0 GHz", CpuSupport.Supported)]
    // Нет в списке
    [InlineData("Intel(R) Core(TM) i7-7700HQ CPU @ 2.80GHz", CpuSupport.NotSupported)]
    [InlineData("Intel(R) Core(TM) i7-4790K CPU @ 4.00GHz", CpuSupport.NotSupported)]
    [InlineData("Intel(R) Core(TM) i5-3470 CPU @ 3.20GHz", CpuSupport.NotSupported)]
    [InlineData("Intel(R) Core(TM) i7 CPU 920 @ 2.67GHz", CpuSupport.Unknown)]
    [InlineData("Intel(R) Core(TM)2 Duo CPU E8400 @ 3.00GHz", CpuSupport.NotSupported)]
    [InlineData("Intel(R) Pentium(R) CPU G4560 @ 3.50GHz", CpuSupport.NotSupported)]
    [InlineData("Intel(R) Celeron(R) CPU N3060 @ 1.60GHz", CpuSupport.NotSupported)]
    [InlineData("Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz", CpuSupport.NotSupported)]
    [InlineData("AMD Ryzen 7 1700X Eight-Core Processor", CpuSupport.NotSupported)]
    [InlineData("AMD Athlon 200GE with Radeon Vega Graphics", CpuSupport.NotSupported)]
    [InlineData("AMD FX(tm)-8350 Eight-Core Processor", CpuSupport.NotSupported)]
    // Не распознано — честное «нет данных»
    [InlineData("Some Future CPU 9000", CpuSupport.Unknown)]
    [InlineData("", CpuSupport.Unknown)]
    [InlineData(null, CpuSupport.Unknown)]
    public void Match_ВстроенныеПравила(string? name, CpuSupport expected)
    {
        Assert.Equal(expected, Win11CpuMatcher.Embedded.Match(name).Support);
    }

    [Fact]
    public void Embedded_ПравилаЗагружены()
    {
        Assert.True(Win11CpuMatcher.Embedded.SupportedRuleCount >= 20);
        Assert.True(Win11CpuMatcher.Embedded.UnsupportedRuleCount >= 10);
    }

    [Fact]
    public void Parse_НеверноеРегулярноеВыражение_Пропускается()
    {
        var matcher = Win11CpuMatcher.Parse("""
        { "supported": [ { "pattern": "([", "note": "битое" }, { "pattern": "\\bRyzen 5 5600\\b", "note": "ок" } ] }
        """);
        Assert.Equal(1, matcher.SupportedRuleCount);
        Assert.Equal(CpuSupport.Supported, matcher.Match("AMD Ryzen 5 5600").Support);
    }
}

public sealed class Win11RequirementsTests
{
    [Theory]
    [InlineData("2.0, 0, 1.38", 2, 0)]
    [InlineData("1.2, 2, 3", 1, 2)]
    [InlineData("2", 2, 0)]
    public void ParseTpmSpecMajor(string spec, int major, int minor)
    {
        var v = Win11Requirements.ParseTpmSpecMajor(spec)!;
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
    }

    [Fact]
    public void Tpm_20Включён_Pass()
    {
        Assert.Equal(Win11CheckStatus.Pass, Win11Requirements.EvaluateTpm(true, true, true, "2.0, 0, 1.38").Status);
    }

    [Fact]
    public void Tpm_НеНайден_FailСПодсказкойПроPttИFtpm()
    {
        var r = Win11Requirements.EvaluateTpm(false, null, null, null);
        Assert.Equal(Win11CheckStatus.Fail, r.Status);
        Assert.Contains("PTT", r.Hint);
        Assert.Contains("fTPM", r.Hint);
    }

    [Fact]
    public void Tpm_Версия12_Fail() =>
        Assert.Equal(Win11CheckStatus.Fail, Win11Requirements.EvaluateTpm(true, true, true, "1.2, 2, 3").Status);

    [Fact]
    public void Tpm_Выключен_Fail() =>
        Assert.Equal(Win11CheckStatus.Fail, Win11Requirements.EvaluateTpm(true, false, false, "2.0, 0, 1.38").Status);

    [Fact]
    public void Tpm_ОпросНеУдался_Unknown() =>
        Assert.Equal(Win11CheckStatus.Unknown, Win11Requirements.EvaluateTpm(null, null, null, null).Status);

    [Theory]
    [InlineData(FirmwareKind.Uefi, 1, Win11CheckStatus.Pass)]
    [InlineData(FirmwareKind.Uefi, 0, Win11CheckStatus.Warning)]
    [InlineData(FirmwareKind.Bios, null, Win11CheckStatus.Fail)]
    [InlineData(FirmwareKind.Unknown, null, Win11CheckStatus.Unknown)]
    public void SecureBoot(FirmwareKind firmware, int? value, Win11CheckStatus expected) =>
        Assert.Equal(expected, Win11Requirements.EvaluateSecureBoot(firmware, value).Status);

    [Theory]
    [InlineData(FirmwareKind.Uefi, Win11CheckStatus.Pass)]
    [InlineData(FirmwareKind.Bios, Win11CheckStatus.Fail)]
    [InlineData(FirmwareKind.Unknown, Win11CheckStatus.Unknown)]
    public void Firmware(FirmwareKind firmware, Win11CheckStatus expected) =>
        Assert.Equal(expected, Win11Requirements.EvaluateFirmware(firmware).Status);

    [Fact]
    public void Разметка_Mbr_FailСПодсказкойMbr2gptБезЗапуска()
    {
        var r = Win11Requirements.EvaluatePartitionStyle(1);
        Assert.Equal(Win11CheckStatus.Fail, r.Status);
        Assert.Contains("mbr2gpt /validate", r.Hint);
        Assert.Contains("не запускает", r.Hint);
    }

    [Theory]
    [InlineData(2, Win11CheckStatus.Pass)]
    [InlineData(0, Win11CheckStatus.Unknown)]
    [InlineData(null, Win11CheckStatus.Unknown)]
    public void Разметка(int? style, Win11CheckStatus expected) =>
        Assert.Equal(expected, Win11Requirements.EvaluatePartitionStyle(style).Status);

    [Theory]
    [InlineData(true, true, Win11CheckStatus.Pass)]
    [InlineData(false, true, Win11CheckStatus.Fail)]
    [InlineData(true, false, Win11CheckStatus.Fail)]
    [InlineData(null, null, Win11CheckStatus.Unknown)]
    public void Инструкции(bool? popcnt, bool? sse42, Win11CheckStatus expected) =>
        Assert.Equal(expected, Win11Requirements.EvaluateInstructions(popcnt, sse42).Status);

    [Theory]
    [InlineData(4UL * 1024 * 1024, Win11CheckStatus.Pass)]
    [InlineData(16UL * 1024 * 1024, Win11CheckStatus.Pass)]
    [InlineData(2UL * 1024 * 1024, Win11CheckStatus.Fail)]
    [InlineData(3UL * 1024 * 1024, Win11CheckStatus.Fail)]
    public void Память(ulong kb, Win11CheckStatus expected) =>
        Assert.Equal(expected, Win11Requirements.EvaluateRam(kb).Status);

    [Theory]
    // «64 ГБ» на коробке — около 64,02 млрд байт.
    [InlineData(64_023_183_360UL, Win11CheckStatus.Pass)]
    [InlineData(500_107_862_016UL, Win11CheckStatus.Pass)]
    [InlineData(32_017_047_552UL, Win11CheckStatus.Fail)]
    public void Диск(ulong bytes, Win11CheckStatus expected) =>
        Assert.Equal(expected, Win11Requirements.EvaluateDisk(bytes).Status);

    [Fact]
    public void Диск_НетДанных() =>
        Assert.Equal(Win11CheckStatus.Unknown, Win11Requirements.EvaluateDisk(null).Status);

    [Fact]
    public void ParseDxDiagXml_ДваАдаптера()
    {
        const string xml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <DxDiag>
          <SystemInformation><DirectXVersion>DirectX 12</DirectXVersion></SystemInformation>
          <DisplayDevices>
            <DisplayDevice><CardName>NVIDIA GeForce RTX 5070</CardName><DriverModel>WDDM 3.2</DriverModel><DDIVersion>12</DDIVersion></DisplayDevice>
            <DisplayDevice><CardName>Microsoft Basic Display Adapter</CardName><DriverModel>Неизвестно</DriverModel><DDIVersion>Неизвестно</DDIVersion></DisplayDevice>
          </DisplayDevices>
        </DxDiag>
        """;
        var adapters = Win11Requirements.ParseDxDiagXml(xml)!;
        Assert.Equal(2, adapters.Count);
        Assert.Equal("NVIDIA GeForce RTX 5070", adapters[0].Name);
        Assert.Equal(new Version(3, 2), adapters[0].Wddm);
        Assert.Equal(12, adapters[0].DdiMajor);
        Assert.Null(adapters[1].Wddm);
        Assert.Null(adapters[1].DdiMajor);
    }

    [Fact]
    public void ParseDxDiagXml_БитыйXml_Null() =>
        Assert.Null(Win11Requirements.ParseDxDiagXml("<DxDiag><DisplayDevices>"));

    [Fact]
    public void DirectX_ХотяБыОдинПодходящий_Pass()
    {
        var adapters = new[]
        {
            new DisplayAdapterInfo { Name = "Old", Wddm = new Version(1, 3), DdiMajor = 11 },
            new DisplayAdapterInfo { Name = "New", Wddm = new Version(2, 7), DdiMajor = 12 }
        };
        Assert.Equal(Win11CheckStatus.Pass, Win11Requirements.EvaluateDirectX(adapters).Status);
    }

    [Fact]
    public void DirectX_ИзвестныйСтарый_Fail()
    {
        var adapters = new[] { new DisplayAdapterInfo { Name = "Old", Wddm = new Version(1, 3), DdiMajor = 11 } };
        Assert.Equal(Win11CheckStatus.Fail, Win11Requirements.EvaluateDirectX(adapters).Status);
    }

    [Fact]
    public void DirectX_БезДрайвераИлиБезОтчёта_Unknown()
    {
        Assert.Equal(Win11CheckStatus.Unknown, Win11Requirements.EvaluateDirectX(null).Status);
        Assert.Equal(Win11CheckStatus.Unknown, Win11Requirements.EvaluateDirectX(
            new[] { new DisplayAdapterInfo { Name = "Microsoft Basic Display Adapter" } }).Status);
    }

    [Fact]
    public void Evaluate_ДевятьПунктов()
    {
        var checks = Win11Requirements.Evaluate(new Win11RawFacts(), Win11CpuMatcher.Embedded.Match(null));
        Assert.Equal(9, checks.Count);
        Assert.Equal(9, checks.Select(c => c.Id).Distinct().Count());
        // Ничего не узнали — ни одного «✓», ни одного ложного «всё хорошо».
        Assert.DoesNotContain(checks, c => c.Status == Win11CheckStatus.Pass);
    }

    private static Win11CheckResult Check(Win11CheckStatus status) =>
        new() { Id = Guid.NewGuid().ToString(), Title = "t", Status = status, Detail = "d" };

    [Fact]
    public void Verdict_ЕстьFail_NotReady()
    {
        var v = Win11Requirements.ComputeVerdict(new[] { Check(Win11CheckStatus.Pass), Check(Win11CheckStatus.Fail), Check(Win11CheckStatus.Unknown) });
        Assert.Equal(Win11VerdictKind.NotReady, v.Kind);
        Assert.Equal(1, v.FailCount);
        Assert.Equal(1, v.UnknownCount);
    }

    [Fact]
    public void Verdict_БезFailСНетДанных_ReadyWithUnknowns()
    {
        var v = Win11Requirements.ComputeVerdict(new[] { Check(Win11CheckStatus.Pass), Check(Win11CheckStatus.Unknown) });
        Assert.Equal(Win11VerdictKind.ReadyWithUnknowns, v.Kind);
    }

    [Fact]
    public void Verdict_ПредупреждениеНеМешает_Ready()
    {
        var v = Win11Requirements.ComputeVerdict(new[] { Check(Win11CheckStatus.Pass), Check(Win11CheckStatus.Warning) });
        Assert.Equal(Win11VerdictKind.Ready, v.Kind);
        Assert.Equal(1, v.WarningCount);
    }

    [Fact]
    public void VerdictText_ЦветИЗамечания()
    {
        var notReady = DiagnosticsViewModel.VerdictText(
            new Win11Verdict { Kind = Win11VerdictKind.NotReady, FailCount = 2, WarningCount = 0, UnknownCount = 0 }, false);
        Assert.Equal("StatusDanger", notReady.BrushKey);
        Assert.Contains("2", notReady.Text);

        var ready = DiagnosticsViewModel.VerdictText(
            new Win11Verdict { Kind = Win11VerdictKind.Ready, FailCount = 0, WarningCount = 1, UnknownCount = 0 }, true);
        Assert.Equal("StatusSuccess", ready.BrushKey);
        Assert.Contains("Замечаний: 1", ready.Text);
    }
}

public sealed class WindowsVersionInfoTests
{
    [Theory]
    [InlineData("Windows 10 Pro", 26200, "Windows 11 Pro")]
    [InlineData("Windows 10 Home", 19045, "Windows 10 Home")]
    [InlineData("", 19045, "Windows")]
    public void FixProductName(string name, int build, string expected) =>
        Assert.Equal(expected, WindowsVersionInfo.FixProductName(name, build));

    [Theory]
    [InlineData("2026-10 Cumulative Update for Windows 11, version 25H2 for x64-based Systems (KB5070000)", true)]
    [InlineData("2026-10 Накопительное обновление для Windows 10 Version 22H2 для систем на базе процессоров x64 (KB5070001)", true)]
    [InlineData("2026-09 Cumulative Update Preview for Windows 11 Version 24H2 for x64-based Systems (KB5069999)", true)]
    [InlineData("2026-10 Cumulative Update for .NET Framework 3.5 and 4.8.1 for Windows 11, version 25H2 (KB5070002)", false)]
    [InlineData("Security Intelligence Update for Microsoft Defender Antivirus - KB2267602", false)]
    [InlineData(null, false)]
    public void IsWindowsCumulativeUpdateTitle(string? title, bool expected) =>
        Assert.Equal(expected, WindowsVersionInfo.IsWindowsCumulativeUpdateTitle(title));

    private static WindowsVersionInfo Info(int build, DateTime? lcu = null) => new()
    {
        ProductName = "Windows", DisplayVersion = "", Build = build, Ubr = 0, LastCumulativeUpdate = lcu
    };

    [Fact]
    public void BuildNotes_Windows10_22H2_ПроEsuИУчётнуюЗаписьБезОбещаний()
    {
        var notes = WindowsVersionInfo.BuildNotes(Info(19045, new DateTime(2026, 10, 14)), new DateTime(2026, 10, 21));
        string all = string.Join(" ", notes);
        Assert.Contains("12 октября 2027", all);
        Assert.Contains("учётная запись Microsoft", all);
        Assert.Contains("не проверяли", all);
        Assert.DoesNotContain("больше двух месяцев", all);
    }

    [Fact]
    public void BuildNotes_СтарыйWindows10_СоветОбновитьсяДо22H2()
    {
        var notes = WindowsVersionInfo.BuildNotes(Info(19044), new DateTime(2026, 10, 21));
        Assert.Contains(notes, n => n.Contains("22H2"));
    }

    [Fact]
    public void BuildNotes_Windows11_БезEsu_НоПроДавниеОбновления()
    {
        var notes = WindowsVersionInfo.BuildNotes(Info(26200, new DateTime(2026, 6, 1)), new DateTime(2026, 10, 21));
        Assert.DoesNotContain(notes, n => n.Contains("ESU"));
        Assert.Contains(notes, n => n.Contains("больше двух месяцев"));
    }
}
