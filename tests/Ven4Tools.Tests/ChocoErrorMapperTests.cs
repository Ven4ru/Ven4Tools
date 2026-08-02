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
    public void MapExitCode_СинтетическийКодОтсутствияЗапуска_НеУтверждаетКонкретнуюПричину()
    {
        // -1 покрывает несколько разных причин (невалидный ID, choco не найден,
        // таймаут, исключение) — единого текста для них нет, поэтому таблица не
        // должна утверждать что-то конкретное вроде "таймаут" (реальный вызывающий
        // код — InstallFromChocoAsync — вообще перехватывает -1 до вызова этого
        // метода; здесь фиксируем честный фолбэк на случай прямого вызова).
        var message = ChocoErrorMapper.MapExitCode(-1);
        Assert.Contains("-1", message);
        Assert.DoesNotContain("таймаут", message);
    }

    [Fact]
    public void MapExitCode_НеизвестныйКод_СодержитСамКод()
    {
        var message = ChocoErrorMapper.MapExitCode(4242);
        Assert.Contains("4242", message);
    }
}
