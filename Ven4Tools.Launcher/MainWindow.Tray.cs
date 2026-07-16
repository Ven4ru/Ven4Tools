using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;
using Ven4Tools.Shared;

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
                    Dispatcher.Invoke(SyncSettingsWindow);
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
                    Dispatcher.Invoke(SyncSettingsWindow);
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
                    else
                        _ = TriggerAutoClientUpdateAsync(info.LatestVersion ?? "");
                });
            }
            catch { } // Dispatcher может быть выключен при завершении приложения
        }

        // Тихое обновление клиента при включённом автоматическом режиме. Список версий
        // на момент фонового обнаружения (UpdateBackgroundService.CheckClientAsync)
        // мог не содержать CDN-подстановки — перезагружаем тем же путём, что и ручная
        // проверка, чтобы получить актуальный ClientVersionInfo с FallbackUrl/ExpectedSha256.
        private async Task TriggerAutoClientUpdateAsync(string latestVersion)
        {
            if (!_autoUpdateClient) return;
            if (_downloadCts != null) return; // уже идёт другая загрузка — попробуем на следующем тике

            // Тихое автообновление не должно показывать модальные диалоги «ниоткуда»,
            // пока лаунчер свёрнут в трей. Если клиент запущен — переустановить его
            // файлы нельзя, а спрашивать разрешение закрыть его блокирующим окном в
            // фоне — плохой UX. Откладываем до следующего тика фоновой проверки или до
            // ручного запуска пользователем (DownloadVersionAsync тоже страхует от
            // гонки: клиент мог запуститься между этой проверкой и установкой).
            if (IsClientRunning())
            {
                AddLog($"ℹ️ Автообновление клиента до {latestVersion} отложено: клиент запущен");
                return;
            }

            await LoadVersionsAsync();
            if (_downloadCts != null) return; // за время перезагрузки списка мог стартовать ручной клик — не гоняем вторую параллельную установку

            var match = _availableVersions.FirstOrDefault(v => v.Version == latestVersion);
            if (match == null)
            {
                AddLog($"⚠️ Автообновление: версия {latestVersion} не найдена в свежем списке — пропуск");
                return;
            }

            AddLog($"🤖 Автоматическое обновление клиента до {latestVersion}...");
            _downloadCts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await DownloadVersionAsync(match, _downloadCts.Token, silent: true);
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

        // Явное управление индикатором «Ход операции». Раньше стадии определялись по
        // ключевым словам в произвольном тексте лога (AddLog), из-за чего обычные
        // сообщения старта («🔧 Проверка компонентов…», «⚠️ Найдены проблемы…»)
        // ложно зажигали стадии 2-4 без единой реальной загрузки. Теперь стадии
        // выставляются напрямую из конвейера скачивания клиента (DownloadVersionAsync).
        // stage: 0 — сброс (все стадии неактивны), 1..5 — сколько шагов подсвечено
        // (1 Загрузка, 2 +Проверка, 3 +Распаковка, 4 +Установка, 5 Готово).
        private void SetOperationStage(int stage)
        {
            var stages = new[] { stageDownload, stageVerify, stageExtract, stageInstall, stageDone };
            var active = (System.Windows.Media.Brush)FindResource("BrandGreen");
            var pending = (System.Windows.Media.Brush)FindResource("SurfaceRaised");
            var border = (System.Windows.Media.Brush)FindResource("BorderBrush");
            for (int i = 0; i < stages.Length; i++)
            {
                bool lit = i < stage;
                stages[i].Background = lit ? active : pending;
                stages[i].BorderBrush = lit ? active : border;
                stages[i].Opacity = lit ? 1 : 0.72;
            }
            if (stage >= 1 && stage <= stages.Length)
                MotionService.Pulse(stages[Math.Clamp(stage - 1, 0, stages.Length - 1)], 1.12, 180);
        }

        private void BtnExit_Click(object sender, RoutedEventArgs e)
        {
            ExitApplication();
        }
    }
}
