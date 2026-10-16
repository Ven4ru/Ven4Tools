using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class CommandLineGuardLocalIdTests
{
    [Theory]
    [InlineData(@"MSIX\AdobeAcrobatReaderCoreApp_26.0.0.1_x64__33qsgwvbcw8mw")]
    [InlineData(@"ARP\User\X64\Lesta Game Center")]
    [InlineData(@"ARP\Machine\X64\{AC76BA86-1033-FF00-7760-BC15014EA700}")]
    public void ValidateLocalPackageId_ЛокальныеIdWinget_Допустимы(string id)
    {
        Assert.True(CommandLineGuard.ValidateLocalPackageId(id));
    }

    [Theory]
    [InlineData("Google.Chrome")]                       // обычный ID — для него ValidateId
    [InlineData(@"MSIX\Foo"" --force")]                 // кавычка
    [InlineData(@"ARP\User\X64\Foo & calc")]           // амперсанд
    [InlineData(@"C:\Windows\System32\cmd.exe")]       // не локальный ID winget
    [InlineData("")]
    public void ValidateLocalPackageId_Постороннее_Отклоняется(string id)
    {
        Assert.False(CommandLineGuard.ValidateLocalPackageId(id));
    }
}
