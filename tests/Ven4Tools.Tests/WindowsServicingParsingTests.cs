using System.Text;
using Ven4Tools.Services.WindowsUpdate;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Разбор вывода DISM и sfc: кодировка, проценты, состояние компонента, коды ошибок,
/// итог проверки системных файлов. Только чистая логика — dism.exe/sfc.exe не запускаются.
/// </summary>
public sealed class WindowsServicingParsingTests
{
    // ── Кодировка и проценты ─────────────────────────────────────────────────

    [Fact]
    public void Utf16Le_ОпознаётсяБезМетки()
    {
        byte[] bytes = Encoding.Unicode.GetBytes("\r\nVerification 45% complete.");
        Assert.True(ConsoleOutputDecoder.LooksLikeUtf16Le(bytes));
        Assert.Contains("Verification 45%", ConsoleOutputDecoder.Decode(bytes));
    }

    [Fact]
    public void Utf16Le_РусскийТекстSfc_Декодируется()
    {
        byte[] bytes = Encoding.Unicode.GetBytes("Проверка 12% завершена.");
        Assert.True(ConsoleOutputDecoder.LooksLikeUtf16Le(bytes));
        Assert.Equal("Проверка 12% завершена.", ConsoleOutputDecoder.Decode(bytes));
    }

    [Fact]
    public void Oem866_НеПринимаетсяЗаUtf16()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        byte[] bytes = Encoding.GetEncoding(866).GetBytes("Ошибка: 0x800f081f");
        Assert.False(ConsoleOutputDecoder.LooksLikeUtf16Le(bytes));
    }

    [Theory]
    [InlineData("[==========================100.0%==========================]", 100.0)]
    [InlineData("[=====                      10.0%                          ]", 10.0)]
    [InlineData("Verification 45% complete.", 45.0)]
    [InlineData("Проверка 7% завершена.", 7.0)]
    [InlineData("[===   5,5%   ]", 5.5)]
    public void Процент_Разбирается(string line, double expected)
    {
        Assert.True(ConsoleOutputDecoder.TryParsePercent(line, out double percent));
        Assert.Equal(expected, percent, 3);
    }

    [Theory]
    [InlineData("Deployment Image Servicing and Management tool")]
    [InlineData("150%")]
    public void Процент_НетИлиНеправдоподобный(string line)
    {
        Assert.False(ConsoleOutputDecoder.TryParsePercent(line, out _));
    }

    // ── Состояние .NET 3.5 ───────────────────────────────────────────────────

    [Theory]
    [InlineData("State : Enabled", WindowsFeatureState.Enabled)]
    [InlineData("State : Disabled", WindowsFeatureState.Disabled)]
    [InlineData("State : Disabled with Payload Removed", WindowsFeatureState.DisabledPayloadRemoved)]
    [InlineData("State : Enable Pending", WindowsFeatureState.EnablePending)]
    [InlineData("State : Disable Pending", WindowsFeatureState.DisablePending)]
    [InlineData("Error: 87", WindowsFeatureState.Unknown)]
    public void СостояниеКомпонента(string stateLine, WindowsFeatureState expected)
    {
        string output = "Feature Information:\r\n\r\nFeature Name : NetFx3\r\nDisplay Name : .NET Framework 3.5\r\n" +
                        stateLine + "\r\nRestart Required : Possible\r\n";
        Assert.Equal(expected, NetFx3Service.ParseState(output));
    }

    [Fact]
    public void ПодписиСостоянийНеПустые()
    {
        foreach (WindowsFeatureState state in Enum.GetValues<WindowsFeatureState>())
            Assert.False(string.IsNullOrWhiteSpace(WindowsUpdateViewModel.DescribeNetFxState(state)));
    }

    // ── Источник sources\sxs ────────────────────────────────────────────────

    [Theory]
    [InlineData(@"E:\", @"E:\sources\sxs")]
    [InlineData(@"E:", @"E:\sources\sxs")]
    [InlineData(@"E:\sources", @"E:\sources\sxs")]
    [InlineData(@"E:\sources\sxs", @"E:\sources\sxs")]
    [InlineData(@"E:\sources\sxs\", @"E:\sources\sxs")]
    public void ПапкаSxs_Находится(string chosen, string expected)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"E:\sources\sxs" };
        Assert.Equal(expected, NetFx3Service.ResolveSxsFolder(chosen, existing.Contains), ignoreCase: true);
    }

    [Fact]
    public void ПапкаSxs_НетНаНосителе()
    {
        Assert.Null(NetFx3Service.ResolveSxsFolder(@"D:\Фото", _ => false));
        Assert.Null(NetFx3Service.ResolveSxsFolder("", _ => true));
    }

    // ── Коды DISM ────────────────────────────────────────────────────────────

    [Fact]
    public void КодИзВывода_ВажнееКодаВыхода()
    {
        Assert.Equal(unchecked((int)0x800F081F), DismErrorMapper.ExtractErrorCode(-1, "\r\nError: 0x800f081f\r\n"));
        Assert.Equal(87, DismErrorMapper.ExtractErrorCode(1, "Error: 87"));
        Assert.Equal(5, DismErrorMapper.ExtractErrorCode(5, "нет строки ошибки"));
    }

    [Theory]
    [InlineData(0x800F081Fu, true)]
    [InlineData(0x800F0906u, true)]
    [InlineData(0x800F0907u, true)]
    [InlineData(0x800F0950u, true)]
    [InlineData(0x80070005u, false)]
    public void ПредлагатьНоситель(uint code, bool expected)
    {
        Assert.Equal(expected, DismErrorMapper.SuggestsSourceMedia(unchecked((int)code)));
    }

    [Fact]
    public void ОписаниеКода_ПоРусскиСКодом()
    {
        string text = DismErrorMapper.Describe(unchecked((int)0x800F0907));
        Assert.Contains("групповая политика", text);
        Assert.Contains("0x800F0907", text);
        Assert.Contains("0x12345678", DismErrorMapper.Describe(0x12345678));
    }

    [Fact]
    public void ИтогDism_3010_УспехСПерезагрузкой()
    {
        var result = DismOperationResult.From(new ServicingProcessResult { ExitCode = 3010, Output = "The operation completed successfully." });
        Assert.True(result.Success);
        Assert.True(result.RebootRequired);
    }

    [Fact]
    public void ИтогDism_ОшибкаИсточника_ПредлагаетНоситель()
    {
        var result = DismOperationResult.From(new ServicingProcessResult
        {
            ExitCode = unchecked((int)0x800F081F),
            Output = "Error: 0x800f081f\r\n\r\nThe source files could not be found."
        });
        Assert.False(result.Success);
        Assert.True(result.SuggestSourceMedia);
        Assert.Contains("установочную флешку", result.Message);
    }

    [Fact]
    public void ИтогDism_Отмена()
    {
        var result = DismOperationResult.From(new ServicingProcessResult { ExitCode = -1, Cancelled = true });
        Assert.True(result.Cancelled);
        Assert.False(result.Success);
    }

    // ── Итог sfc ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Windows Resource Protection did not find any integrity violations.", SfcVerdict.NoViolations)]
    [InlineData("Защита ресурсов Windows не обнаружила нарушений целостности.", SfcVerdict.NoViolations)]
    [InlineData("Windows Resource Protection found corrupt files and successfully repaired them.", SfcVerdict.Repaired)]
    [InlineData("Программа защиты ресурсов Windows обнаружила поврежденные файлы и успешно их восстановила.", SfcVerdict.Repaired)]
    [InlineData("Windows Resource Protection found corrupt files but was unable to fix some of them.", SfcVerdict.NotAllRepaired)]
    [InlineData("Программа защиты ресурсов Windows обнаружила поврежденные файлы, но не может восстановить некоторые из них.", SfcVerdict.NotAllRepaired)]
    [InlineData("Windows Resource Protection could not perform the requested operation.", SfcVerdict.CouldNotRun)]
    [InlineData("There is a system repair pending which requires reboot to complete.", SfcVerdict.RebootPending)]
    public void ИтогSfc_ПоТексту(string output, SfcVerdict expected)
    {
        Assert.Equal(expected, SfcResultClassifier.Classify("Beginning system scan.\r\n" + output, ""));
    }

    [Fact]
    public void ИтогSfc_ЗапаснойПутьПоCbsLog()
    {
        const string unknownLanguage = "Windows-Ressourcenschutz …";
        Assert.Equal(SfcVerdict.NotAllRepaired, SfcResultClassifier.Classify(unknownLanguage,
            "2026-10-21 10:00:00, Info CSI 00000001 [SR] Cannot repair member file [l:10]\"foo.dll\"\n"));
        Assert.Equal(SfcVerdict.Repaired, SfcResultClassifier.Classify(unknownLanguage,
            "2026-10-21 10:00:00, Info CSI 00000002 [SR] Repairing corrupted file \\??\\C:\\Windows\\foo.dll\n"));
        Assert.Equal(SfcVerdict.NoViolations, SfcResultClassifier.Classify(unknownLanguage,
            "2026-10-21 10:00:00, Info CSI 00000003 [SR] Verify complete\n"));
        Assert.Equal(SfcVerdict.Unknown, SfcResultClassifier.Classify(unknownLanguage, ""));
    }

    [Fact]
    public void СводкаПроверки_ПриПроблемеСодержитПутиЖурналов()
    {
        var dismOk = new DismOperationResult { Success = true };
        string ok = WindowsUpdateViewModel.BuildSystemFileCheckSummary(dismOk, SfcVerdict.NoViolations, "");
        Assert.DoesNotContain("CBS.log", ok);

        string bad = WindowsUpdateViewModel.BuildSystemFileCheckSummary(dismOk, SfcVerdict.NotAllRepaired, "");
        Assert.Contains("CBS.log", bad);
    }
}
