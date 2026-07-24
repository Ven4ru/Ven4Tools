using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    // Обёртка над AppRowViewModel для карточки приложения (прототип, см.
    // docs/superpowers/specs/2026-07-17-app-cards-prototype-design.md). Не
    // дублирует состояние строки каталога — читает его напрямую и вызывает
    // существующие/новые сервисы установки.
    public sealed class AppCardViewModel : INotifyPropertyChanged
    {
        public AppRowViewModel Row { get; }
        private readonly Func<string, Task<bool>> _confirmPmInstall;
        private readonly string _installDrive;

        public event Action? RequestClose;

        public AppCardViewModel(AppRowViewModel row, Func<string, Task<bool>> confirmPmInstall, string installDrive)
        {
            Row = row;
            _confirmPmInstall = confirmPmInstall;
            _installDrive = installDrive;
            // Карточка не дублирует состояние строки, а читает его напрямую (CanLaunch,
            // IsInstalled и т.д. — проброшенные геттеры), но WPF-биндинг обновляет
            // значение только когда САМА AppCardViewModel поднимает PropertyChanged для
            // своего свойства. Без этой подписки фоновый пересчёт строки
            // (CatalogViewModel.UpdateInstalledStatusAsync — переиндексация Play-кнопки
            // при каждом обновлении статуса установленных) не долетал бы до уже открытой
            // карточки: она замирала на значении, актуальном в момент открытия. Снимается
            // в Detach() при закрытии окна (AppCardWindow.Closed), чтобы Row не держал
            // ссылку на закрытую карточку.
            Row.PropertyChanged += Row_PropertyChanged;

            LaunchCommand = new RelayCommand(_ =>
            {
                Row.LaunchCommand.Execute(null);
                // Row.Launch() сбрасывает LaunchPath в null при неудаче (см.
                // AppRowViewModel.Launch) — на этом различаем успех/провал без
                // отдельного возвращаемого значения у команды.
                if (Row.LaunchPath != null)
                {
                    RequestClose?.Invoke();
                }
                else
                {
                    StatusText = "❌ Не удалось запустить приложение";
                    OnPropertyChanged(nameof(CanLaunch));
                    OnPropertyChanged(nameof(ShowLaunchButton));
                }
            }, _ => Row.CanLaunch && !IsBusy);

            InstallCommand   = new RelayCommand(async _ => await InstallAsync(),   _ => !IsInstalled && !IsBusy);
            ReinstallCommand = new RelayCommand(async _ => await ReinstallAsync(), _ => IsInstalled && !IsBusy);
            UninstallCommand = new RelayCommand(async _ => await UninstallAsync(), _ => IsInstalled && !IsBusy);
        }

        public string DisplayName => Row.DisplayName;
        public string CategoryString => Row.CategoryString;
        public BitmapImage? Icon => Row.Icon;

        public string Description => string.IsNullOrWhiteSpace(Row.Description)
            ? "Описание отсутствует."
            : Row.Description!;

        public string? OfficialSiteUrl => HomepageUrlHelper.ExtractHomepage(Row.App.InstallerUrls.FirstOrDefault());

        public string VersionText => IsInstalled && !string.IsNullOrEmpty(Row.InstalledVersion)
            ? Row.InstalledVersion!
            : (Row.CatalogVersion is { Length: > 0 } v ? v : "—");

        public string SizeText => Row.CatalogSizeText is { Length: > 0 } s ? s : "—";

        public string PackageIdText => Row.App.AlternativeId is { Length: > 0 } wid
            ? wid
            : (Row.App.ChocoId is { Length: > 0 } cid ? cid : "—");

        public bool IsInstalled => Row.IsInstalled;
        public bool CanLaunch => Row.CanLaunch;

        // Видимость кнопок карточки. Вынесено в вычисляемые свойства, чтобы во
        // время «Переустановить» (Uninstall→Install) набор кнопок не мигал:
        // фаза удаления кратковременно делает IsInstalled=false, что иначе
        // показало бы кнопку «Установить» между фазами. Пока IsReinstalling=true
        // все кнопки действий скрыты — виден только StatusText.
        public bool ShowLaunchButton => Row.CanLaunch && !IsReinstalling;
        public bool ShowInstallButton => !IsInstalled && !IsReinstalling;
        public bool ShowInstalledActions => IsInstalled && !IsReinstalling;

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set { _isBusy = value; OnPropertyChanged(); RefreshCommands(); }
        }

        private bool _isReinstalling;
        public bool IsReinstalling
        {
            get => _isReinstalling;
            private set
            {
                _isReinstalling = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ShowLaunchButton));
                OnPropertyChanged(nameof(ShowInstallButton));
                OnPropertyChanged(nameof(ShowInstalledActions));
            }
        }

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); }
        }

        public RelayCommand LaunchCommand { get; }
        public RelayCommand InstallCommand { get; }
        public RelayCommand ReinstallCommand { get; }
        public RelayCommand UninstallCommand { get; }

        private void RefreshCommands()
        {
            LaunchCommand.RaiseCanExecuteChanged();
            InstallCommand.RaiseCanExecuteChanged();
            ReinstallCommand.RaiseCanExecuteChanged();
            UninstallCommand.RaiseCanExecuteChanged();
        }

        private void RaiseInstallStateChanged()
        {
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(CanLaunch));
            OnPropertyChanged(nameof(VersionText));
            OnPropertyChanged(nameof(ShowLaunchButton));
            OnPropertyChanged(nameof(ShowInstallButton));
            OnPropertyChanged(nameof(ShowInstalledActions));
            RefreshCommands();
        }

        private async Task InstallAsync()
        {
            // Тот же гейт занятости общего семафора, что перед установкой из каталога/
            // истории/пина (см. CatalogViewModel.InstallSelectedAsync, HistoryTab,
            // MainWindow.PinInstallBtn_Click) — раньше карточка молча ждала семафор
            // без явного предупреждения пользователю.
            if (Views.UiGuards.WarnIfInstallBusy()) return;

            IsBusy = true;
            try
            {
                StatusText = "Установка...";
                using var installService = new InstallationService();
                var progress = new Progress<AppInstallProgress>(p => StatusText = p.Status);
                await InstallationService.InstallSemaphore.WaitAsync();
                try
                {
                    var result = await installService.InstallAppAsync(
                        Row.App, new[] { "winget", "msstore" }, CancellationToken.None, progress,
                        _installDrive, Row.PinnedVersion, _confirmPmInstall);
                    if (result.Success)
                    {
                        Row.IsInstalled = true;
                        StatusText = "✅ Установлено";
                    }
                    else
                    {
                        StatusText = $"❌ {result.Message}";
                    }
                }
                finally
                {
                    InstallationService.InstallSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                // Ловит и исключения из конструктора InstallationService/WaitAsync
                // ДО захвата семафора — без внешнего try/finally IsBusy остался бы
                // true навсегда, а кнопки карточки — залипшими.
                StatusText = $"❌ {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                RaiseInstallStateChanged();
            }
        }

        private async Task UninstallAsync()
        {
            IsBusy = true;
            try
            {
                StatusText = "Удаление...";
                await InstallationService.InstallSemaphore.WaitAsync();
                try
                {
                    bool ok = await AppUninstallService.TryUninstallAsync(Row.App.AlternativeId, Row.DisplayName);
                    if (ok)
                    {
                        Row.IsInstalled = false;
                        Row.LaunchPath = null;
                        StatusText = "✅ Удалено";
                    }
                    else
                    {
                        StatusText = "⚠ Деинсталлятор не найден";
                    }
                }
                finally
                {
                    InstallationService.InstallSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                StatusText = $"❌ {ex.Message}";
            }
            finally
            {
                IsBusy = false;
                RaiseInstallStateChanged();
            }
        }

        private async Task ReinstallAsync()
        {
            IsReinstalling = true;
            try
            {
                await UninstallAsync();
                if (!IsInstalled) await InstallAsync();
            }
            finally
            {
                IsReinstalling = false;
                RaiseInstallStateChanged();
            }
        }

        // Изменения строки, актуальные для карточки, и какие собственные свойства
        // карточки по ним нужно перепроверить — та же связка, что RaiseInstallStateChanged
        // поднимает вручную после Install/Uninstall/Reinstall, только теперь ещё и на
        // события, пришедшие СНАРУЖИ (фоновое обновление статуса каталога).
        private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(AppRowViewModel.IsInstalled):
                case nameof(AppRowViewModel.InstalledVersion):
                case nameof(AppRowViewModel.HasUpdate):
                    OnPropertyChanged(nameof(IsInstalled));
                    OnPropertyChanged(nameof(VersionText));
                    OnPropertyChanged(nameof(ShowInstallButton));
                    OnPropertyChanged(nameof(ShowInstalledActions));
                    RefreshCommands();
                    break;
                case nameof(AppRowViewModel.CanLaunch):
                    OnPropertyChanged(nameof(CanLaunch));
                    OnPropertyChanged(nameof(ShowLaunchButton));
                    RefreshCommands();
                    break;
            }
        }

        // Вызывается при закрытии окна карточки (AppCardWindow.Closed) — без отписки
        // Row держал бы ссылку на закрытую карточку до конца жизни строки каталога.
        public void Detach() => Row.PropertyChanged -= Row_PropertyChanged;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
