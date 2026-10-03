using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>Строка блока «Последние действия» на экране «Обзор».</summary>
    public sealed class OverviewHistoryRow
    {
        public string Title { get; init; } = "";
        public string Details { get; init; } = "";
    }

    /// <summary>
    /// Экран «Обзор» новой оболочки: что требует внимания и куда идти дальше.
    /// Сам ничего не проверяет и в сеть не ходит — только показывает то, что уже
    /// знают фоновые службы (обновления программ, Windows Update), загрузчик
    /// каталога и история установок, и подписывается на их изменения.
    /// </summary>
    public sealed class OverviewViewModel : ViewModelBase
    {
        /// <summary>Просьба открыть раздел: ключ тот же, что у главного окна.</summary>
        public event Action<string>? NavigateRequested;

        public ObservableCollection<OverviewHistoryRow> Recent { get; } = new();

        private string _summary = "";
        public string Summary { get => _summary; private set => SetField(ref _summary, value); }

        private string _appUpdatesStatus = "";
        public string AppUpdatesStatus { get => _appUpdatesStatus; private set => SetField(ref _appUpdatesStatus, value); }

        private string _appUpdatesHint = "";
        public string AppUpdatesHint { get => _appUpdatesHint; private set => SetField(ref _appUpdatesHint, value); }

        private bool _appUpdatesNeedAttention;
        public bool AppUpdatesNeedAttention { get => _appUpdatesNeedAttention; private set => SetField(ref _appUpdatesNeedAttention, value); }

        private string _windowsUpdateStatus = "";
        public string WindowsUpdateStatus { get => _windowsUpdateStatus; private set => SetField(ref _windowsUpdateStatus, value); }

        private string _windowsUpdateHint = "";
        public string WindowsUpdateHint { get => _windowsUpdateHint; private set => SetField(ref _windowsUpdateHint, value); }

        private bool _windowsUpdateNeedsAttention;
        public bool WindowsUpdateNeedsAttention { get => _windowsUpdateNeedsAttention; private set => SetField(ref _windowsUpdateNeedsAttention, value); }

        private string _clientVersion = "";
        public string ClientVersion { get => _clientVersion; private set => SetField(ref _clientVersion, value); }

        private string _catalogStatus = "";
        public string CatalogStatus { get => _catalogStatus; private set => SetField(ref _catalogStatus, value); }

        private bool _hasRecent;
        public bool HasRecent { get => _hasRecent; private set => SetField(ref _hasRecent, value); }

        public RelayCommand OpenCatalogCommand { get; }
        public RelayCommand OpenAppUpdatesCommand { get; }
        public RelayCommand OpenWindowsUpdateCommand { get; }
        public RelayCommand OpenDebloaterCommand { get; }
        public RelayCommand OpenDiagnosticsCommand { get; }
        public RelayCommand OpenHistoryCommand { get; }
        public RelayCommand OpenAboutCommand { get; }

        public OverviewViewModel()
        {
            OpenCatalogCommand = new RelayCommand(_ => NavigateRequested?.Invoke("catalog"));
            OpenAppUpdatesCommand = new RelayCommand(_ => NavigateRequested?.Invoke("updates"));
            OpenWindowsUpdateCommand = new RelayCommand(_ => NavigateRequested?.Invoke("windowsupdate"));
            OpenDebloaterCommand = new RelayCommand(_ => NavigateRequested?.Invoke("debloater"));
            OpenDiagnosticsCommand = new RelayCommand(_ => NavigateRequested?.Invoke("diagnostics"));
            OpenHistoryCommand = new RelayCommand(_ => NavigateRequested?.Invoke("history"));
            OpenAboutCommand = new RelayCommand(_ => NavigateRequested?.Invoke("about"));

            ClientVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "—";
        }

        /// <summary>Подписки живут, пока экран показан: главное окно держит его весь сеанс.</summary>
        public void Attach()
        {
            UpdateBackgroundService.CountChanged += OnCountsChanged;
            WindowsUpdateBackgroundService.CountChanged += OnCountsChanged;
            CatalogLoaderService.CatalogReady += OnCatalogReady;
            InstallHistoryService.Instance.Changed += OnHistoryChanged;
            ProfileService.Changed += OnCountsChanged;
            Refresh();
        }

        public void Detach()
        {
            UpdateBackgroundService.CountChanged -= OnCountsChanged;
            WindowsUpdateBackgroundService.CountChanged -= OnCountsChanged;
            CatalogLoaderService.CatalogReady -= OnCatalogReady;
            InstallHistoryService.Instance.Changed -= OnHistoryChanged;
            ProfileService.Changed -= OnCountsChanged;
        }

        private void OnCountsChanged() => RunOnUi(RefreshStatuses);
        private void OnCatalogReady(Models.MasterCatalog _) => RunOnUi(RefreshStatuses);
        private void OnHistoryChanged() => RunOnUi(() => _ = RefreshRecentAsync());

        private static void RunOnUi(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }

        public void Refresh()
        {
            RefreshStatuses();
            _ = RefreshRecentAsync();
        }

        private void RefreshStatuses()
        {
            var profile = ProfileService.Current;
            bool backgroundOff = profile.ParanoidMode || OfflineService.IsOffline;

            (AppUpdatesStatus, AppUpdatesHint, AppUpdatesNeedAttention) =
                DescribeAppUpdates(UpdateBackgroundService.AvailableCount, profile.NotifyAppUpdates && !backgroundOff);

            (WindowsUpdateStatus, WindowsUpdateHint, WindowsUpdateNeedsAttention) =
                DescribeWindowsUpdate(WindowsUpdateBackgroundService.AvailableCount,
                    profile.WindowsUpdateMode != "NotSet" && !backgroundOff);

            var state = CatalogLoaderService.State;
            CatalogStatus = state.UsableCatalog is { } catalog
                ? $"версия {catalog.Version}, программ: {catalog.Apps.Count}"
                : "ещё не загружен";

            Summary = DescribeSummary(AppUpdatesNeedAttention, WindowsUpdateNeedsAttention);
        }

        /// <summary>
        /// Текст карточки обновлений программ. Чистая функция — покрыта тестами.
        /// <paramref name="count"/> = -1 означает «фоновая проверка ещё не отработала».
        /// </summary>
        internal static (string Status, string Hint, bool NeedsAttention) DescribeAppUpdates(int count, bool backgroundEnabled)
        {
            if (count > 0)
                return ($"Можно обновить: {count}", "Список — в «Установленных», с фильтром «есть обновление».", true);
            if (count == 0)
                return ("Все программы актуальны", "По данным последней фоновой проверки.", false);
            return backgroundEnabled
                ? ("Проверка ещё не выполнялась", "Фоновая проверка запускается через несколько минут после старта.", false)
                : ("Фоновая проверка выключена", "Проверить вручную можно в «Установленных».", false);
        }

        /// <summary>Текст карточки Windows Update. Чистая функция — покрыта тестами.</summary>
        internal static (string Status, string Hint, bool NeedsAttention) DescribeWindowsUpdate(int count, bool backgroundEnabled)
        {
            if (count > 0)
                return ($"Найдено патчей: {count}", "Патчи никогда не ставятся без вашего решения.", true);
            return backgroundEnabled
                ? ("Новых патчей не найдено", "Фоновая проверка идёт раз в шесть часов.", false)
                : ("Фоновая проверка выключена", "Проверить вручную можно на вкладке «Windows Update».", false);
        }

        internal static string DescribeSummary(bool appUpdates, bool windowsUpdate)
        {
            if (appUpdates && windowsUpdate) return "Есть обновления программ и патчи Windows.";
            if (appUpdates) return "Есть обновления программ, остальное в порядке.";
            if (windowsUpdate) return "Есть патчи Windows, остальное в порядке.";
            return "Ничего не требует внимания.";
        }

        private async Task RefreshRecentAsync()
        {
            try
            {
                var history = await InstallHistoryService.Instance.GetHistoryAsync();
                var rows = history
                    .OrderByDescending(h => h.InstalledAt)
                    .Take(4)
                    .Select(h => new OverviewHistoryRow
                    {
                        Title = (h.Success ? "Установлено: " : "Не удалось установить: ") + h.AppName,
                        Details = $"{SourceName(h.Source)} · {h.DateLabel}"
                    })
                    .ToList();

                Recent.Clear();
                foreach (var row in rows) Recent.Add(row);
                HasRecent = Recent.Count > 0;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Обзор] История не прочитана: {ex.Message}");
            }
        }

        private static string SourceName(string source) => source switch
        {
            "winget" => "winget",
            "choco" => "Chocolatey",
            "direct" => "прямая ссылка",
            "cache" => "офлайн-кэш",
            "local" => "локальный файл",
            _ => source
        };
    }
}
