using Ven4Tools.Services;
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    /// <summary>Экспериментальный модуль датчиков и оверлея: отзывы и выключенный драйвер.</summary>
    public class ModuleFeedbackTests
    {
        [Fact]
        public void ОтзывНаGitHub_ШаблонЗаголовокИВерсия()
        {
            string url = DiagnosticsViewModel.ModuleFeedbackGitHubUrl("6.3.0");

            Assert.StartsWith("https://github.com/Ven4ru/Ven4Tools/issues/new?template=module_feedback.yml", url);
            Assert.Contains("&version=6.3.0", url);
            Assert.Contains("&title=%D0%9E%D1%82%D0%B7%D1%8B%D0%B2", url); // «Отзыв…», закодировано
        }

        [Fact]
        public void ОтзывНаСайте_РазделОтзывов()
        {
            Assert.Equal("https://ven4tools.ru/#scene-reviews", DiagnosticsViewModel.ModuleFeedbackSiteUrl);
        }

        [Fact]
        public void ВЭкспериментальнойВерсии_ДрайверНеСтавится()
        {
            Assert.False(PawnIoDriver.FeatureEnabled);
        }
    }
}
