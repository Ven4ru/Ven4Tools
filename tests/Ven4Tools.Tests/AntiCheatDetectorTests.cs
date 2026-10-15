using Ven4Tools.Services;
using Xunit;

namespace Ven4Tools.Tests
{
    /// <summary>Определение запущенной игры с античитом по именам процессов.</summary>
    public class AntiCheatDetectorTests
    {
        [Theory]
        [InlineData("VALORANT-Win64-Shipping", AntiCheat.Vanguard)]
        [InlineData("League of Legends", AntiCheat.Vanguard)]
        [InlineData("EasyAntiCheat_EOS", AntiCheat.EasyAntiCheat)]
        [InlineData("easyanticheat", AntiCheat.EasyAntiCheat)]
        [InlineData("BEService", AntiCheat.BattlEye)]
        [InlineData("FACEIT", AntiCheat.Faceit)]
        [InlineData("GameMon64", AntiCheat.GameGuard)]
        public void ЗащищённаяИгра_Определяется(string process, AntiCheat expected)
        {
            Assert.Equal(expected, AntiCheatDetector.Match(new[] { "explorer", "chrome", process }));
        }

        [Fact]
        public void ОбычныеПрограммы_НеИгра()
        {
            Assert.Null(AntiCheatDetector.Match(new[] { "explorer", "chrome", "Discord", "steam", "vgc", "vgtray" }));
        }
    }
}
