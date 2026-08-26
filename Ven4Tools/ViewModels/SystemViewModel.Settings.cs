using System;
using System.Windows;
using Microsoft.Win32;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        private void LoadSettings()
        {
            SetField(ref _notifyInstallComplete, ProfileService.Current.NotifyInstallComplete, nameof(NotifyInstallComplete));
            SetField(ref _notifyAppUpdates, ProfileService.Current.NotifyAppUpdates, nameof(NotifyAppUpdates));

            double catalogTimeout = Math.Clamp(AppSettings.CatalogTimeout, 3, 30);
            double checkTimeout   = Math.Clamp(AppSettings.CheckTimeout, 5, 60);
            SetField(ref _catalogTimeoutValue, catalogTimeout, nameof(CatalogTimeoutValue));
            SetField(ref _checkTimeoutValue, checkTimeout, nameof(CheckTimeoutValue));
            CatalogTimeoutText = $"{(int)catalogTimeout} сек";
            CheckTimeoutText   = $"{(int)checkTimeout} сек";

            SetField(ref _silentInstall, ProfileService.Current.SilentInstall, nameof(SilentInstall));
            SetField(ref _defaultInstallFolderText, ProfileService.Current.DefaultInstallFolder, nameof(DefaultInstallFolderText));
            DefaultInstallFolderStatusText = "";

            _loadingCatalogMode = true;
            SetField(ref _catalogModeTag, ProfileService.Current.CatalogMode, nameof(CatalogModeTag));
            _loadingCatalogMode = false;
        }

        private void SaveSettings()
        {
            AppSettings.Save(
                catalogTimeout: (int)CatalogTimeoutValue,
                checkTimeout:   (int)CheckTimeoutValue);

            ProfileService.Current.NotifyInstallComplete = NotifyInstallComplete;
            ProfileService.Current.NotifyAppUpdates = NotifyAppUpdates;
            ProfileService.Save();
        }

        // ── Уведомления / таймауты ───────────────────────────────────────────────

        private bool _notifyInstallComplete = true;
        public bool NotifyInstallComplete
        {
            get => _notifyInstallComplete;
            set
            {
                if (_notifyInstallComplete == value) return;
                SetField(ref _notifyInstallComplete, value);
                SaveSettings();
            }
        }

        private bool _notifyAppUpdates = true;
        public bool NotifyAppUpdates
        {
            get => _notifyAppUpdates;
            set
            {
                if (_notifyAppUpdates == value) return;
                SetField(ref _notifyAppUpdates, value);
                SaveSettings();
            }
        }

        private double _catalogTimeoutValue = 10;
        public double CatalogTimeoutValue
        {
            get => _catalogTimeoutValue;
            set
            {
                if (_catalogTimeoutValue == value) return;
                SetField(ref _catalogTimeoutValue, value);
                CatalogTimeoutText = $"{(int)value} сек";
                SaveSettings();
            }
        }

        private string _catalogTimeoutText = "10 сек";
        public string CatalogTimeoutText { get => _catalogTimeoutText; private set => SetField(ref _catalogTimeoutText, value); }

        private double _checkTimeoutValue = 15;
        public double CheckTimeoutValue
        {
            get => _checkTimeoutValue;
            set
            {
                if (_checkTimeoutValue == value) return;
                SetField(ref _checkTimeoutValue, value);
                CheckTimeoutText = $"{(int)value} сек";
                SaveSettings();
            }
        }

        private string _checkTimeoutText = "15 сек";
        public string CheckTimeoutText { get => _checkTimeoutText; private set => SetField(ref _checkTimeoutText, value); }

        // ── Установка приложений ──────────────────────────────────────────────────

        private bool _silentInstall;
        public bool SilentInstall
        {
            get => _silentInstall;
            set
            {
                if (_silentInstall == value) return;
                SetField(ref _silentInstall, value);
                ProfileService.Current.SilentInstall = value;
                ProfileService.Save();
            }
        }

        private string _defaultInstallFolderText = "";
        public string DefaultInstallFolderText
        {
            get => _defaultInstallFolderText;
            set => ApplyDefaultInstallFolder(value);
        }

        private string _defaultInstallFolderStatusText = "";
        public string DefaultInstallFolderStatusText { get => _defaultInstallFolderStatusText; private set => SetField(ref _defaultInstallFolderStatusText, value); }

        private void BrowseDefaultInstallFolder()
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description         = "Выберите папку установки приложений по умолчанию",
                ShowNewFolderButton = true
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            ApplyDefaultInstallFolder(dlg.SelectedPath);
        }

        /// <summary>
        /// Сохраняет папку установки в профиль, предварительно прогоняя её через тот же
        /// CommandLineGuard.ValidateInstallFolder, которым пользуется путь winget. Иначе
        /// значение молча отбрасывалось бы только в момент установки, и пользователь
        /// считал бы, что папка задана. Пустая строка допустима — это штатный сброс
        /// к выбору winget по умолчанию.
        /// </summary>
        private void ApplyDefaultInstallFolder(string? path)
        {
            string value = (path ?? "").Trim();

            if (!CommandLineGuard.ValidateInstallFolder(value))
            {
                SetField(ref _defaultInstallFolderText, ProfileService.Current.DefaultInstallFolder, nameof(DefaultInstallFolderText));
                DefaultInstallFolderStatusText =
                    "⚠ Путь не принят: нужен абсолютный локальный путь без сетевых имён и кавычек. Оставлено прежнее значение.";
                return;
            }

            SetField(ref _defaultInstallFolderText, value, nameof(DefaultInstallFolderText));
            ProfileService.Current.DefaultInstallFolder = value;
            ProfileService.Save();

            DefaultInstallFolderStatusText = value.Length == 0
                ? "Папка не задана — winget выбирает её сам."
                : $"Сохранено: {value}";
        }

        // ── Область каталога ───────────────────────────────────────────────────────

        private string _catalogModeTag = "full";
        public string CatalogModeTag
        {
            get => _catalogModeTag;
            set
            {
                if (_loadingCatalogMode || _catalogModeTag == value) return;
                SetField(ref _catalogModeTag, value);
                ProfileService.Current.CatalogMode = value;
                ProfileService.Save();
            }
        }

        // ── Скрытые приложения ─────────────────────────────────────────────────────

        private string _hiddenAppsStatusText = "";
        public string HiddenAppsStatusText { get => _hiddenAppsStatusText; private set => SetField(ref _hiddenAppsStatusText, value); }

        private void UnhideAllApps()
        {
            // Отдельный экземпляр AppManager нарочно: он ничего не держит в памяти
            // кроме файлового состояния (apps.json/alternatives.json/hidden.json).
            var appManager = new AppManager();
            int count = appManager.HiddenAppsCount;
            if (count == 0)
            {
                HiddenAppsStatusText = "Скрытых приложений нет.";
                return;
            }

            appManager.UnhideAllApps();
            HiddenAppsStatusText =
                $"Показано: {count}. Чтобы увидеть их в списке — «Обновить каталог» на вкладке «Каталог» или перезапустите клиент.";
            AppLogger.Write($"👁 Показаны скрытые приложения ({count})");
        }

        // ── Перенос настроек (экспорт/импорт) ─────────────────────────────────────

        private string _transferStatusText = "";
        public string TransferStatusText { get => _transferStatusText; private set => SetField(ref _transferStatusText, value); }

        private void ExportSettings()
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
                TransferStatusText = result.Message;
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

        private void ImportSettings()
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
                TransferStatusText = result.Message;
                AppLogger.Write(result.Success ? $"📥 {result.Message}" : $"❌ {result.Message}");

                if (!result.Success)
                {
                    MessageBox.Show(result.Message, "Импорт настроек",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Обновляем состояние вкладки и оформление по свежим данным сервисов
                LoadSettings();
                LoadOfflineSettings();
                LoadSourceOrderUI();
                SetField(ref _minimizeToTray, ProfileService.Current.MinimizeToTray, nameof(MinimizeToTray));
                ThemeService.Apply(ProfileService.Current.Theme);
                ThemeApplied?.Invoke();
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
