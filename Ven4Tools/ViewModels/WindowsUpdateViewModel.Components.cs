using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// «.NET Framework 3.5 в один клик» — нужен 1С, старым играм и программам.
    /// Включение через Центр обновления, при ошибке — с установочной флешки/ISO.
    /// </summary>
    public sealed partial class WindowsUpdateViewModel
    {
        private bool _netFxCheckedOnce;

        public RelayCommand CheckNetFxCommand { get; private set; } = null!;
        public RelayCommand EnableNetFxCommand { get; private set; } = null!;
        public RelayCommand EnableNetFxFromFolderCommand { get; private set; } = null!;
        public RelayCommand EnableNetFxFromIsoCommand { get; private set; } = null!;
        public RelayCommand DisableNetFxCommand { get; private set; } = null!;

        private WindowsFeatureState _netFxState = WindowsFeatureState.Unknown;
        public WindowsFeatureState NetFxState
        {
            get => _netFxState;
            private set
            {
                if (!SetField(ref _netFxState, value)) return;
                OnPropertyChanged(nameof(CanEnableNetFx));
                OnPropertyChanged(nameof(CanDisableNetFx));
            }
        }

        /// <summary>Кнопки включения видны, пока компонент не включён (или состояние неизвестно).</summary>
        public bool CanEnableNetFx => NetFxState is not (WindowsFeatureState.Enabled or WindowsFeatureState.EnablePending);
        public bool CanDisableNetFx => NetFxState == WindowsFeatureState.Enabled;

        private string _netFxStateText = "Состояние ещё не проверялось.";
        public string NetFxStateText { get => _netFxStateText; private set => SetField(ref _netFxStateText, value); }

        private string _netFxStatusText = "";
        public string NetFxStatusText { get => _netFxStatusText; private set => SetField(ref _netFxStatusText, value); }

        private bool _isNetFxRunning;
        public bool IsNetFxRunning { get => _isNetFxRunning; private set => SetField(ref _isNetFxRunning, value); }

        private double _netFxProgress;
        public double NetFxProgress { get => _netFxProgress; private set => SetField(ref _netFxProgress, value); }

        private bool _showNetFxSourceHint;
        /// <summary>Подсветить вариант «с носителя» после ошибки, связанной с источником файлов.</summary>
        public bool ShowNetFxSourceHint { get => _showNetFxSourceHint; private set => SetField(ref _showNetFxSourceHint, value); }

        private void InitComponentCommands()
        {
            InitServicingCommands();
            CheckNetFxCommand = RelayCommand.FromAsync(_ => CheckNetFxStateAsync(), _ => !IsSystemTaskRunning);
            EnableNetFxCommand = RelayCommand.FromAsync(_ => EnableNetFxAsync(), _ => !IsSystemTaskRunning);
            EnableNetFxFromFolderCommand = RelayCommand.FromAsync(_ => EnableNetFxFromFolderAsync(), _ => !IsSystemTaskRunning);
            EnableNetFxFromIsoCommand = RelayCommand.FromAsync(_ => EnableNetFxFromIsoAsync(), _ => !IsSystemTaskRunning);
            DisableNetFxCommand = RelayCommand.FromAsync(_ => DisableNetFxAsync(), _ => !IsSystemTaskRunning);
        }

        private void OnComponentsSectionShown()
        {
            RefreshDriverPolicyState();
            if (_netFxCheckedOnce || IsSystemTaskRunning) return;
            _ = CheckNetFxStateSafeAsync();
        }

        private async Task CheckNetFxStateSafeAsync()
        {
            try { await CheckNetFxStateAsync(); }
            catch (Exception ex) { AppLogger.Write(ex, "WindowsUpdateViewModel.CheckNetFxStateAsync"); }
        }

        /// <summary>Подпись состояния по-русски. Чистая функция — для тестов.</summary>
        internal static string DescribeNetFxState(WindowsFeatureState state) => state switch
        {
            WindowsFeatureState.Enabled => "✅ Включён — программы, которым нужен .NET 3.5, запустятся.",
            WindowsFeatureState.Disabled => "Выключен. Файлы компонента уже есть в системе — включится быстро.",
            WindowsFeatureState.DisabledPayloadRemoved => "Выключен. Файлов компонента в системе нет — Windows скачает их через Центр обновления (или возьмёт с установочного носителя).",
            WindowsFeatureState.EnablePending => "⏳ Включается — завершится после перезагрузки компьютера.",
            WindowsFeatureState.DisablePending => "⏳ Выключается — завершится после перезагрузки компьютера.",
            _ => "Состояние неизвестно."
        };

        private async Task CheckNetFxStateAsync()
        {
            if (IsSystemTaskRunning) return;
            _netFxCheckedOnce = true;
            NetFxStateText = "⏳ Проверка состояния...";
            // Проверка тоже запускает dism.exe — на эти секунды остальные действия
            // недоступны, иначе включение упёрлось бы в «уже идёт обслуживание Windows».
            BeginSystemTask(cancellable: false);
            WindowsFeatureState state;
            string error;
            try
            {
                (state, error) = await NetFx3Service.GetStateAsync(CancellationToken.None);
            }
            finally
            {
                EndSystemTask();
            }
            NetFxState = state;
            NetFxStateText = state == WindowsFeatureState.Unknown && error.Length > 0
                ? $"Состояние неизвестно: {error}"
                : DescribeNetFxState(state);
        }

        private Task EnableNetFxAsync()
        {
            const string caption = ".NET Framework 3.5";
            if (!CanStartSystemTask(caption, needsServicingExclusive: true)) return Task.CompletedTask;
            var answer = MessageBox.Show(
                "Включить .NET Framework 3.5?\n\n" +
                "Windows скачает файлы компонента через Центр обновления — это может занять от нескольких минут до получаса. " +
                "Если интернета нет или загрузка запрещена, можно будет указать установочную флешку или ISO.\n\n" +
                "Может потребоваться перезагрузка.",
                caption, MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return Task.CompletedTask;
            return RunNetFxEnableAsync(source: null, isoToDismount: null);
        }

        private Task EnableNetFxFromFolderAsync()
        {
            const string caption = ".NET Framework 3.5 с носителя";
            if (!CanStartSystemTask(caption, needsServicingExclusive: true)) return Task.CompletedTask;

            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = Tr("Выберите установочную флешку Windows или папку sources\\sxs")
            };
            if (ShowDialogWithOwner(dlg) != true) return Task.CompletedTask;

            string? sxs = NetFx3Service.ResolveSxsFolder(dlg.FolderName, Directory.Exists);
            if (sxs == null)
            {
                MessageBox.Show(
                    $"В «{dlg.FolderName}» нет папки sources\\sxs.\n\n" +
                    "Выберите корень установочной флешки Windows (или подключённого образа) той же версии, что установлена на компьютере.",
                    caption, MessageBoxButton.OK, MessageBoxImage.Warning);
                return Task.CompletedTask;
            }
            if (!ConfirmSourceInstall(caption, sxs)) return Task.CompletedTask;
            return RunNetFxEnableAsync(sxs, isoToDismount: null);
        }

        private async Task EnableNetFxFromIsoAsync()
        {
            const string caption = ".NET Framework 3.5 из ISO";
            if (!CanStartSystemTask(caption, needsServicingExclusive: true)) return;

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = Tr("Выберите образ установки Windows (ISO)"),
                Filter = Tr("Образ диска (*.iso)|*.iso")
            };
            if (ShowDialogWithOwner(dlg) != true) return;
            string iso = dlg.FileName;
            if (!ConfirmSourceInstall(caption, iso)) return;

            NetFxStatusText = "⏳ Подключение образа...";
            var (root, error) = await NetFx3Service.MountIsoAsync(iso, CancellationToken.None);
            if (root == null)
            {
                NetFxStatusText = $"❌ {error}";
                return;
            }
            AppLogger.Write($"[Компоненты] Образ {Path.GetFileName(iso)} подключён как {root}");
            string? sxs = NetFx3Service.ResolveSxsFolder(root, Directory.Exists);
            if (sxs == null)
            {
                await NetFx3Service.DismountIsoAsync(iso);
                NetFxStatusText = "❌ В образе нет папки sources\\sxs — это не установочный образ Windows.";
                return;
            }
            await RunNetFxEnableAsync(sxs, isoToDismount: iso);
        }

        private bool? ShowDialogWithOwner(Microsoft.Win32.CommonDialog dialog)
        {
            var owner = OwnerWindowProvider?.Invoke();
            return owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        }

        private static bool ConfirmSourceInstall(string caption, string source) =>
            MessageBox.Show(
                $"Включить .NET Framework 3.5, взяв файлы из «{source}»?\n\n" +
                "Носитель должен быть той же версии Windows (и разрядности), что установлена на компьютере, — " +
                "иначе DISM сообщит, что файлы не подходят. Центр обновления при этом не используется.\n\n" +
                "Может потребоваться перезагрузка.",
                caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        private async Task RunNetFxEnableAsync(string? source, string? isoToDismount)
        {
            var ct = BeginSystemTask(cancellable: true);
            IsNetFxRunning = true;
            NetFxProgress = 0;
            ShowNetFxSourceHint = false;
            NetFxStatusText = source == null
                ? "⏳ Включение .NET Framework 3.5 через Центр обновления..."
                : "⏳ Включение .NET Framework 3.5 с носителя...";
            AppLogger.Write("[Компоненты] Включение .NET Framework 3.5" + (source != null ? $" с носителя {source}" : ""));
            DismOperationResult result;
            try
            {
                result = await NetFx3Service.EnableAsync(source, OnNetFxPercent, ct);
            }
            finally
            {
                if (isoToDismount != null) await NetFx3Service.DismountIsoAsync(isoToDismount);
                IsNetFxRunning = false;
                EndSystemTask();
            }

            await ShowNetFxResultAsync(result, enabling: true, fromWindowsUpdate: source == null);
        }

        private async Task DisableNetFxAsync()
        {
            const string caption = ".NET Framework 3.5";
            if (!CanStartSystemTask(caption, needsServicingExclusive: true)) return;
            var answer = MessageBox.Show(
                "Выключить .NET Framework 3.5?\n\n" +
                "Программы, которым он нужен (1С, старые игры и программы), перестанут запускаться. " +
                "Включить обратно можно здесь же.\n\nМожет потребоваться перезагрузка.",
                caption, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            var ct = BeginSystemTask(cancellable: true);
            IsNetFxRunning = true;
            NetFxProgress = 0;
            NetFxStatusText = "⏳ Выключение .NET Framework 3.5...";
            DismOperationResult result;
            try
            {
                result = await NetFx3Service.DisableAsync(OnNetFxPercent, ct);
            }
            finally
            {
                IsNetFxRunning = false;
                EndSystemTask();
            }
            await ShowNetFxResultAsync(result, enabling: false, fromWindowsUpdate: false);
        }

        private void OnNetFxPercent(double percent)
        {
            // Прогресс приходит из потока чтения вывода DISM — в UI через диспетчер.
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess()) NetFxProgress = percent;
            else dispatcher.BeginInvoke(() => NetFxProgress = percent);
        }

        private async Task ShowNetFxResultAsync(DismOperationResult result, bool enabling, bool fromWindowsUpdate)
        {
            const string caption = ".NET Framework 3.5";
            string action = enabling ? Tr("включён") : Tr("выключен");
            if (result.Cancelled)
            {
                NetFxStatusText = "Операция прервана.";
            }
            else if (result.Success)
            {
                NetFxProgress = 100;
                NetFxStatusText = result.RebootRequired
                    ? $"✅ .NET Framework 3.5 {action}. Перезагрузите компьютер, чтобы завершить."
                    : $"✅ .NET Framework 3.5 {action}.";
                AppLogger.Write($"[Компоненты] .NET Framework 3.5 {action}" + (result.RebootRequired ? " (нужна перезагрузка)" : ""));
                if (result.RebootRequired)
                    MessageBox.Show(NetFxStatusText, caption, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                NetFxStatusText = $"❌ {result.Message}";
                AppLogger.Write($"[Компоненты] .NET Framework 3.5 не {action}: {result.Message}");
                if (enabling && result.SuggestSourceMedia)
                {
                    ShowNetFxSourceHint = true;
                    if (fromWindowsUpdate)
                        MessageBox.Show(result.Message + "\n\n" +
                            "Ниже на вкладке можно указать установочную флешку (или папку) либо ISO-образ Windows той же версии.",
                            caption, MessageBoxButton.OK, MessageBoxImage.Warning);
                    else
                        MessageBox.Show(result.Message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show(result.Message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            await CheckNetFxStateAsync();
        }
    }
}
