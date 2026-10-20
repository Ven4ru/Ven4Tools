using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Карточка одного физического диска в блоке «Здоровье дисков». Цвет вердикта
    /// задаётся в разметке по <see cref="VerdictLevel"/> через DynamicResource — так он
    /// сам переживает смену темы и не требует ручного обновления кистей.
    /// </summary>
    public sealed class DiskHealthCard
    {
        public required string Title { get; init; }
        public required string VerdictText { get; init; }
        /// <summary>Ok / Watch / Backup / NoData — для триггеров цвета в разметке.</summary>
        public required string VerdictLevel { get; init; }
        /// <summary>Причины вердикта, каждая с новой строки.</summary>
        public required string ReasonsText { get; init; }
        public required IReadOnlyList<SensorLine> Lines { get; init; }
    }

    /// <summary>
    /// Блоки «Здоровье дисков», «Батарея ноутбука» и «Проверка оперативной памяти».
    /// Только чтение; опрос — при открытии раздела и по кнопке, постоянно не крутится.
    /// </summary>
    public sealed partial class DiagnosticsViewModel
    {
        private IReadOnlyList<DiskHealthReading> _lastDiskReadings = Array.Empty<DiskHealthReading>();
        private bool _diskHealthChecked;

        private IReadOnlyList<DiskHealthCard> _diskHealthCards = Array.Empty<DiskHealthCard>();
        public IReadOnlyList<DiskHealthCard> DiskHealthCards { get => _diskHealthCards; private set => SetField(ref _diskHealthCards, value); }

        private string _diskHealthStatusText = "Данные дисков читаются при открытии раздела";
        public string DiskHealthStatusText { get => _diskHealthStatusText; private set => SetField(ref _diskHealthStatusText, value); }

        private bool _isReadingDiskHealth;
        public bool IsReadingDiskHealth
        {
            get => _isReadingDiskHealth;
            private set { if (SetField(ref _isReadingDiskHealth, value)) RefreshDiskHealthCommand.RaiseCanExecuteChanged(); }
        }

        public RelayCommand RefreshDiskHealthCommand { get; private set; } = null!;

        private void InitHardwareHealthCommands()
        {
            RefreshDiskHealthCommand = RelayCommand.FromAsync(_ => RefreshDiskHealthAsync(), _ => !IsReadingDiskHealth);
            InitMemoryTestCommands();
        }

        /// <summary>Раздел открыт: один раз прочитать диски, батарею и итог теста памяти.</summary>
        public void StartHardwareHealth()
        {
            _ = RefreshDiskHealthAsync();
            _ = RefreshBatteryAsync();
            _ = RefreshMemoryTestResultAsync();
        }

        private Task? _diskHealthTask;

        /// <summary>Чтение уже идёт — вернуть его же: и кнопка, и общий запуск диагностики дождутся одного опроса.</summary>
        private Task RefreshDiskHealthAsync()
        {
            if (_diskHealthTask is { IsCompleted: false }) return _diskHealthTask;
            return _diskHealthTask = ReadDiskHealthAsync();
        }

        private async Task ReadDiskHealthAsync()
        {
            IsReadingDiskHealth = true;
            DiskHealthStatusText = "Читаю данные SMART дисков…";
            try
            {
                var readings = await DiskHealthService.ReadAsync();
                _lastDiskReadings = readings;
                _diskHealthChecked = true;
                DiskHealthCards = readings.Select(ToCard).ToList();
                DiskHealthStatusText = readings.Count == 0
                    ? "Диски не найдены"
                    : $"Проверено: {DateTime.Now:t}. Данные — из SMART самого диска; где диск их не отдаёт, — из счётчиков Windows.";
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "DiagnosticsViewModel.RefreshDiskHealth");
                DiskHealthStatusText = "Не удалось прочитать данные дисков — подробности в логе";
            }
            finally { IsReadingDiskHealth = false; }
        }

        /// <summary>
        /// «Запустить диагностику»: заодно перечитать здоровье дисков и учесть вердикты в
        /// общем бейдже. «Нет данных» бейдж не портит — это не находка, а её отсутствие.
        /// </summary>
        private async Task RefreshDiskHealthForBadgeAsync()
        {
            await RefreshDiskHealthAsync();
            foreach (var d in _lastDiskReadings)
            {
                var level = DiskHealthVerdict.Evaluate(d).Level;
                if (level == DiskVerdictLevel.Backup) _lastRunHadCritical = true;
                else if (level == DiskVerdictLevel.Watch) _lastRunHadWarning = true;
            }
        }

        private static DiskHealthCard ToCard(DiskHealthReading d)
        {
            var verdict = DiskHealthVerdict.Evaluate(d);
            return new DiskHealthCard
            {
                Title = DiskHealthFormat.Title(d),
                VerdictText = $"{verdict.Icon} {verdict.Label}",
                VerdictLevel = verdict.Level.ToString(),
                ReasonsText = string.Join("\n", verdict.Reasons),
                Lines = DiskHealthFormat.Lines(d).Select(l => new SensorLine(l.Label, l.Value)).ToList()
            };
        }

        /// <summary>Секции «Здоровье дисков», «Батарея», «Проверка памяти» для полного отчёта.</summary>
        private string HardwareHealthReportText()
        {
            var sb = new StringBuilder();
            // Пустая строка-разделитель после предыдущей секции отчёта.
            sb.AppendLine();
            sb.AppendLine("--- Здоровье дисков ---");
            if (!_diskHealthChecked)
                sb.AppendLine(Tr("Не проверялось: откройте раздел «Диагностика» или нажмите «Проверить диски»."));
            else if (_lastDiskReadings.Count == 0)
                sb.AppendLine(Tr("Диски не найдены"));
            foreach (var d in _lastDiskReadings)
            {
                var verdict = DiskHealthVerdict.Evaluate(d);
                sb.AppendLine($"{Tr(DiskHealthFormat.Title(d))} — {Tr(verdict.Label)}");
                foreach (var reason in verdict.Reasons) sb.AppendLine("  • " + Tr(reason));
                foreach (var (label, value) in DiskHealthFormat.Lines(d))
                    sb.AppendLine($"  {Tr(label)}: {Tr(value)}");
            }
            sb.AppendLine();
            sb.AppendLine("--- Батарея ---");
            sb.AppendLine(Tr(BatteryReportLine()));
            sb.AppendLine();
            sb.AppendLine("--- Проверка оперативной памяти ---");
            sb.AppendLine(Tr(MemoryTestStatusText));
            sb.AppendLine();
            return sb.ToString();
        }
    }
}
