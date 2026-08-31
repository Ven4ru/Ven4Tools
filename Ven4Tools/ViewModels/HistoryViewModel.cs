using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Вкладка «История» — поиск, фильтры, флаг сохранения истории, очистка,
    /// переустановка. Перенесено из code-behind при MVVM-миграции клиента
    /// (2026-08-24, вторая вкладка после пилота DebloaterTab). Поведение не
    /// менялось, кроме <see cref="IsReinstalling"/> — см. спек
    /// docs/superpowers/specs/2026-08-24-historytab-mvvm-design.md.
    /// </summary>
    public sealed class HistoryViewModel : ViewModelBase
    {
        private List<HistoryEntry> _allEntries = new();

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { if (SetField(ref _searchText, value)) ApplyFilter(); }
        }

        private bool _successOnly;
        public bool SuccessOnly
        {
            get => _successOnly;
            set { if (SetField(ref _successOnly, value)) ApplyFilter(); }
        }

        private bool _failOnly;
        public bool FailOnly
        {
            get => _failOnly;
            set { if (SetField(ref _failOnly, value)) ApplyFilter(); }
        }

        private List<HistoryEntry> _filteredEntries = new();
        public List<HistoryEntry> FilteredEntries
        {
            get => _filteredEntries;
            private set => SetField(ref _filteredEntries, value);
        }

        public string HistoryCount => FilteredEntries.Count.ToString();

        /// <summary>
        /// Флаг приватности: отключить запись истории можно было раньше только
        /// правкой profile.json руками. Управление вынесено туда, где история и
        /// показывается (не новая логика — перенос из HistoryTab.xaml.cs).
        /// </summary>
        public bool SaveHistory
        {
            get => ProfileService.Current.SaveInstallHistory;
            set
            {
                if (ProfileService.Current.SaveInstallHistory == value) return;
                ProfileService.Current.SaveInstallHistory = value;
                ProfileService.Save();
                AppLogger.Write(value
                    ? "[История] Запись истории установок включена"
                    : "[История] Запись истории установок отключена");
                OnPropertyChanged();
            }
        }

        // Согласованное отступление от 1:1 (см. спек): раньше блокировалась только
        // нажатая кнопка «Переустановить», теперь — все, пока идёт одна операция.
        // InstallSemaphore и так сериализует установки — это чуть строже старого
        // поведения, а не слабее.
        private bool _isReinstalling;
        public bool IsReinstalling
        {
            get => _isReinstalling;
            private set { if (SetField(ref _isReinstalling, value)) ReinstallCommand.RaiseCanExecuteChanged(); }
        }

        public RelayCommand ClearHistoryCommand { get; }
        public RelayCommand ReinstallCommand { get; }

        public HistoryViewModel()
        {
            ClearHistoryCommand = RelayCommand.FromAsync(async _ => await ClearHistoryAsync());
            ReinstallCommand = RelayCommand.FromAsync(
                async param => await ReinstallAsync(param as HistoryEntry),
                _ => !IsReinstalling);
        }

        public async Task RefreshAsync()
        {
            _allEntries = await InstallHistoryService.Instance.GetHistoryAsync();
            ApplyFilter();
        }

        /// <summary>
        /// Чистая функция фильтрации, вынесенная из <see cref="ApplyFilter"/>.
        /// Смысл выноса — дать юнит-тестам шов для проверки самого предиката на
        /// реальных записях: набор истории попадает во ViewModel только через
        /// InstallHistoryService, и без этого метода тесты фильтра проверяли лишь
        /// отсутствие исключения на пустом списке. Семантика не менялась: поиск по
        /// подстроке в названии ИЛИ категории без учёта регистра, а два
        /// одновременно включённых переключателя означают «показать всё».
        /// </summary>
        internal static List<HistoryEntry> Filter(IEnumerable<HistoryEntry> source,
            string query, bool successOnly, bool failOnly)
        {
            string q = query.Trim();
            var filtered = source;

            if (!string.IsNullOrEmpty(q))
                filtered = filtered.Where(e => e.AppName.Contains(q, StringComparison.OrdinalIgnoreCase)
                                            || e.Category.Contains(q, StringComparison.OrdinalIgnoreCase));

            if (successOnly && !failOnly) filtered = filtered.Where(e => e.Success);
            if (failOnly && !successOnly) filtered = filtered.Where(e => !e.Success);

            return filtered.ToList();
        }

        private void ApplyFilter()
        {
            FilteredEntries = Filter(_allEntries, SearchText, SuccessOnly, FailOnly);
            OnPropertyChanged(nameof(HistoryCount));
        }

        private async Task ClearHistoryAsync()
        {
            var r = MessageBox.Show("Очистить всю историю установок?",
                "Очистка", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
            await InstallHistoryService.Instance.ClearAsync();
            await RefreshAsync();
        }

        private async Task ReinstallAsync(HistoryEntry? entry)
        {
            if (entry == null) return;

            // Проверяем занятость общего семафора установки ДО любых UI-мутаций —
            // семафор общий с каталогом и Windows Update.
            if (Views.UiGuards.WarnIfInstallBusy()) return;

            var catalog = CatalogLoaderService.State.Catalog;
            var catalogApp = catalog?.Apps.FirstOrDefault(a => a.Id == entry.AppId);
            if (catalogApp == null)
            {
                MessageBox.Show($"Приложение «{entry.AppName}» не найдено в текущем каталоге.",
                    "Не найдено", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var appInfo = new AppInfo
            {
                Id            = catalogApp.Id,
                DisplayName   = catalogApp.Name,
                Category      = AppCategory.Другое,
                AlternativeId = catalogApp.WingetId,
                InstallerUrls = !string.IsNullOrEmpty(catalogApp.DownloadUrl)
                    ? new List<string> { catalogApp.DownloadUrl }
                    : new(),
                ChocoId = catalogApp.ChocoId,
                // SHA256 обязателен для установки по прямой ссылке при переустановке.
                Sha256 = catalogApp.Sha256
            };
            // Переопределение тихого флага (напр. AutoHotkey v2: "/silent" вместо "/S") —
            // без этого переустановка теряет override и падает на дефолтном "/S".
            if (!string.IsNullOrEmpty(catalogApp.SilentArgs))
                appInfo.SilentArgs = catalogApp.SilentArgs;

            AppLogger.Write($"🔄 Переустановка: {entry.AppName}...");

            var progress = new Progress<AppInstallProgress>(p =>
                AppLogger.Write($"  {p.Status}"));

            IsReinstalling = true;
            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                using var installer = new InstallationService();
                using var cts = new CancellationTokenSource();
                var result = await installer.InstallAppAsync(
                    appInfo, new[] { "winget", "msstore" }, cts.Token, progress, "C:\\", null, Views.UiGuards.ConfirmPackageManagerInstallAsync);

                AppLogger.Write(result.Success
                    ? $"✅ {entry.AppName} переустановлен"
                    : $"❌ {entry.AppName}: {result.Message}");
            }
            catch (OperationCanceledException)
            {
                // InstallAppAsync гасит обычные ошибки и возвращает (false, сообщение),
                // но отмену пробрасывает наружу намеренно. Сюда же попадает таймаут
                // HttpClient при прямой загрузке (TaskCanceledException).
                AppLogger.Write($"⏹️ Переустановка {entry.AppName} прервана");
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка переустановки {entry.AppName}: {ex.Message}");
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                IsReinstalling = false;
            }
        }
    }
}
