using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class SystemTab : UserControl
    {

        private void LoadSettings()
        {
            // AppSettings уже загружены из того же файла при старте приложения.
            // Сами уведомления — не таймауты, а поведение, поэтому живут в профиле
            // (profile.json), как и остальные функциональные переключатели этой вкладки.
            chkNotifications.IsChecked = ProfileService.Current.NotifyInstallComplete;
            chkUpdateNotifications.IsChecked = ProfileService.Current.NotifyAppUpdates;
            sliderCatalogTimeout.Value = Math.Clamp(AppSettings.CatalogTimeout, 3, 30);
            sliderCheckTimeout.Value = Math.Clamp(AppSettings.CheckTimeout, 5, 60);
            txtCatalogTimeout.Text = $"{(int)sliderCatalogTimeout.Value} сек";
            txtCheckTimeout.Text = $"{(int)sliderCheckTimeout.Value} сек";

            // Параметры установки живут в профиле (profile.json), а не в AppSettings —
            // их читает InstallationService через ProfileService.Current.
            chkSilentInstall.IsChecked = ProfileService.Current.SilentInstall;
            txtDefaultInstallFolder.Text = ProfileService.Current.DefaultInstallFolder;
            txtDefaultInstallFolderStatus.Text = "";

            _loadingCatalogMode = true;
            SelectComboByTag(cmbCatalogMode, ProfileService.Current.CatalogMode);
            _loadingCatalogMode = false;
        }

        private void SaveSettings()
        {
            AppSettings.Save(
                catalogTimeout: (int)sliderCatalogTimeout.Value,
                checkTimeout:   (int)sliderCheckTimeout.Value);

            ProfileService.Current.NotifyInstallComplete = chkNotifications.IsChecked ?? true;
            ProfileService.Current.NotifyAppUpdates = chkUpdateNotifications.IsChecked ?? true;
            ProfileService.Save();
        }

        // ── Установка приложений ──────────────────────────────────────────────────

        private void ChkSilentInstall_Click(object sender, RoutedEventArgs e)
        {
            ProfileService.Current.SilentInstall = chkSilentInstall.IsChecked == true;
            ProfileService.Save();
        }

        private void BtnBrowseDefaultInstallFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description         = "Выберите папку установки приложений по умолчанию",
                ShowNewFolderButton = true
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            ApplyDefaultInstallFolder(dlg.SelectedPath);
        }

        private void TxtDefaultInstallFolder_LostFocus(object sender, RoutedEventArgs e)
            => ApplyDefaultInstallFolder(txtDefaultInstallFolder.Text);

        /// <summary>
        /// Сохраняет папку установки в профиль, предварительно прогоняя её через тот же
        /// <see cref="CommandLineGuard.ValidateInstallFolder"/>, которым пользуется путь
        /// winget. Иначе значение молча отбрасывалось бы только в момент установки, и
        /// пользователь считал бы, что папка задана. Пустая строка допустима — это
        /// штатный сброс к выбору winget по умолчанию.
        /// </summary>
        private void ApplyDefaultInstallFolder(string? path)
        {
            string value = (path ?? "").Trim();

            if (!CommandLineGuard.ValidateInstallFolder(value))
            {
                txtDefaultInstallFolder.Text = ProfileService.Current.DefaultInstallFolder;
                txtDefaultInstallFolderStatus.Text =
                    "⚠ Путь не принят: нужен абсолютный локальный путь без сетевых имён и кавычек. Оставлено прежнее значение.";
                return;
            }

            txtDefaultInstallFolder.Text = value;
            ProfileService.Current.DefaultInstallFolder = value;
            ProfileService.Save();

            txtDefaultInstallFolderStatus.Text = value.Length == 0
                ? "Папка не задана — winget выбирает её сам."
                : $"Сохранено: {value}";
        }

        // ── Область каталога ───────────────────────────────────────────────────────

        private void CmbCatalogMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingCatalogMode || cmbCatalogMode.SelectedItem is not ComboBoxItem item) return;
            ProfileService.Current.CatalogMode = item.Tag?.ToString() ?? "full";
            ProfileService.Save();
        }

        // ── Скрытые приложения ─────────────────────────────────────────────────────

        private void BtnUnhideAllApps_Click(object sender, RoutedEventArgs e)
        {
            // Отдельный экземпляр AppManager нарочно: он ничего не держит в памяти
            // кроме файлового состояния (apps.json/alternatives.json/hidden.json),
            // тот же приём, что и у ProfileService/AppSettings — обращение к диску,
            // а не к уже загруженному в CatalogViewModel списку строк.
            var appManager = new AppManager();
            int count = appManager.HiddenAppsCount;
            if (count == 0)
            {
                txtHiddenAppsStatus.Text = "Скрытых приложений нет.";
                return;
            }

            appManager.UnhideAllApps();
            txtHiddenAppsStatus.Text =
                $"Показано: {count}. Чтобы увидеть их в списке — «Обновить каталог» на вкладке «Каталог» или перезапустите клиент.";
            AppLogger.Write($"👁 Показаны скрытые приложения ({count})");
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
