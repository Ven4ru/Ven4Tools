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
        
        private void LoadSettings()
        {
            // AppSettings is already loaded from the same file at startup
            chkNotifications.IsChecked = AppSettings.Notifications;
            chkUpdateNotifications.IsChecked = AppSettings.UpdateNotifications;
            sliderCatalogTimeout.Value = Math.Clamp(AppSettings.CatalogTimeout, 3, 30);
            sliderCheckTimeout.Value = Math.Clamp(AppSettings.CheckTimeout, 5, 60);
            txtCatalogTimeout.Text = $"{(int)sliderCatalogTimeout.Value} сек";
            txtCheckTimeout.Text = $"{(int)sliderCheckTimeout.Value} сек";
        }

        private void SaveSettings()
        {
            AppSettings.Save(
                catalogTimeout:      (int)sliderCatalogTimeout.Value,
                checkTimeout:        (int)sliderCheckTimeout.Value,
                notifications:       chkNotifications.IsChecked ?? true,
                updateNotifications: chkUpdateNotifications.IsChecked ?? true);
        }

        // ── Перенос настроек (экспорт/импорт) ─────────────────────────────────────

        private void BtnExportSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new SaveFileDialog
                {
                    Title    = "Экспорт настроек Ven4Tools",
                    Filter   = "Архив настроек Ven4Tools (*.zip)|*.zip",
                    FileName = $"Ven4Tools-настройки-{DateTime.Now:yyyy-MM-dd}.zip"
                };
                if (dlg.ShowDialog() != true) return;

                var result = ProfileExportService.Export(dlg.FileName);
                txtTransferStatus.Text = result.Message;
                AppLogger.Write(result.Success ? $"📤 {result.Message}" : $"❌ {result.Message}");
                if (!result.Success)
                    MessageBox.Show(result.Message, "Экспорт настроек",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка экспорта настроек: {ex.Message}");
                MessageBox.Show($"Не удалось экспортировать настройки: {ex.Message}",
                    "Экспорт настроек", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnImportSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "Импорт настроек Ven4Tools",
                    Filter = "Архив настроек Ven4Tools (*.zip)|*.zip|Все файлы (*.*)|*.*"
                };
                if (dlg.ShowDialog() != true) return;

                var confirm = MessageBox.Show(
                    "Текущие локальные настройки (профиль, пресеты, избранное, параметры приложения) будут перезаписаны данными из архива.\n\nПродолжить?",
                    "Импорт настроек", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;

                var result = ProfileExportService.Import(dlg.FileName);
                txtTransferStatus.Text = result.Message;
                AppLogger.Write(result.Success ? $"📥 {result.Message}" : $"❌ {result.Message}");

                if (!result.Success)
                {
                    MessageBox.Show(result.Message, "Импорт настроек",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Обновляем элементы вкладки и оформление по свежим данным сервисов
                LoadSettings();
                LoadOfflineSettings();
                LoadSourceOrderUI();
                chkMinimizeToTray.IsChecked = ProfileService.Current.MinimizeToTray;
                ThemeService.Apply(ProfileService.Current.Theme);
                LocalizationService.Init();

                MessageBox.Show(
                    result.Message + "\n\nНастройки применены. Избранное обновится после перезапуска приложения.",
                    "Импорт настроек", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка импорта настроек: {ex.Message}");
                MessageBox.Show($"Не удалось импортировать настройки: {ex.Message}",
                    "Импорт настроек", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
