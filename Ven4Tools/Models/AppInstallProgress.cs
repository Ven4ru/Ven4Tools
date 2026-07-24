using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ven4Tools.Models
{
    // INotifyPropertyChanged обязателен: CatalogViewModel переиспользует один и
    // тот же экземпляр на все последующие Progress<AppInstallProgress>-события
    // одного приложения (мутирует Status/Percentage на месте, не пересоздаёт
    // объект) — без уведомления WPF-биндинг ProgressBar.Value="{Binding
    // Percentage}" в CatalogTab.xaml обновляется только на первом событии
    // (когда элемент добавляется в ObservableCollection) и застревает дальше,
    // хотя установка по факту продолжается и завершается. В императивном
    // коде до MVVM-переноса это компенсировалось явным Items.Refresh() —
    // при переносе на биндинг эквивалент потерялся.
    public class AppInstallProgress : INotifyPropertyChanged
    {
        public string AppId { get; set; } = string.Empty;
        public string AppName { get; set; } = string.Empty;

        private string _status = string.Empty;
        public string Status
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        private int _percentage;
        public int Percentage
        {
            get => _percentage;
            set => SetField(ref _percentage, value);
        }

        private bool _isIndeterminate;
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set => SetField(ref _isIndeterminate, value);
        }

        private InstallPhase _phase = InstallPhase.Download;
        /// <summary>
        /// Текущая фаза установки. Percentage считается заново в каждой фазе
        /// (полные 0-100% на скачивание, отдельно 0-100% на установку) — раньше
        /// обе фазы были замешаны в одну шкалу 0-100, из-за чего полоска прогресса
        /// была нечитаемой (пользовательский фидбек 2026-07-24). UI (CatalogTab)
        /// красит полоску по этому свойству через InstallPhaseToBrushConverter.
        /// </summary>
        public InstallPhase Phase
        {
            get => _phase;
            set => SetField(ref _phase, value);
        }

        private InstallOutcome _outcome = InstallOutcome.NotYetDetermined;
        /// <summary>
        /// Результат сверки кода выхода установщика с фактическим состоянием системы
        /// (см. <see cref="InstallOutcomeEvaluator"/>). Остаётся <see cref="InstallOutcome.NotYetDetermined"/>
        /// до финального отчёта об установке — все промежуточные статусы («Скачивание»,
        /// «Установка...») его не трогают. UI (CatalogTab) красит текст статуса по этому
        /// свойству отдельно от <see cref="Phase"/> — Phase про этап процесса, Outcome про
        /// то, насколько мы уверены в итоге.
        /// </summary>
        public InstallOutcome Outcome
        {
            get => _outcome;
            set => SetField(ref _outcome, value);
        }

        /// <summary>
        /// Сквозная оценка прогресса (0-100) для агрегированной шкалы по всей
        /// очереди установки (CatalogViewModel.OverallProgressPercentage). Без
        /// неё агрегат «прыгал» бы назад в момент переключения Download →
        /// Installing, когда Percentage сбрасывается на 0 для новой фазы.
        /// Взвешено 50/50 между фазами; для IsIndeterminate (нет гранулярных
        /// данных о ходе фазы) берётся середина её диапазона — честная оценка
        /// «примерно на этом этапе», а не выдуманный точный процент.
        /// </summary>
        public double EffectiveProgress => Phase switch
        {
            InstallPhase.Download => IsIndeterminate ? 25.0 : Percentage * 0.5,
            InstallPhase.Installing => IsIndeterminate ? 75.0 : 50.0 + Percentage * 0.5,
            InstallPhase.Done => 100.0,
            InstallPhase.Error => 100.0,
            _ => Percentage
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }
}
