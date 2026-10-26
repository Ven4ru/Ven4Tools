using System;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Блок «Проверка оперативной памяти»: запуск штатного mdsched.exe и итог последнего
    /// теста из журнала System. Перезагрузку предлагает и выполняет сама Windows.
    /// </summary>
    public sealed partial class DiagnosticsViewModel
    {
        private string _memoryTestStatusText = "Итог проверки памяти читается при открытии раздела";
        public string MemoryTestStatusText { get => _memoryTestStatusText; private set => SetField(ref _memoryTestStatusText, value); }

        /// <summary>Последний тест нашёл ошибки — строка итога подсвечивается.</summary>
        private bool _memoryTestHasErrors;
        public bool MemoryTestHasErrors { get => _memoryTestHasErrors; private set => SetField(ref _memoryTestHasErrors, value); }

        private string _memoryTestScheduledText = "";
        public string MemoryTestScheduledText { get => _memoryTestScheduledText; private set => SetField(ref _memoryTestScheduledText, value); }

        public RelayCommand RunMemoryTestCommand { get; private set; } = null!;

        private void InitMemoryTestCommands()
        {
            RunMemoryTestCommand = new RelayCommand(_ => RunMemoryTest());
        }

        private async Task RefreshMemoryTestResultAsync()
        {
            try
            {
                var result = await MemoryDiagnosticsService.ReadLastResultAsync();
                MemoryTestStatusText = MemoryDiagnosticsService.Describe(result);
                MemoryTestHasErrors = result?.ErrorsFound == true;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.RefreshMemoryTestResult");
                MemoryTestStatusText = "Не удалось прочитать журнал Windows — подробности в логе";
            }
        }

        private void RunMemoryTest()
        {
            var answer = MessageBox.Show(
                "Память проверяется при перезагрузке компьютера: проверка идёт до загрузки Windows и занимает от 10 минут до часа и больше — зависит от объёма памяти.\n\n" +
                "Сохраните открытые документы и закройте программы.\n\n" +
                "Сейчас откроется окно Windows «Средство проверки памяти»: в нём можно выбрать «Выполнить перезагрузку и проверку» сейчас или проверку при следующем включении компьютера. " +
                "После проверки откройте этот раздел — здесь появится её итог.\n\n" +
                "Открыть средство проверки памяти?",
                "Проверка оперативной памяти", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            if (MemoryDiagnosticsService.Launch())
            {
                MemoryTestScheduledText = "Окно проверки памяти открыто. Если выбрали проверку при следующем включении — она пройдёт при ближайшей перезагрузке.";
            }
            else
            {
                MessageBox.Show("Не удалось открыть средство проверки памяти Windows (mdsched.exe) — подробности в логе.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
