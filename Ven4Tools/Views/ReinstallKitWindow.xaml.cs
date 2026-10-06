using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Views
{
    /// <summary>
    /// Окно «Перед переустановкой Windows»: собирает на флешку драйверы, профили Wi-Fi
    /// и список программ этого компьютера (см. <see cref="ReinstallKitBuilder"/>).
    /// Систему не меняет — только читает и складывает в выбранную папку.
    /// </summary>
    public partial class ReinstallKitWindow : Window
    {
        private const string KitFolderName = "Ven4Tools-Kit";

        private CancellationTokenSource? _cts;
        private string? _builtFolder;

        public ReinstallKitWindow()
        {
            InitializeComponent();
            txtKitFolder.Text = SuggestFolder();
            txtKitAccount.Text = SuggestAccountName();
            Closed += (_, _) => _cts?.Cancel();
        }

        /// <summary>Имя нынешнего пользователя, если оно годится для новой учётной записи.</summary>
        private static string SuggestAccountName()
        {
            string current = Environment.UserName;
            return WindowsAnswerFileBuilder.ValidateAccountName(current) == null ? current : "User";
        }

        /// <summary>Настройки файла ответов — такие же, как на этом компьютере.</summary>
        private static WindowsAnswerFileBuilder.Options AnswerFileOptions(string account, string kitRelativePath)
        {
            string locale = System.Globalization.CultureInfo.CurrentCulture.Name;
            if (locale.Length == 0) locale = "ru-RU";

            var keyboards = new List<string>();
            try
            {
                foreach (System.Windows.Forms.InputLanguage language in System.Windows.Forms.InputLanguage.InstalledInputLanguages)
                    if (!keyboards.Contains(language.Culture.Name, StringComparer.OrdinalIgnoreCase))
                        keyboards.Add(language.Culture.Name);
            }
            catch { /* список раскладок недоступен — берём язык системы */ }
            if (keyboards.Count == 0) keyboards.Add(locale);

            string architecture =
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                    ? "arm64" : "amd64";

            return new WindowsAnswerFileBuilder.Options(
                account, kitRelativePath, locale, keyboards, TimeZoneInfo.Local.Id, architecture);
        }

        /// <summary>
        /// Открывает окно поверх главного. Владелец ищется по типу, а не берётся из
        /// <see cref="Application.MainWindow"/>: после закрытия заставки это свойство
        /// пусто, и WPF записывает в него первое же созданное окно — то есть само это
        /// окно, а назначить окно владельцем самому себе нельзя.
        /// </summary>
        public static void ShowFor(Application? application)
        {
            var window = new ReinstallKitWindow();
            var owner = application?.Windows.OfType<MainWindow>().FirstOrDefault();
            if (owner != null) window.Owner = owner;
            else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.ShowDialog();
        }

        /// <summary>Первая подключённая флешка, а если её нет — «Документы».</summary>
        private static string SuggestFolder()
        {
            try
            {
                var removable = DriveInfo.GetDrives()
                    .FirstOrDefault(d => d.DriveType == DriveType.Removable && d.IsReady);
                if (removable != null) return Path.Combine(removable.RootDirectory.FullName, KitFolderName);
            }
            catch { /* диск исчез во время опроса — берём «Документы» */ }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), KitFolderName);
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Папка набора «Перед переустановкой»",
                ShowNewFolderButton = true
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                txtKitFolder.Text = dialog.SelectedPath;
        }

        private async void Build_Click(object sender, RoutedEventArgs e)
        {
            string folder = txtKitFolder.Text.Trim();
            if (folder.Length == 0)
            {
                ShowStatus("Укажите папку набора.", error: true);
                return;
            }
            var options = new ReinstallKitBuilder.Options(
                folder, chkKitDrivers.IsChecked == true, chkKitWifi.IsChecked == true, chkKitApps.IsChecked == true);
            if (!options.Drivers && !options.Wifi && !options.Apps)
            {
                ShowStatus("Отметьте хотя бы один пункт.", error: true);
                return;
            }

            bool answerFile = chkKitAnswerFile.IsChecked == true;
            string account = txtKitAccount.Text.Trim();
            if (answerFile && WindowsAnswerFileBuilder.ValidateAccountName(account) is { } accountError)
            {
                ShowStatus(accountError, error: true);
                return;
            }

            SetBusy(true);
            _cts = new CancellationTokenSource();
            try
            {
                var progress = new Progress<string>(text => ShowStatus("⏳ " + text, error: false));

                var catalogApps = new List<(string Id, string Name, string WingetId)>();
                var installed = new List<KitInstalledProgram>();
                if (options.Apps)
                {
                    ShowStatus("⏳ Список установленных программ…", error: false);
                    (catalogApps, installed) = await CollectProgramsAsync();
                }

                var result = await ReinstallKitBuilder.BuildAsync(
                    options, catalogApps, installed, AppContext.BaseDirectory,
                    KitCommandRunner.Default, progress, _cts.Token, ExportWingetAsync);

                _builtFolder = Path.GetFullPath(folder);
                btnOpenKitFolder.IsEnabled = true;

                string summary = Describe(result);
                if (answerFile)
                {
                    try
                    {
                        string written = WindowsAnswerFileBuilder.WriteToDriveRoot(
                            folder, kitPath => AnswerFileOptions(account, kitPath));
                        summary += $"\n\nФайл ответов: {written}. Установка Windows создаст учётную запись «{account}» без пароля " +
                                   "и после первого входа запустит набор. Пароль задайте сами после входа.";
                        AppLogger.Write($"💾 Файл ответов для установки Windows записан: {written}");
                    }
                    catch (Exception ex)
                    {
                        summary += $"\n\n⚠️ Файл ответов не записан: {ex.Message}";
                        AppLogger.Write($"⚠️ Файл ответов для установки Windows не записан: {ex.Message}");
                    }
                }
                ShowStatus(summary, error: false);
                AppLogger.Write($"💾 Набор «Перед переустановкой» собран: драйверов {result.Drivers?.ToString() ?? "—"}, " +
                                $"профилей Wi-Fi {result.WifiProfiles?.ToString() ?? "—"}, программ каталога {result.CatalogApps?.ToString() ?? "—"}");
            }
            catch (OperationCanceledException)
            {
                // Окно закрыли во время сборки — сообщать уже некому.
            }
            catch (Exception ex)
            {
                ShowStatus($"❌ Набор не собран: {ex.Message}", error: true);
                AppLogger.Write($"❌ Набор «Перед переустановкой» не собран: {ex.Message}");
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
                SetBusy(false);
            }
        }

        /// <summary>
        /// Что стоит на компьютере: программы каталога (их вернёт файл ответа) и всё,
        /// что видит winget (по нему строится список «поставить вручную»).
        /// </summary>
        private static async Task<(List<(string Id, string Name, string WingetId)> Catalog, List<KitInstalledProgram> Installed)>
            CollectProgramsAsync()
        {
            var (_, output) = await WingetRunner.RunAsync($"list {WingetArgs.NonInteractiveLine}");
            var rows = InstalledViewModel.ParseWingetList(output);
            var installedIds = new HashSet<string>(rows.Select(r => r.WingetId), StringComparer.OrdinalIgnoreCase);

            var catalog = CatalogLoaderService.State.UsableCatalog;
            var catalogApps = catalog == null
                ? new List<(string, string, string)>()
                : catalog.Apps
                    .Where(a => !string.IsNullOrWhiteSpace(a.WingetId) && installedIds.Contains(a.WingetId))
                    .Select(a => (a.Id, a.Name, a.WingetId))
                    .ToList();

            return (catalogApps, rows.Select(r => new KitInstalledProgram(r.Name, r.WingetId, r.Source)).ToList());
        }

        private static async Task<bool> ExportWingetAsync(string path)
        {
            try
            {
                var (code, _) = await WingetRunner.RunAsync($"export -o \"{path}\" {WingetArgs.NonInteractiveLine}");
                return code == 0 && File.Exists(path);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[Набор] winget export не выполнен: {ex.Message}");
                return false;
            }
        }

        private static string Describe(ReinstallKitBuilder.Result result)
        {
            var text = new StringBuilder("✅ Набор собран.\n");
            if (result.Drivers is { } drivers)
                text.Append($"\nДрайверы: {drivers} ({result.DriversBytes / 1024 / 1024} МБ)");
            if (result.WifiProfiles is { } wifi)
                text.Append(wifi > 0 ? $"\nПрофили Wi-Fi: {wifi}" : "\nПрофили Wi-Fi: на этом компьютере их нет");
            if (result.CatalogApps is { } apps)
                text.Append($"\nПрограммы: вернутся сами — {apps}, через winget — {result.WingetOnly}, вручную — {result.Manual}");
            foreach (string warning in result.Warnings)
                text.Append("\n⚠️ " + warning);
            text.Append($"\n\nПосле переустановки Windows откройте {ReinstallKitBuilder.LauncherFileName} из папки набора.");
            return text.ToString();
        }

        private void SetBusy(bool busy)
        {
            btnBuildKit.IsEnabled = !busy;
            btnKitBrowse.IsEnabled = !busy;
            txtKitFolder.IsEnabled = !busy;
            chkKitDrivers.IsEnabled = !busy;
            chkKitWifi.IsEnabled = !busy;
            chkKitApps.IsEnabled = !busy;
            chkKitAnswerFile.IsEnabled = !busy;
            txtKitAccount.IsEnabled = !busy;
        }

        private void ShowStatus(string text, bool error)
        {
            txtKitStatus.Text = text;
            txtKitStatus.Foreground = (System.Windows.Media.Brush)FindResource(error ? "StatusDanger" : "TextPrimary");
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_builtFolder == null) return;
            try
            {
                Process.Start(new ProcessStartInfo(TrustedExecutablePaths.ExplorerExe) { ArgumentList = { _builtFolder } });
            }
            catch (Exception ex)
            {
                ShowStatus($"Не удалось открыть папку: {ex.Message}", error: true);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
