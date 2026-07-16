using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class NetworkTab : UserControl
    {
        private bool _busy = false;

        public NetworkTab()
        {
            InitializeComponent();

            btnRunAll.Click          += async (_, _) => await RunAllAsync();
            btnRefreshAdapters.Click += (_, _) => RefreshAdapters();
            btnPing.Click            += async (_, _) => await RunPingAsync();
            btnCheckServices.Click   += async (_, _) => await RunServicesAsync();
            btnGetIp.Click           += async (_, _) => await RunGetIpAsync();
            btnCheckDns.Click        += async (_, _) => await RunDnsAsync();
            btnResetNetwork.Click    += async (_, _) => await RunResetNetworkAsync();

            Loaded += (_, _) => RefreshAdapters();
        }

        // ── Полная диагностика ────────────────────────────────────────────────

        private async Task RunAllAsync()
        {
            if (_busy) return;
            _busy = true;
            btnRunAll.IsEnabled = false;
            btnRunAll.Content = "⏳ Диагностика...";
            try
            {
                RefreshAdapters();
                await RunPingAsync();
                await RunServicesAsync();
                await RunGetIpAsync();
            }
            finally
            {
                _busy = false;
                btnRunAll.IsEnabled = true;
                btnRunAll.Content = "🔍 Запустить полную диагностику";
            }
        }

        // ── Адаптеры ─────────────────────────────────────────────────────────

        private void RefreshAdapters()
        {
            var adapters = DiagnosticsService.GetAdapters();
            lstAdapters.ItemsSource = adapters;
            txtAdaptersEmpty.Visibility = adapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AppLogger.Write($"[Сеть] Адаптеров: {adapters.Count}");
        }

        // ── Пинг ─────────────────────────────────────────────────────────────

        private async Task RunPingAsync()
        {
            btnPing.IsEnabled = false;
            SetPingRow(txtPing1, txtPingIcon1, "...", null);
            SetPingRow(txtPing2, txtPingIcon2, "...", null);
            SetPingRow(txtPing3, txtPingIcon3, "...", null);
            SetPingRow(txtPing4, txtPingIcon4, "...", null);

            var hosts = new[] { "1.1.1.1", "8.8.8.8", "google.com", "ven4tools.ru" };
            var targets = new[] {
                (txtPing1, txtPingIcon1), (txtPing2, txtPingIcon2),
                (txtPing3, txtPingIcon3), (txtPing4, txtPingIcon4)
            };

            var tasks = new List<Task>();
            for (int i = 0; i < hosts.Length; i++)
            {
                var host = hosts[i];
                var (ms, icon) = targets[i];
                tasks.Add(Task.Run(async () =>
                {
                    var r = await DiagnosticsService.PingHostAsync(host);
                    Dispatcher.Invoke(() => SetPingRow(ms, icon, r.Display, r.Reachable));
                    AppLogger.Write($"[Сеть] Пинг {host}: {r.Display}");
                }));
            }
            await Task.WhenAll(tasks);
            btnPing.IsEnabled = true;
        }

        private static void SetPingRow(TextBlock txt, TextBlock icon, string ms, bool? ok)
        {
            txt.Text = ms;
            if (ok == null)  { icon.Text = "⬜"; icon.Foreground = Brushes.Gray; return; }
            if (ok == true)  { icon.Text = "✅"; icon.Foreground = new SolidColorBrush(Color.FromRgb(74,222,128)); }
            else             { icon.Text = "❌"; icon.Foreground = new SolidColorBrush(Colors.LightCoral); }
        }

        // ── Доступность сервисов ──────────────────────────────────────────────

        private async Task RunServicesAsync()
        {
            btnCheckServices.IsEnabled = false;
            var icons = new[] { txtSvc1, txtSvc2, txtSvc3, txtSvc4, txtSvc5 };
            var mstxts = new[] { txtSvcMs1, txtSvcMs2, txtSvcMs3, txtSvcMs4, txtSvcMs5 };
            foreach (var i in icons) { i.Text = "⏳"; i.Foreground = Brushes.Gray; }

            var checks = new[]
            {
                ("Google",     "https://www.google.com"),
                ("YouTube",    "https://www.youtube.com"),
                ("Discord",    "https://discord.com"),
                ("Cloudflare", "https://www.cloudflare.com"),
                ("GitHub",     "https://github.com"),
            };

            var tasks = new List<Task>();
            for (int i = 0; i < checks.Length; i++)
            {
                var (name, url) = checks[i];
                var ic = icons[i]; var ms = mstxts[i];
                tasks.Add(Task.Run(async () =>
                {
                    var r = await DiagnosticsService.CheckServiceAsync(name, url);
                    Dispatcher.Invoke(() =>
                    {
                        ic.Text = r.Available ? "✅" : "❌";
                        ic.Foreground = r.Available
                            ? new SolidColorBrush(Color.FromRgb(74,222,128))
                            : new SolidColorBrush(Colors.LightCoral);
                        ms.Text = r.Available ? $"{r.Ms} мс" : "таймаут";
                    });
                    AppLogger.Write($"[Сеть] {name}: {(r.Available ? "✅" : "❌")} {r.Ms}мс");
                }));
            }
            await Task.WhenAll(tasks);
            btnCheckServices.IsEnabled = true;
        }

        // ── Внешний IP ───────────────────────────────────────────────────────

        private async Task RunGetIpAsync()
        {
            btnGetIp.IsEnabled = false;
            txtPublicIp.Text = "определяется...";
            try
            {
                var ip = await DiagnosticsService.GetPublicIpAsync();
                txtPublicIp.Text = ip;
                AppLogger.Write($"[Сеть] Внешний IP: {ip}");
            }
            finally { btnGetIp.IsEnabled = true; }
        }

        // ── DNS ──────────────────────────────────────────────────────────────

        private async Task RunDnsAsync()
        {
            btnCheckDns.IsEnabled = false;
            txtDnsResult.Visibility = Visibility.Visible;
            txtDnsResult.Text = "Проверка DNS...";
            try
            {
                var result = await DiagnosticsService.CheckDnsAsync("google.com");
                txtDnsResult.Text = result;
                AppLogger.Write("[Сеть] DNS проверка завершена");
            }
            catch (Exception ex) { txtDnsResult.Text = $"Ошибка: {ex.Message}"; }
            finally { btnCheckDns.IsEnabled = true; }
        }

        // ── Сброс сети ───────────────────────────────────────────────────────

        private async Task RunResetNetworkAsync()
        {
            var confirm = MessageBox.Show(
                "Сброс сетевых настроек:\n\n" +
                "• netsh winsock reset\n• netsh int ip reset\n• ipconfig /release\n• ipconfig /renew\n\n" +
                "Потребуются права администратора и перезагрузка.\n\nПродолжить?",
                "Сброс сети", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            btnResetNetwork.IsEnabled = false;
            try
            {
                AppLogger.Write("[Сеть] Запуск сброса сетевых настроек...");
                // Приложение уже работает с правами администратора (перезапуск через UAC
                // в MainWindow), поэтому runas не нужен — запускаем скрыто и перенаправляем
                // вывод команд в лог-панель вместо отдельного окна консоли.
                var psi = new ProcessStartInfo
                {
                    FileName  = TrustedExecutablePaths.CmdExe,
                    Arguments = "/c netsh winsock reset & netsh int ip reset & " +
                                "ipconfig /release & ipconfig /renew",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    WindowStyle            = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true
                };
                int exitCode = -1;
                using var p = Process.Start(psi);
                if (p != null)
                {
                    var stdoutTask = p.StandardOutput.ReadToEndAsync();
                    var stderrTask = p.StandardError.ReadToEndAsync();
                    await p.WaitForExitAsync();
                    exitCode = p.ExitCode;

                    // Выводим результат команд в лог-панель построчно
                    foreach (var line in (await stdoutTask).Split('\n'))
                    {
                        var t = line.Trim();
                        if (!string.IsNullOrWhiteSpace(t)) AppLogger.Write($"[Сеть] {t}");
                    }
                    var err = (await stderrTask).Trim();
                    if (!string.IsNullOrWhiteSpace(err)) AppLogger.Write($"[Сеть] ⚠ {err}");
                }

                // Проверяем код выхода: цепочка команд через «&» возвращает код последней,
                // ненулевой код означает, что часть сброса не удалась (нет прав, DHCP и т.п.).
                if (exitCode == 0)
                {
                    AppLogger.Write("[Сеть] Сброс завершён");
                    MessageBox.Show("Перезагрузите компьютер для применения изменений.",
                        "Готово", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    AppLogger.Write($"[Сеть] ⚠ Сброс завершился с кодом {exitCode} — часть команд могла не выполниться");
                    MessageBox.Show(
                        $"Сброс сетевых настроек завершился с ошибкой (код {exitCode}). Часть команд могла не выполниться.\n\n" +
                        "Запустите приложение от имени администратора и попробуйте ещё раз. Подробности — в логах.",
                        "Сброс не завершён", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Сеть] Ошибка сброса: {ex.Message}");
                MessageBox.Show("Не удалось сбросить сетевые настройки. Запустите приложение от имени администратора и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { btnResetNetwork.IsEnabled = true; }
        }
    }
}
