using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class SettingsWindow : Window
    {
        private readonly MainWindow _owner;

        // Программная установка значений в Sync поднимает и SelectionChanged у
        // ComboBox, и Checked/Unchecked у флажков-переключателей — глушим обработчики
        // на время синхронизации, чтобы не было каскада Save/обратной записи того же
        // значения. Раньше флажки слушали Click, который программной установкой не
        // поднимается, и глушить было нечего — но вместе с этим не поднимался он и при
        // переключении через средства автоматизации/экранного диктора (UIA-паттерн
        // Toggle вызывает OnToggle, а не Click): галка вставала в новое положение, а
        // настройка молча не сохранялась. Checked/Unchecked покрывают все три способа.
        private bool _suppressSourceChange;
        private bool _suppressToggleChange;

        // Отчёт последней проверки: по нему работает кнопка «Исправить». Хранится
        // именно отчёт, а не флаг «есть что чинить» — починка обязана применять тот
        // самый манифест, который проверка видела и по которому строила список.
        private ClientIntegrityReport? _lastIntegrityReport;

        // Длинный список расхождений полностью в окно настроек не помещается и не
        // должен: подробности всё равно уходят в журнал лаунчера.
        private const int MaxDisplayedFindings = 100;

        public SettingsWindow(MainWindow owner, bool backgroundUpdates, bool startMinimized,
            bool autostart, bool autoUpdateClient, DownloadSource downloadSource)
        {
            InitializeComponent();
            _owner = owner;
            Sync(backgroundUpdates, startMinimized, autostart, autoUpdateClient, downloadSource);
        }

        // Значения расставляет владелец (в т.ч. из меню трея, пока окно открыто) —
        // обработчики на это время заглушены, иначе Sync тут же записал бы обратно
        // то, что только что прочитал.
        internal void Sync(bool backgroundUpdates, bool startMinimized, bool autostart,
            bool autoUpdateClient, DownloadSource downloadSource)
        {
            _suppressToggleChange = true;
            chkBackgroundUpdates.IsChecked = backgroundUpdates;
            chkStartMinimized.IsChecked    = startMinimized;
            chkAutostart.IsChecked         = autostart;
            rbAutoUpdateManual.IsChecked   = !autoUpdateClient;
            rbAutoUpdateAuto.IsChecked     = autoUpdateClient;
            _suppressToggleChange = false;

            // Порядок пунктов ComboBox совпадает с порядком членов enum DownloadSource:
            // индекс == (int)значение.
            _suppressSourceChange = true;
            cmbDownloadSource.SelectedIndex = (int)downloadSource;
            _suppressSourceChange = false;
        }

        private void ChkBackgroundUpdates_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressToggleChange) return;
            _owner.OnBackgroundUpdatesChanged(chkBackgroundUpdates.IsChecked == true);
        }

        private void ChkStartMinimized_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressToggleChange) return;
            _owner.OnStartMinimizedChanged(chkStartMinimized.IsChecked == true);
        }

        private void ChkAutostart_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressToggleChange) return;
            _owner.OnAutostartChanged(chkAutostart.IsChecked == true);
        }

        // Только Checked: в группе переключателей снятие галки со старого варианта и
        // установка на новый — одно действие пользователя, и обрабатывать его дважды
        // (второй раз с ещё не обновлённым состоянием) незачем.
        private void RbAutoUpdateMode_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressToggleChange) return;
            _owner.OnAutoUpdateClientChanged(rbAutoUpdateAuto.IsChecked == true);
        }

        private void CmbDownloadSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSourceChange) return;
            if (cmbDownloadSource.SelectedIndex < 0) return;
            _owner.OnDownloadSourceChanged((DownloadSource)cmbDownloadSource.SelectedIndex);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// Открывает проводник с выделенным launcher.log. Именно выделение файла, а не
        /// просто папки: рядом лежат краш-отчёты и журналы установки, и «откройте папку
        /// и найдите нужный файл» — заведомо худшая инструкция, чем готовое выделение.
        /// Если журнал ещё не создан (первый запуск, запись отключена ошибкой диска) —
        /// открываем саму папку.
        /// </summary>
        private void BtnOpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(LauncherLog.LogDirectory);

                var psi = System.IO.File.Exists(LauncherLog.LogPath)
                    ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{LauncherLog.LogPath}\"")
                    : new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{LauncherLog.LogDirectory}\"");
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Не удалось открыть папку журнала:\n{ex.Message}\n\nПуть: {LauncherLog.LogDirectory}",
                    "Журнал лаунчера", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // --- Диагностика клиента ---

        // async void обработчик обязан ловить всё сам: необработанное исключение из
        // него уронило бы весь лаунчер, а не только окно настроек.
        private async void BtnCheckIntegrity_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await RunIntegrityCheckAsync();
            }
            catch (Exception ex)
            {
                ShowIntegrityStatus($"Проверка не выполнена: {ex.Message}", "StatusDanger");
            }
            finally
            {
                btnCheckIntegrity.IsEnabled = true;
            }
        }

        private async void BtnRepairIntegrity_Click(object sender, RoutedEventArgs e)
        {
            var report = _lastIntegrityReport;
            if (report == null) return;

            try
            {
                btnRepairIntegrity.IsEnabled = false;
                btnCheckIntegrity.IsEnabled = false;
                ShowIntegrityStatus("Восстановление...", "StatusInfo");

                bool repaired = await _owner.RepairClientIntegrityAsync(report, CancellationToken.None);
                if (!repaired)
                {
                    ShowIntegrityStatus(
                        $"Не удалось исправить: {report.RepairMessage ?? "подробности в журнале лаунчера"}",
                        "StatusDanger");
                    return;
                }

                // Перепроверяем по факту, а не верим успешному применению: «Исправлено»
                // должно означать «сверено заново и совпало», иначе это обещание.
                await RunIntegrityCheckAsync();
                if (_lastIntegrityReport?.Status == ClientIntegrityStatus.Healthy)
                {
                    ShowIntegrityStatus("✅ Исправлено — целостность клиента подтверждена", "StatusSuccess");
                }
            }
            catch (Exception ex)
            {
                ShowIntegrityStatus($"Восстановление не выполнено: {ex.Message}", "StatusDanger");
            }
            finally
            {
                btnCheckIntegrity.IsEnabled = true;
            }
        }

        private async Task RunIntegrityCheckAsync()
        {
            btnCheckIntegrity.IsEnabled = false;
            ResetIntegrityView();
            ShowIntegrityStatus("Проверка...", "StatusInfo");

            var report = await _owner.CheckClientIntegrityAsync(CancellationToken.None);
            _lastIntegrityReport = report;

            if (report == null)
            {
                // null — проверка не состоялась (уже идёт другая операция, отмена или
                // сбой). Причина уже в журнале, здесь незачем её выдумывать заново.
                ShowIntegrityStatus(
                    "Проверка не завершена — подробности в журнале лаунчера", "StatusWarning");
                return;
            }

            RenderIntegrityReport(report);
        }

        private void RenderIntegrityReport(ClientIntegrityReport report)
        {
            // ACL — независимая находка: показывается при любом исходе сверки файлов,
            // в том числе когда сверить не удалось. Только информирование: менять права
            // папки без отдельного осознанного решения пользователя лаунчер не будет.
            txtIntegrityAcl.Visibility = report.AclCompromised ? Visibility.Visible : Visibility.Collapsed;

            switch (report.Status)
            {
                case ClientIntegrityStatus.NotInstalled:
                    ShowIntegrityStatus("Клиент не установлен — проверять нечего", "StatusWarning");
                    break;

                case ClientIntegrityStatus.ManifestUnavailable:
                    ShowIntegrityStatus(
                        "Не удалось проверить целостность — сервер недоступен, попробуйте позже",
                        "StatusWarning");
                    ShowIntegrityDetail(report.Summary);
                    break;

                case ClientIntegrityStatus.CheckFailed:
                    ShowIntegrityStatus("Не удалось прочитать файлы клиента", "StatusDanger");
                    ShowIntegrityDetail(report.Summary);
                    break;

                case ClientIntegrityStatus.ExecutableCorrupted:
                    ShowIntegrityStatus(
                        "Клиент повреждён — переустановите его полностью через обычное обновление",
                        "StatusDanger");
                    ShowIntegrityDetail(report.Summary);
                    break;

                case ClientIntegrityStatus.FullReinstallRecommended:
                    ShowIntegrityStatus(
                        "Слишком много расхождений с опубликованной версией — переустановите клиент " +
                        "полностью через обычное обновление",
                        "StatusDanger");
                    // Доля совпавших файлов и проценты — внутри причины плана.
                    ShowIntegrityDetail(report.Plan?.Reason ?? report.Summary);
                    break;

                case ClientIntegrityStatus.Healthy:
                    ShowIntegrityStatus("✅ Целостность клиента подтверждена, ошибок не найдено", "StatusSuccess");
                    ShowIntegrityDetail(report.Summary);
                    break;

                case ClientIntegrityStatus.RepairAvailable:
                    ShowIntegrityStatus("Найдены расхождения с опубликованной версией", "StatusWarning");
                    ShowIntegrityDetail(report.Summary);
                    ShowFindings(report);
                    btnRepairIntegrity.IsEnabled = true;
                    btnRepairIntegrity.Visibility = Visibility.Visible;
                    break;
            }
        }

        private void ShowFindings(ClientIntegrityReport report)
        {
            if (report.Plan == null) return;

            var lines = new List<string>();
            foreach (var entry in report.Plan.ToDownload)
            {
                lines.Add($"⟳ {entry.Path} — повреждён или отсутствует");
            }
            foreach (string path in report.Plan.ToDelete)
            {
                lines.Add($"✖ {path} — лишний файл, будет удалён");
            }

            int total = lines.Count;
            if (total > MaxDisplayedFindings)
            {
                lines.RemoveRange(MaxDisplayedFindings, total - MaxDisplayedFindings);
                lines.Add($"…и ещё {total - MaxDisplayedFindings}");
            }

            lstIntegrityFindings.ItemsSource = lines;
            pnlIntegrityFindings.Visibility = Visibility.Visible;
        }

        private void ResetIntegrityView()
        {
            txtIntegrityDetail.Visibility = Visibility.Collapsed;
            txtIntegrityAcl.Visibility = Visibility.Collapsed;
            pnlIntegrityFindings.Visibility = Visibility.Collapsed;
            lstIntegrityFindings.ItemsSource = null;
            btnRepairIntegrity.Visibility = Visibility.Collapsed;
            _lastIntegrityReport = null;
        }

        private void ShowIntegrityStatus(string text, string brushKey)
        {
            txtIntegrityStatus.Text = text;
            txtIntegrityStatus.Foreground = (Brush)FindResource(brushKey);
            txtIntegrityStatus.Visibility = Visibility.Visible;
        }

        private void ShowIntegrityDetail(string text)
        {
            txtIntegrityDetail.Text = text;
            txtIntegrityDetail.Visibility = Visibility.Visible;
        }
    }
}
