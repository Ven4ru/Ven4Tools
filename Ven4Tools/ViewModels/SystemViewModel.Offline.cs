using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        private void LoadOfflineSettings()
        {
            SetField(ref _offlineMode, ProfileService.Current.OfflineMode, nameof(OfflineMode));
            SetField(ref _forceOnlineMode, ProfileService.Current.ForceOnlineMode, nameof(ForceOnlineMode));
            SetField(ref _paranoidMode, ProfileService.Current.ParanoidMode, nameof(ParanoidMode));

            string cachePath = ProfileService.Current.OfflineCachePath;
            if (string.IsNullOrEmpty(cachePath)) cachePath = OfflineService.CacheBasePath;
            SetField(ref _offlineCachePathText, cachePath, nameof(OfflineCachePathText));
        }

        private void SaveOfflineSettings()
        {
            ProfileService.Current.OfflineCachePath = OfflineCachePathText.Trim();
            ProfileService.Save();
        }

        private bool _offlineMode;
        public bool OfflineMode
        {
            get => _offlineMode;
            set
            {
                if (_offlineMode == value) return;
                SetField(ref _offlineMode, value);
                ProfileService.Current.OfflineMode = value;
                ProfileService.Save();
                RefreshTabVisibility?.Invoke();
                UpdateConnectivityStatus();
            }
        }

        private bool _forceOnlineMode;
        public bool ForceOnlineMode
        {
            get => _forceOnlineMode;
            set
            {
                if (_forceOnlineMode == value) return;
                SetField(ref _forceOnlineMode, value);
                ProfileService.Current.ForceOnlineMode = value;
                ProfileService.Save();
                RefreshTabVisibility?.Invoke();
                UpdateConnectivityStatus();
            }
        }

        private bool _paranoidMode;
        public bool ParanoidMode
        {
            get => _paranoidMode;
            set
            {
                if (_paranoidMode == value) return;
                SetField(ref _paranoidMode, value);
                ProfileService.Current.ParanoidMode = value;
                ProfileService.Save();
            }
        }

        // Сохранение — только по LostFocus (см. UpdateSourceTrigger=LostFocus в XAML),
        // не на каждое нажатие клавиши, ровно как в оригинале (txtOfflineCachePath.LostFocus).
        private string _offlineCachePathText = "";
        public string OfflineCachePathText
        {
            get => _offlineCachePathText;
            set
            {
                if (_offlineCachePathText == value) return;
                SetField(ref _offlineCachePathText, value);
                SaveOfflineSettings();
            }
        }

        private string _connIconText = "🟢";
        public string ConnIconText { get => _connIconText; private set => SetField(ref _connIconText, value); }

        private string _connStatusText = "Интернет доступен";
        public string ConnStatusText { get => _connStatusText; private set => SetField(ref _connStatusText, value); }

        // Дефолт — прозрачная кисть: в оригинальном XAML у pnlConnStatus нет статичного
        // Background, цвет всегда выставлялся программно из UpdateConnectivityStatus().
        private Brush _connStatusBackground = Brushes.Transparent;
        public Brush ConnStatusBackground { get => _connStatusBackground; private set => SetField(ref _connStatusBackground, value); }

        public void UpdateConnectivityStatus()
        {
            bool online        = ConnectivityMonitor.IsOnline;
            bool offlineForced = ProfileService.Current.OfflineMode;
            bool onlineForced  = ProfileService.Current.ForceOnlineMode;

            if (offlineForced)
            {
                ConnIconText = "🟡";
                ConnStatusText = "Принудительный офлайн — вкладки скрыты вручную";
                ConnStatusBackground = new SolidColorBrush(Color.FromRgb(70, 55, 10));
            }
            else if (!online && onlineForced)
            {
                ConnIconText = "🟠";
                ConnStatusText = "Соединение не обнаружено, но онлайн-режим принудительно включён";
                ConnStatusBackground = new SolidColorBrush(Color.FromRgb(80, 45, 5));
            }
            else if (!online)
            {
                ConnIconText = "🔴";
                ConnStatusText = "Интернет недоступен — онлайн-вкладки скрыты";
                ConnStatusBackground = new SolidColorBrush(Color.FromRgb(80, 20, 20));
            }
            else
            {
                ConnIconText = "🟢";
                ConnStatusText = "Интернет доступен — все вкладки активны";
                ConnStatusBackground = new SolidColorBrush(Color.FromRgb(15, 50, 20));
            }
            ConnectivityStatusUpdated?.Invoke();
        }
    }
}
