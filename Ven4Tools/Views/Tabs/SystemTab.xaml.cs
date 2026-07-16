using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.Views.Tabs
{
    public partial class SystemTab : UserControl
    {
        private bool _initialized = false;
        private bool _loadingAppearance = true;
        private bool _connSubscribed = false;

        public SystemTab()
        {
            InitializeComponent();

            SelectComboByTag(cmbTheme, ProfileService.Current.Theme);
            SelectComboByTag(cmbLanguage, ProfileService.Current.Language);
            chkCompactMode.IsChecked = ProfileService.Current.CompactMode;
            chkReduceMotion.IsChecked = ProfileService.Current.ReduceMotion;
            MotionService.Enabled = !ProfileService.Current.ReduceMotion;
            _loadingAppearance = false;

            Loaded += SystemTab_Loaded;

            chkMinimizeToTray.IsChecked = ProfileService.Current.MinimizeToTray;
            chkNotifications.Click += (_, _) => SaveSettings();
            chkUpdateNotifications.Click += (_, _) => SaveSettings();
            sliderCatalogTimeout.ValueChanged += (_, e) => { txtCatalogTimeout.Text = $"{(int)e.NewValue} сек"; SaveSettings(); };
            sliderCheckTimeout.ValueChanged += (_, e) => { txtCheckTimeout.Text = $"{(int)e.NewValue} сек"; SaveSettings(); };
            btnCopySystemInfo.Click += BtnCopySystemInfo_Click;
            btnOpenLogs.Click += BtnOpenLogs_Click;
            btnOpenLatestLog.Click += BtnOpenLatestLog_Click;
            btnClearLogs.Click += BtnClearLogs_Click;
            btnCheckUpdates.Click += BtnCheckUpdates_Click;
            btnDisableTurboBoost.Click += BtnDisableTurboBoost_Click;
            btnEnableTurboBoost.Click += BtnEnableTurboBoost_Click;
            // Подписка на btnSaveSnapshot задаётся в XAML (Click="BtnSaveSnapshot_Click"),
            // повторная подписка в code-behind вызывала двойной вызов обработчика.

            // Offline mode
            chkOfflineMode.Click      += ChkOfflineMode_Click;
            chkForceOnlineMode.Click  += ChkForceOnlineMode_Click;
            chkParanoidMode.Click     += ChkParanoidMode_Click;
            txtOfflineCachePath.LostFocus += (_, _) => SaveOfflineSettings();

            // Подписка на ConnectivityMonitor — в Loaded: вкладка кэшируется и переиспользуется,
            // поэтому после Unloaded нужно подписываться заново при каждом показе
            Unloaded += SystemTab_Unloaded;

            LoadSettings();
            LoadOfflineSettings();
        }

        private void OnConnectivityChanged(bool online) => Dispatcher.Invoke(UpdateConnectivityStatus);

        private void SystemTab_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_connSubscribed)
            {
                ConnectivityMonitor.StatusChanged -= OnConnectivityChanged;
                _connSubscribed = false;
            }
        }

        private async void SystemTab_Loaded(object sender, RoutedEventArgs e)
        {
            // Переподписка при каждом показе вкладки (после Unloaded подписка снимается)
            if (!_connSubscribed)
            {
                ConnectivityMonitor.StatusChanged += OnConnectivityChanged;
                _connSubscribed = true;
            }
            UpdateConnectivityStatus();

            if (_initialized) return;
            _initialized = true;

            await LoadSystemInfoAsync();
            LoadSourceOrderUI();
            UpdateCacheStats();
            LoadCacheAppsList();
            LoadSnapshotsList();

            bool? turbo = await GetTurboBoostStateAsync();
            if (turbo.HasValue)
                AppLogger.Write(turbo.Value ? "⚡ Турбобуст: включён" : "⚡ Турбобуст: отключён");
        }

    }
}
