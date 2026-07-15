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
        // ── Offline mode ──────────────────────────────────────────────────────────

        private void LoadOfflineSettings()
        {
            chkOfflineMode.IsChecked      = ProfileService.Current.OfflineMode;
            chkForceOnlineMode.IsChecked  = ProfileService.Current.ForceOnlineMode;
            chkParanoidMode.IsChecked     = ProfileService.Current.ParanoidMode;
            txtOfflineCachePath.Text      = ProfileService.Current.OfflineCachePath;
            if (string.IsNullOrEmpty(txtOfflineCachePath.Text))
                txtOfflineCachePath.Text = OfflineService.CacheBasePath;
        }

        private void SaveOfflineSettings()
        {
            ProfileService.Current.OfflineCachePath = txtOfflineCachePath.Text.Trim();
            ProfileService.Save();
        }

        private void ChkOfflineMode_Click(object sender, RoutedEventArgs e)
        {
            ProfileService.Current.OfflineMode = chkOfflineMode.IsChecked == true;
            ProfileService.Save();
            if (Window.GetWindow(this) is MainWindow mw)
                mw.UpdateTabVisibility();
            UpdateConnectivityStatus();
        }

        private void ChkForceOnlineMode_Click(object sender, RoutedEventArgs e)
        {
            ProfileService.Current.ForceOnlineMode = chkForceOnlineMode.IsChecked == true;
            ProfileService.Save();
            if (Window.GetWindow(this) is MainWindow mw)
                mw.UpdateTabVisibility();
            UpdateConnectivityStatus();
        }

        private void ChkParanoidMode_Click(object sender, RoutedEventArgs e)
        {
            ProfileService.Current.ParanoidMode = chkParanoidMode.IsChecked == true;
            ProfileService.Save();
        }

        private void UpdateConnectivityStatus()
        {
            bool online       = ConnectivityMonitor.IsOnline;
            bool offlineForced = ProfileService.Current.OfflineMode;
            bool onlineForced  = ProfileService.Current.ForceOnlineMode;

            if (offlineForced)
            {
                txtConnIcon.Text   = "🟡";
                txtConnStatus.Text = "Принудительный офлайн — вкладки скрыты вручную";
                pnlConnStatus.Background = new SolidColorBrush(Color.FromRgb(70, 55, 10));
            }
            else if (!online && onlineForced)
            {
                txtConnIcon.Text   = "🟠";
                txtConnStatus.Text = "Соединение не обнаружено, но онлайн-режим принудительно включён";
                pnlConnStatus.Background = new SolidColorBrush(Color.FromRgb(80, 45, 5));
            }
            else if (!online)
            {
                txtConnIcon.Text   = "🔴";
                txtConnStatus.Text = "Интернет недоступен — онлайн-вкладки скрыты";
                pnlConnStatus.Background = new SolidColorBrush(Color.FromRgb(80, 20, 20));
            }
            else
            {
                txtConnIcon.Text   = "🟢";
                txtConnStatus.Text = "Интернет доступен — все вкладки активны";
                pnlConnStatus.Background = new SolidColorBrush(Color.FromRgb(15, 50, 20));
            }
            MotionService.Pulse(pnlConnStatus, 1.015, 160);
        }
    }
}
