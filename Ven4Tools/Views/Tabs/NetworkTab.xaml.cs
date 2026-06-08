using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class NetworkTab : UserControl
    {
        private ZapretService? _zapretService;
        private bool _isZapretActionInProgress = false;
        private bool _initialized = false;
        private Action? _sessionChangedHandler;

        public NetworkTab()
        {
            InitializeComponent();
            _zapretService = new ZapretService(AddLog);

            _sessionChangedHandler = () => Dispatcher.Invoke(UpdateAuthState);
            Loaded += (_, _) =>
            {
                UserSession.Changed += _sessionChangedHandler;
                if (!_initialized)
                {
                    CheckZapretStatus();
                    _initialized = true;
                }
                UpdateAuthState();
            };
            Unloaded += (_, _) => UserSession.Changed -= _sessionChangedHandler;
        }

        private void UpdateAuthState()
        {
            pnlZapretAuth.Visibility = UserSession.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        }
        
        private static void AddLog(string message) => AppLogger.Write(message);
        
        private void CheckZapretStatus()
        {
            if (_zapretService?.IsInstalled == true)
            {
                txtZapretStatus.Text = "✅ Установлен";
                btnInstallZapret.Visibility = Visibility.Collapsed;
                btnZapretActions.Visibility = Visibility.Visible;
                AddLog("✅ Zapret обнаружен в системе");
            }
            else
            {
                txtZapretStatus.Text = "❌ Не установлен";
                btnInstallZapret.Visibility = Visibility.Visible;
                btnZapretActions.Visibility = Visibility.Collapsed;
            }
        }
        
        private async void BtnInstallZapret_Click(object sender, RoutedEventArgs e)
        {
            if (_isZapretActionInProgress) return;
            
            var warning1 = MessageBox.Show(
                "⚠️ ВАЖНОЕ ПРЕДУПРЕЖДЕНИЕ\n\n" +
                "Zapret устанавливает системный драйвер WinDivert, который работает на уровне ядра Windows.\n\n" +
                "Возможные риски:\n" +
                "• Синие экраны смерти (BSOD)\n" +
                "• Конфликты с антивирусами, античитами, VPN\n" +
                "• Срабатывание антивирусов\n\n" +
                "Ven4Tools только скачивает и распаковывает файлы.\n" +
                "Все риски и ответственность — полностью на вас.\n\n" +
                "Продолжить?",
                "Предупреждение",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            
            if (warning1 != MessageBoxResult.Yes) return;
            
            var warning2 = MessageBox.Show(
                "Вы уверены?\n\n" +
                "Zapret — инструмент для продвинутых пользователей.\n" +
                "Если вы не понимаете, что делает драйвер WinDivert — откажитесь.\n\n" +
                "Продолжить?",
                "Подтверждение",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            
            if (warning2 != MessageBoxResult.Yes) return;
            
            _isZapretActionInProgress = true;
            btnInstallZapret.IsEnabled = false;
            txtZapretState.Text = "Установка...";
            
            try
            {
                AddLog("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                AddLog("📥 Начинаем установку zapret...");
                
                bool success = await _zapretService!.InstallAsync();
                
                if (success)
                {
                    AddLog("✅ Zapret успешно установлен!");
                    txtZapretState.Text = "Готово";
                    CheckZapretStatus();
                    
                    var openMenu = MessageBox.Show(
                        "✅ Zapret установлен!\n\n" +
                        "Открыть меню service.bat для настройки?",
                        "Установка завершена",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    
                    if (openMenu == MessageBoxResult.Yes)
                    {
                        _zapretService.OpenServiceMenu();
                    }
                }
                else
                {
                    AddLog("❌ Ошибка установки zapret");
                    txtZapretState.Text = "Ошибка установки";
                }
            }
            catch (Exception ex)
            {
                AddLog($"❌ Исключение: {ex.Message}");
                txtZapretState.Text = "Ошибка";
            }
            finally
            {
                _isZapretActionInProgress = false;
                btnInstallZapret.IsEnabled = true;
            }
        }
        
        private void BtnOpenServiceMenu_Click(object sender, RoutedEventArgs e)
        {
            if (_isZapretActionInProgress) return;
            _zapretService!.OpenServiceMenu();
            txtZapretState.Text = "Открыто меню";
            AddLog("📟 Открыто меню service.bat");
        }
        
        private void BtnOpenZapretFolder_Click(object sender, RoutedEventArgs e)
        {
            _zapretService!.OpenInstallFolder();
            txtZapretState.Text = "Папка открыта";
            AddLog($"📁 Открыта папка: {_zapretService.InstallPath}");
        }
        
        private void BtnOpenDocumentation_Click(object sender, RoutedEventArgs e)
        {
            _zapretService!.OpenDocumentation();
            txtZapretState.Text = "Документация открыта";
            AddLog("📖 Открыта документация");
        }
        
        private async void BtnRemoveZapret_Click(object sender, RoutedEventArgs e)
        {
            if (_isZapretActionInProgress) return;
            
            var confirm = MessageBox.Show(
                "Вы действительно хотите удалить zapret?\n\n" +
                "Будут удалены:\n" +
                "• Все файлы zapret\n" +
                "• Службы, связанные с zapret\n\n" +
                "⚠️ Для полного удаления драйвера потребуется перезагрузка.\n\n" +
                "Продолжить?",
                "Удаление zapret",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            
            if (confirm != MessageBoxResult.Yes) return;
            
            _isZapretActionInProgress = true;
            btnRemoveZapret.IsEnabled = false;
            txtZapretState.Text = "Удаление...";
            
            try
            {
                AddLog("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
                AddLog("🗑️ Удаление zapret...");
                
                await _zapretService!.RemoveAsync();
                CheckZapretStatus();
                txtZapretState.Text = "Готово";
                
                AddLog("✅ Zapret удалён");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка удаления: {ex.Message}");
                txtZapretState.Text = "Ошибка";
            }
            finally
            {
                _isZapretActionInProgress = false;
                btnRemoveZapret.IsEnabled = true;
            }
        }
        
        private async void BtnCheckDns_Click(object sender, RoutedEventArgs e)
        {
            AddLog("🔍 Проверка DNS (nslookup google.com)...");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "nslookup",
                    Arguments = "google.com",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using var process = Process.Start(psi);
                if (process == null) { AddLog("❌ Не удалось запустить nslookup"); return; }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask  = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(outputTask, errorTask);
                await process.WaitForExitAsync();
                string output = outputTask.Result;
                string error  = errorTask.Result;

                foreach (var line in (output + error).Split('\n'))
                {
                    string trimmed = line.Trim('\r', ' ');
                    if (!string.IsNullOrWhiteSpace(trimmed))
                        AddLog($"   {trimmed}");
                }

                AddLog(process.ExitCode == 0 ? "✅ DNS работает" : "⚠️ Возможны проблемы с DNS");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка DNS-проверки: {ex.Message}");
            }
        }

        private async void BtnResetNetwork_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Сброс сетевых настроек:\n\n" +
                "• netsh winsock reset\n" +
                "• netsh int ip reset\n" +
                "• ipconfig /release\n" +
                "• ipconfig /renew\n\n" +
                "Потребуются права администратора и перезагрузка.\n\n" +
                "Продолжить?",
                "Сброс сети",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                AddLog("🔄 Запуск сброса сетевых настроек с правами администратора...");

                // Run elevated; /c runs all commands, pause lets user read before window closes
                var psi = new ProcessStartInfo
                {
                    FileName  = "cmd.exe",
                    Arguments = "/c netsh winsock reset & netsh int ip reset & " +
                                "ipconfig /release & ipconfig /renew & " +
                                "echo. & echo Готово. Нажмите любую клавишу... & pause > nul",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Normal
                };

                using var process = Process.Start(psi);
                if (process != null)
                    await process.WaitForExitAsync();

                AddLog("✅ Сброс сетевых настроек завершён");
                MessageBox.Show(
                    "Для применения изменений перезагрузите компьютер.",
                    "Готово",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка сброса сети: {ex.Message}");
            }
        }
    }
}