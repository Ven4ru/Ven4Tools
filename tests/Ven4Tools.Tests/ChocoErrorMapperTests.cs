using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class ChocoErrorMapperTests
{
    [Fact]
    public void MapExitCode_КодWindowsInstaller1618_СообщаетОПараллельнойУстановке()
    {
        var message = ChocoErrorMapper.MapExitCode(1618);
        Assert.Contains("уже выполняется", message);
    }

    [Fact]
    public void MapExitCode_СобственныйКодChocolatey1_СообщаетОбОшибкеПакета()
    {
        var message = ChocoErrorMapper.MapExitCode(1);
        Assert.Contains("Chocolatey", message);
    }

    [Fact]
    public void MapExitCode_КодТаймаута_СообщаетОТаймауте()
    {
        // -1 — синтетический код, которым RunChocoInstallAsync помечает случаи,
        // когда реального кода выхода нет (таймаут, choco не запустился).
        var message = ChocoErrorMapper.MapExitCode(-1);
        Assert.Contains("таймауту", message);
    }

    [Fact]
    public void MapExitCode_НеизвестныйКод_СодержитСамКод()
    {
        var message = ChocoErrorMapper.MapExitCode(4242);
        Assert.Contains("4242", message);
    }
}
