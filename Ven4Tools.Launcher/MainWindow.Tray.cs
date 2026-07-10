using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        private void CreateTrayIcon()
        {
            try
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Windows.Forms.Application.ExecutablePath);
                _notifyIcon = new NotifyIcon
                {
                    Icon    = icon ?? System.Drawing.SystemIcons.Application,
                    Visible = true,
                    Text    = "Ven4Tools Launcher"
                };

                _trayItemAutostart = new ToolStripMenuItem("Запускать при старте Windows")
                {
                    Checked      = GetAutostart(),
                    CheckOnClick = true
                };
                _trayItemAutostart.CheckedChanged += (s, e) =>
                {
                    _autostart = _trayItemAutostart.Checked;
                    SetAutostart(_autostart);
                    SaveSettings();
                    Dispatcher.Invoke(() => chkAutostart.IsChecked = _autostart);
                };

                _trayItemBgUpdates = new ToolStripMenuItem("Проверять обновления в фоне")
                {
                    Checked      = _backgroundUpdates,
                    CheckOnClick = true
                };
                _trayItemBgUpdates.CheckedChanged += (s, e) =>
                {
                    _backgroundUpdates = _trayItemBgUpdates.Checked;
                    if (_backgroundUpdates)
                        _updateService?.Start();
                    else
                        _updateService?.Stop();
                    SaveSettings();
                    Dispatcher.Invoke(() => chkBackgroundUpdates.IsChecked = _backgroundUpdates);
                };

                var itemAutostart = _trayItemAutostart;
                var itemBgUpdates = _trayItemBgUpdates;

                var contextMenu = new ContextMenuStrip();
                contextMenu.Items.Add("Показать окно", null, (s, e) => Dispatcher.Invoke(ShowWindow));
                contextMenu.Items.Add("Проверить обновления", null, async (s, e) =>
                {
                    await (_updateService?.CheckNowAsync() ?? Task.CompletedTask);
                    // InvokeAsync<Task> возвращает DispatcherOperation<Task> — .Task.Unwrap() даёт inner Task
                    await Dispatcher.InvokeAsync(async () => await CheckForUpdatesAsync()).Task.Unwrap();
                });
                contextMenu.Items.Add("-");
                contextMenu.Items.Add(itemAutostart);
                contextMenu.Items.Add(itemBgUpdates);
                contextMenu.Items.Add("-");
                contextMenu.Items.Add("Выход", null, (s, e) => ExitApplication());

                _notifyIcon.ContextMenuStrip = contextMenu;
                _notifyIcon.DoubleClick      += (s, e) => Dispatcher.Invoke(ShowWindow);
                _notifyIcon.BalloonTipClicked += (s, e) => Dispatcher.Invoke(ShowWindow);
            }
            catch (Exception ex)
            {
                AddLog($"⚠️ Ошибка создания иконки в трее: {ex.Message}");
            }
        }

        private void StartBackgroundService()
        {
            var ver = Assembly.GetExecutingAssembly().GetName().Version;
            var launcherVersion = ver != null
                ? $"{ver.Major}.{ver.Minor}.{ver.Build}"
                : "0.0.0";

            _updateService = new UpdateBackgroundService(launcherVersion, () => _clientPath, AddLog)
            {
                LastNotifiedLauncherVersion = _lastNotifiedLauncherVersion,
                LastNotifiedClientVersion   = _lastNotifiedClientVersion,
                LastNotifiedNotificationId  = _lastNotifiedNotificationId
            };

            _updateService.UpdateAvailable += OnUpdateAvailable;

            _updateService.WingetUpgradeCountChanged += count =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_notifyIcon != null && count > 0)
                        _notifyIcon.Text = $"Ven4Tools [{count} обновл.]";
                    else if (_notifyIcon != null)
                        _notifyIcon.Text = "Ven4Tools Launcher";
                });
            };

            _updateService.NotificationAvailable += notif =>
            {
                Dispatcher.Invoke(() =>
                {
                    _lastNotifiedNotificationId = notif.Id;
                    SaveSettings();

                    _notifyIcon?.ShowBalloonTip(
                        8000,
                        "Ven4Tools",
                        notif.Message,
                        ToolTipIcon.Info);
                    AddLog($"📢 Уведомление: {notif.Message}");
                });
            };

            if (_backgroundUpdates)
                _updateService.Start();
        }

        private void OnUpdateAvailable(string type, UpdateInfo info)
        {
            // Все операции — на UI-потоке: и запись полей, и SaveSettings, и UI-обновления.
            // Это устраняет race condition с ThreadPool-потоком таймера.
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (type == "launcher") _lastNotifiedLauncherVersion = info.LatestVersion ?? "";
                    else                    _lastNotifiedClientVersion   = info.LatestVersion ?? "";
                    SaveSettings();

                    string title = type == "launcher"
                        ? $"Обновление лаунчера {info.LatestVersion}"
                        : $"Новая версия Ven4Tools {info.LatestVersion}";

                    string notes = info.ReleaseNotes ?? "Подробности — в окне лаунчера.";
                    notes = Regex.Replace(notes, @"[#*`\-]", "").Trim();
                    if (notes.Length > 250) notes = notes.Substring(0, 247) + "...";

                    AddLog($"🔔 {title}");
                    AddLog($"   {notes.Replace('\n', ' ')}");

                    _notifyIcon?.ShowBalloonTip(
                        8000,
                        title,
                        $"v{info.CurrentVersion} → v{info.LatestVersion}\n\n{notes}",
                        ToolTipIcon.Info);

                    if (type == "launcher")
                        btnInstallUpdate.Visibility = Visibility.Visible;
                });
            }
            catch { } // Dispatcher может быть выключен при завершении приложения
        }

        private void ShowWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();

            // Отложенная (при автозапуске в трее) установка компонентов из setup:
            // окно теперь видимо, поэтому UAC-диалоги и прогресс увидит пользователь.
            // Fire-and-forget — не блокируем показ окна; метод сам обрабатывает ошибки.
            if (_pendingSetupComponents)
            {
                _pendingSetupComponents = false;
                _ = ProcessSetupComponentRequestsAsync();
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // Крестик прячет окно в трей только если включена соответствующая настройка
            if (_minimizeToTray)
            {
                e.Cancel = true;
                Hide();
            }
            else
            {
                ExitApplication();
            }
        }

        private void ExitApplication()
        {
            _watchdog?.Dispose();
            _updateService?.Dispose();
            _notifyIcon?.Dispose();
            try { _clientProcess?.Dispose(); } catch { }
            _clientProcess = null;
            System.Windows.Application.Current.Shutdown();
        }

        // Лаунчер может часами жить в трее (фоновая служба проверяет обновления
        // раз в 3 часа) — без обрезки txtLog рос бы неограниченно на всю сессию.
        private const int MaxLogLines = 1000;

        private void AddLog(string message)
        {
            Dispatcher.Invoke(() =>
            {
                txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                if (txtLog.LineCount > MaxLogLines)
                {
                    int cutIndex = txtLog.GetCharacterIndexFromLineIndex(txtLog.LineCount - MaxLogLines);
                    txtLog.Text = txtLog.Text.Substring(cutIndex);
                }
                txtLog.ScrollToEnd();
            });
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            ExitApplication();
        }
    }
}
