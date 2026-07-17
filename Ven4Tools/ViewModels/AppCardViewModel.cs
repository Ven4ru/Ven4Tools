using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
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
                }
            }, _ => Row.CanLaunch);

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

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set { _isBusy = value; OnPropertyChanged(); RefreshCommands(); }
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
            InstallCommand.RaiseCanExecuteChanged();
            ReinstallCommand.RaiseCanExecuteChanged();
            UninstallCommand.RaiseCanExecuteChanged();
        }

        private void RaiseInstallStateChanged()
        {
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(CanLaunch));
            OnPropertyChanged(nameof(VersionText));
            RefreshCommands();
        }

        private async Task InstallAsync()
        {
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
            await UninstallAsync();
            if (!IsInstalled) await InstallAsync();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
