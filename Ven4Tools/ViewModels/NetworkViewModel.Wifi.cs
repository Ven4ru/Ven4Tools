using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Ven4Tools.Helpers;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Строка «Сохранённые сети Wi-Fi». Пароль читается только по кнопке и
    /// забывается, как только его скрыли: в памяти клиента он не задерживается
    /// дольше, чем виден на экране.
    /// </summary>
    public sealed class WifiNetworkRow : ViewModelBase
    {
        private const string Mask = "••••••••";

        public WifiNetworkRow(WifiProfileInfo profile) => Profile = profile;

        public WifiProfileInfo Profile { get; }
        public string Name => Profile.ProfileName;

        private string _passwordText = Mask;
        public string PasswordText
        {
            get => _passwordText;
            private set => SetField(ref _passwordText, value);
        }

        private bool _isPasswordShown;
        public bool IsPasswordShown
        {
            get => _isPasswordShown;
            private set { if (SetField(ref _isPasswordShown, value)) OnPropertyChanged(nameof(PasswordButtonText)); }
        }
        public string PasswordButtonText => _isPasswordShown ? "🙈 Скрыть" : "👁 Показать пароль";

        private BitmapSource? _qrImage;
        public BitmapSource? QrImage
        {
            get => _qrImage;
            private set
            {
                if (!SetField(ref _qrImage, value)) return;
                OnPropertyChanged(nameof(HasQr));
                OnPropertyChanged(nameof(QrButtonText));
            }
        }
        public bool HasQr => _qrImage != null;
        public string QrButtonText => _qrImage != null ? "Скрыть QR-код" : "📱 QR-код для телефона";

        private string? _note;
        public string? Note
        {
            get => _note;
            private set { if (SetField(ref _note, value)) OnPropertyChanged(nameof(HasNote)); }
        }
        public bool HasNote => !string.IsNullOrEmpty(_note);

        internal void ShowPassword(WifiProfileDetails? details)
        {
            if (details == null)
            {
                Note = Tr("Windows не отдала профиль этой сети — нет данных.");
                return;
            }
            string? text = DescribeMissingPassword(details);
            if (text != null)
            {
                Note = text;
                return;
            }
            PasswordText = details.Password!;
            IsPasswordShown = true;
            Note = null;
        }

        internal void HidePassword()
        {
            PasswordText = Mask;
            IsPasswordShown = false;
        }

        internal void ShowQr(WifiProfileDetails? details)
        {
            if (details == null)
            {
                Note = Tr("Windows не отдала профиль этой сети — нет данных.");
                return;
            }
            string? payload = WifiQrPayload.Build(details.Ssid, details.Password, details.Security, details.Hidden);
            if (payload == null)
            {
                Note = DescribeMissingPassword(details) ?? Tr("Для этой сети QR-код не строится.");
                return;
            }
            try
            {
                QrImage = QrBitmap.Render(payload);
                Note = Tr("Наведите камеру телефона на код — телефон предложит подключиться к сети.");
            }
            catch (ArgumentException)
            {
                Note = Tr("Имя сети и пароль слишком длинные для QR-кода.");
            }
        }

        internal void HideQr()
        {
            QrImage = null;
            Note = null;
        }

        /// <summary>Почему пароля нет; null — пароль есть (или сеть открытая).</summary>
        private static string? DescribeMissingPassword(WifiProfileDetails d) => d.Security switch
        {
            WifiSecurityKind.Open => Tr("Открытая сеть — пароля нет."),
            WifiSecurityKind.Enterprise => Tr("Корпоративная сеть (вход по учётной записи) — общего пароля нет."),
            WifiSecurityKind.Unknown when d.Password == null => Tr("Тип защиты сети не распознан — нет данных."),
            _ when d.KeyProtected => Tr("Windows хранит пароль этой сети зашифрованным и не отдаёт его."),
            _ when d.Password == null => Tr("Пароль у профиля не сохранён."),
            _ => null,
        };
    }

    public sealed partial class NetworkViewModel
    {
        // ── Сохранённые сети Wi-Fi ───────────────────────────────────────────

        public ObservableCollection<WifiNetworkRow> WifiNetworks { get; } = new();

        public RelayCommand RefreshWifiCommand { get; private set; } = null!;
        public RelayCommand ToggleWifiPasswordCommand { get; private set; } = null!;
        public RelayCommand ToggleWifiQrCommand { get; private set; } = null!;

        private bool _wifiAvailable;
        /// <summary>Есть ли беспроводной адаптер. Нет — блок «Сохранённые сети Wi-Fi» скрыт целиком.</summary>
        public bool WifiAvailable
        {
            get => _wifiAvailable;
            private set => SetField(ref _wifiAvailable, value);
        }

        private string _wifiStatusText = "";
        public string WifiStatusText
        {
            get => _wifiStatusText;
            private set => SetField(ref _wifiStatusText, value);
        }

        private bool _isLoadingWifi;

        private void InitWifi()
        {
            RefreshWifiCommand = RelayCommand.FromAsync(_ => LoadWifiAsync(), _ => !_isLoadingWifi);
            ToggleWifiPasswordCommand = RelayCommand.FromAsync(p => ToggleWifiPasswordAsync(p as WifiNetworkRow));
            ToggleWifiQrCommand = RelayCommand.FromAsync(p => ToggleWifiQrAsync(p as WifiNetworkRow));
        }

        /// <summary>Перечитать список профилей (только имена — пароли не читаются).</summary>
        internal async Task LoadWifiAsync()
        {
            if (_isLoadingWifi) return;
            _isLoadingWifi = true;
            RefreshWifiCommand.RaiseCanExecuteChanged();
            try
            {
                var (hasAdapter, profiles) = await Task.Run(() =>
                {
                    bool has = WifiProfileService.HasWirelessAdapter();
                    IReadOnlyList<WifiProfileInfo> list = has ? WifiProfileService.GetProfiles() : Array.Empty<WifiProfileInfo>();
                    return (has, list);
                });

                WifiAvailable = hasAdapter;
                WifiNetworks.Clear();
                foreach (WifiProfileInfo p in profiles)
                    WifiNetworks.Add(new WifiNetworkRow(p));
                WifiStatusText = profiles.Count == 0
                    ? Tr("Сохранённых сетей нет.")
                    : string.Format(Tr("Сохранённых сетей: {0}. Пароли скрыты, пока вы не нажмёте «Показать пароль»."), profiles.Count);
            }
            finally
            {
                _isLoadingWifi = false;
                RefreshWifiCommand.RaiseCanExecuteChanged();
            }
        }

        private async Task ToggleWifiPasswordAsync(WifiNetworkRow? row)
        {
            if (row == null) return;
            if (row.IsPasswordShown)
            {
                row.HidePassword();
                return;
            }
            WifiProfileDetails? details = await Task.Run(() => WifiProfileService.GetDetails(row.Profile));
            row.ShowPassword(details);
        }

        private async Task ToggleWifiQrAsync(WifiNetworkRow? row)
        {
            if (row == null) return;
            if (row.HasQr)
            {
                row.HideQr();
                return;
            }
            WifiProfileDetails? details = await Task.Run(() => WifiProfileService.GetDetails(row.Profile));
            row.ShowQr(details);
        }
    }
}
