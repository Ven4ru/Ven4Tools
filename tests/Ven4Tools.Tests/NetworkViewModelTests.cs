using System.Windows.Media;
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    public class NetworkViewModelTests
    {
        [Fact]
        public void SetRow_OkNull_УстанавливаетНейтральнуюИконку()
        {
            var row = new NetworkCheckResult();

            NetworkViewModel.SetRow(row, "отключено", null);

            Assert.Equal("отключено", row.Text);
            Assert.Equal("⬜", row.IconText);
            Assert.Same(Brushes.Gray, row.IconBrush);
        }

        [Fact]
        public void SetRow_OkTrue_УстанавливаетЗелёнуюИконку()
        {
            var row = new NetworkCheckResult();

            NetworkViewModel.SetRow(row, "12 мс", true);

            Assert.Equal("12 мс", row.Text);
            Assert.Equal("✅", row.IconText);
            Assert.Equal(Color.FromRgb(74, 222, 128), ((SolidColorBrush)row.IconBrush).Color);
        }

        [Fact]
        public void SetRow_OkFalse_УстанавливаетКраснуюИконку()
        {
            var row = new NetworkCheckResult();

            NetworkViewModel.SetRow(row, "недоступен", false);

            Assert.Equal("недоступен", row.Text);
            Assert.Equal("❌", row.IconText);
            Assert.Equal(Colors.LightCoral, ((SolidColorBrush)row.IconBrush).Color);
        }

        [Fact]
        public void NetworkCheckResult_ДефолтнаяКисть_БезApplication_ПадаетВБелыйФолбэк()
        {
            Assert.Null(System.Windows.Application.Current);

            var row = new NetworkCheckResult();

            Assert.Same(Brushes.White, row.IconBrush);
        }

        [Fact]
        public void NetworkCheckResult_Дефолты_СовпадаютСОригиналомXaml()
        {
            var row = new NetworkCheckResult();

            Assert.Equal("—", row.Text);
            Assert.Equal("⬜", row.IconText);
        }

        [Fact]
        public void Конструктор_УстанавливаетДефолтныеЗначения()
        {
            var vm = new NetworkViewModel();

            Assert.Equal("🔍 Запустить полную диагностику", vm.RunAllButtonText);
            Assert.Equal("не определён", vm.PublicIpText);
            Assert.Equal("", vm.DnsResultText);
            Assert.False(vm.DnsResultVisible);
            Assert.False(vm.AdaptersEmpty);
            Assert.Empty(vm.Adapters);
            Assert.False(vm.IsBusy);
            Assert.False(vm.IsPinging);
            Assert.False(vm.IsCheckingServices);
            Assert.False(vm.IsGettingIp);
            Assert.False(vm.IsCheckingDns);
            Assert.False(vm.IsResettingNetwork);
        }

        [Fact]
        public void ВсеКоманды_ИзначальноCanExecute()
        {
            var vm = new NetworkViewModel();

            Assert.True(vm.RunAllCommand.CanExecute(null));
            Assert.True(vm.RefreshAdaptersCommand.CanExecute(null));
            Assert.True(vm.PingCommand.CanExecute(null));
            Assert.True(vm.CheckServicesCommand.CanExecute(null));
            Assert.True(vm.GetIpCommand.CanExecute(null));
            Assert.True(vm.CheckDnsCommand.CanExecute(null));
            Assert.True(vm.ResetNetworkCommand.CanExecute(null));
        }

        [Fact]
        public void PingRows_СозданыКакНезависимыеЭкземпляры()
        {
            var vm = new NetworkViewModel();

            Assert.NotSame(vm.Ping1, vm.Ping2);
            Assert.NotSame(vm.Ping3, vm.Ping4);
            Assert.NotSame(vm.Svc1, vm.Svc5);
        }
    }
}
