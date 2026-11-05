using System.Collections.Generic;
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    /// <summary>
    /// Плашка различия у одинаковых названий в «Установленных»: архитектура из локального
    /// ID winget, иначе область установки; у строк без дубля плашки нет.
    /// </summary>
    public class InstalledVariantTagTests
    {
        private static InstalledApp App(string name, string id) => new() { Name = name, WingetId = id };

        [Theory]
        [InlineData(@"ARP\Machine\X64\{AC76BA86-1033-FF00-7760-BC15014EA700}", "x64")]
        [InlineData(@"ARP\User\X86\Lesta Game Center", "x86")]
        [InlineData(@"MSIX\Microsoft.NET.Native.Framework.2.2_2.2.29512.0_x64__8wekyb3d8bbwe", "x64")]
        [InlineData(@"MSIX\Microsoft.NET.Native.Framework.2.2_2.2.29512.0_arm64__8wekyb3d8bbwe", "ARM64")]
        [InlineData(@"MSIX\Microsoft.UI.Xaml.2.8_8.2310.30001.0_neutral__8wekyb3d8bbwe", null)]
        [InlineData(@"MSIX\Microsoft.NET.Native.Framework.2.2_2.2.2951…", null)]
        [InlineData("Microsoft.Edge", null)]
        public void Архитектура_ИзЛокальногоId(string id, string? expected)
        {
            Assert.Equal(expected, InstalledApp.ParseArchitecture(id));
        }

        [Fact]
        public void Дубли_РазнойАрхитектуры_ПолучаютАрхитектуру()
        {
            var apps = new List<InstalledApp>
            {
                App(".NET Native", @"MSIX\Microsoft.NET.Native.Framework.2.2_2.2.29512.0_x64__8wekyb3d8bbwe"),
                App(".NET Native", @"MSIX\Microsoft.NET.Native.Framework.2.2_2.2.29512.0_x86__8wekyb3d8bbwe"),
                App("Calculator", @"MSIX\Microsoft.WindowsCalculator_11.2405_x64__8wekyb3d8bbwe"),
            };

            InstalledViewModel.MarkVariants(apps);

            Assert.Equal("x64", apps[0].VariantTag);
            Assert.Equal("x86", apps[1].VariantTag);
            Assert.False(apps[2].HasVariantTag);
        }

        [Fact]
        public void Дубли_ОднойАрхитектуры_ПолучаютОбласть()
        {
            var apps = new List<InstalledApp>
            {
                App("Microsoft Edge", @"ARP\Machine\X64\Microsoft Edge"),
                App("Microsoft Edge", @"ARP\User\X64\Microsoft Edge"),
            };

            InstalledViewModel.MarkVariants(apps);

            Assert.Equal("для всех", apps[0].VariantTag);
            Assert.Equal("для пользователя", apps[1].VariantTag);
        }

        [Fact]
        public void Дубли_БезДанных_БезПлашки()
        {
            var apps = new List<InstalledApp>
            {
                App("Microsoft Edge", "Microsoft.Edge"),
                App("Microsoft Edge", "Microsoft.Edge"),
            };

            InstalledViewModel.MarkVariants(apps);

            Assert.False(apps[0].HasVariantTag);
            Assert.False(apps[1].HasVariantTag);
        }
    }
}
