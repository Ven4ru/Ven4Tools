using Ven4Tools.Services;
using Xunit;

namespace Ven4Tools.Tests
{
    /// <summary>
    /// Список адаптеров на вкладке «Сеть»: служебные фильтры драйвера (NDIS lightweight
    /// filters) не показываются как отдельные «активные» адаптеры.
    /// </summary>
    public class DiagnosticsAdapterFilterTests
    {
        [Theory]
        [InlineData("Ethernet-WFP Native MAC Layer LightWeight Filter-0000", "WFP Native MAC Layer LightWeight Filter")]
        [InlineData("Ethernet-QoS Packet Scheduler-0000", "QoS Packet Scheduler")]
        [InlineData("Ethernet-WFP 802.3 MAC Layer LightWeight Filter-0000", "WFP 802.3 MAC Layer LightWeight Filter")]
        [InlineData("Wi-Fi-Virtual WiFi Filter Driver-0000", "Virtual WiFi Filter Driver")]
        public void ФильтрБезАдреса_Скрывается(string name, string description)
        {
            Assert.True(DiagnosticsService.IsDriverFilterInterface(name, description, hasIpAddress: false));
        }

        [Theory]
        [InlineData("Ethernet", "Intel(R) Ethernet Connection I219-V")]
        [InlineData("Wi-Fi", "Intel(R) Wi-Fi 6 AX201 160MHz")]
        [InlineData("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter")]
        public void НастоящийАдаптерБезАдреса_Остаётся(string name, string description)
        {
            Assert.False(DiagnosticsService.IsDriverFilterInterface(name, description, hasIpAddress: false));
        }

        [Fact]
        public void ИнтерфейсСАдресом_НеСкрываетсяДажеСПризнакомФильтра()
        {
            Assert.False(DiagnosticsService.IsDriverFilterInterface(
                "Ethernet-QoS Packet Scheduler-0000", "QoS Packet Scheduler", hasIpAddress: true));
        }

        [Fact]
        public void ПустыеИмяИОписание_НеСчитаютсяФильтром()
        {
            Assert.False(DiagnosticsService.IsDriverFilterInterface("", null, hasIpAddress: false));
        }
    }
}
