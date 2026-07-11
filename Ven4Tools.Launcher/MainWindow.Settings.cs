using System;
using System.IO;
using System.Windows;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<LauncherSettings>(json);
                    if (settings != null)
                    {
                        _minimizeToTray              = settings.MinimizeToTray;
                        _installPath                 = settings.InstallPath ?? "";
                        _backgroundUpdates           = settings.BackgroundUpdates;
                        _autostart                   = settings.Autostart;
                        _startMinimized              = settings.StartMinimized;
                        _autoUpdateClient            = settings.AutoUpdateClient;
                        _lastNotifiedLauncherVersion = settings.LastNotifiedLauncherVersion ?? "";
                        _lastNotifiedClientVersion   = settings.LastNotifiedClientVersion   ?? "";
                        _lastNotifiedNotificationId  = settings.LastNotifiedNotificationId  ?? "";
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var settings = new
                {
                    MinimizeToTray              = _minimizeToTray,
                    InstallPath                 = _installPath,
                    BackgroundUpdates           = _backgroundUpdates,
                    Autostart                   = _autostart,
                    StartMinimized              = _startMinimized,
                    AutoUpdateClient            = _autoUpdateClient,
                    LastNotifiedLauncherVersion = _lastNotifiedLauncherVersion,
                    LastNotifiedClientVersion   = _lastNotifiedClientVersion,
                    LastNotifiedNotificationId  = _lastNotifiedNotificationId
                };
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(settings, Newtonsoft.Json.Formatting.Indented);
                // Атомарная запись: сначала во временный файл, затем замена.
                // Так настройки не побьются при сбое в момент записи.
                string tmp = _settingsPath + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(_settingsPath))
                    File.Replace(tmp, _settingsPath, null);
                else
                    File.Move(tmp, _settingsPath);
            }
            catch { }
        }

        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            if (_settingsWindow == null || !_settingsWindow.IsLoaded)
            {
                _settingsWindow = new SettingsWindow(
                    this, _backgroundUpdates, _startMinimized, _autostart, _autoUpdateClient)
                {
                    Owner = this
                };
                _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            }
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }

        // Значения могут поменяться из контекстного меню трея, пока окно настроек
        // открыто — держим его в курсе.
        private void SyncSettingsWindow() =>
            _settingsWindow?.Sync(_backgroundUpdates, _startMinimized, _autostart, _autoUpdateClient);

        internal void OnBackgroundUpdatesChanged(bool isChecked)
        {
            _backgroundUpdates = isChecked;
            if (_trayItemBgUpdates != null) _trayItemBgUpdates.Checked = _backgroundUpdates;
            if (_backgroundUpdates)
                _updateService?.Start();
            else
                _updateService?.Stop();
            SaveSettings();
        }

        internal void OnStartMinimizedChanged(bool isChecked)
        {
            _startMinimized = isChecked;
            SaveSettings();
        }

        internal void OnAutostartChanged(bool isChecked)
        {
            _autostart = isChecked;
            if (_trayItemAutostart != null) _trayItemAutostart.Checked = _autostart;
            if (!_isUiTestMode)
                SetAutostart(_autostart);
            SaveSettings();
        }

        internal void OnAutoUpdateClientChanged(bool isChecked)
        {
            _autoUpdateClient = isChecked;
            SaveSettings();
        }

        private static bool GetAutostart()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
                return key?.GetValue("Ven4Tools.Launcher") != null;
            }
            catch { return false; }
        }

        private static void SetAutostart(bool enable)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (key == null) return;

                if (enable)
                {
                    string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
                    if (string.IsNullOrEmpty(exe)) return; // single-file publish — MainModule может быть null
                    key.SetValue("Ven4Tools.Launcher", $"\"{exe}\"");
                }
                else
                {
                    key.DeleteValue("Ven4Tools.Launcher", throwOnMissingValue: false);
                }
            }
            catch { }
        }
    }
}
