using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class NetworkTab : UserControl
    {
        private ZapretService? _zapretService;
        private bool _isZapretActionInProgress = false;
        
        public event Action<string>? LogMessage;
        
        public NetworkTab()
        {
            InitializeComponent();
            _zapretService = new ZapretService(AddLog);
            CheckZapretStatus();
        }
        
        private void AddLog(string message)
        {
            // Пишем в UI лог
            Dispatcher.Invoke(() =>
            {
                txtInstallLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                txtInstallLog.ScrollToEnd();
            });
            // Отправляем в главный лог (статус-бар)
            LogMessage?.Invoke(message);
        }
        
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
        
        private void BtnCheckDns_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start("cmd.exe", "/c nslookup google.com");
                AddLog("🔍 Запущена проверка DNS");
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка: {ex.Message}");
            }
        }
        
        private void BtnResetNetwork_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Сброс сетевых настроек:\n\n" +
                "• netsh winsock reset\n" +
                "• netsh int ip reset\n" +
                "• ipconfig /release\n" +
                "• ipconfig /renew\n\n" +
                "После выполнения потребуется перезагрузка.\n\n" +
                "Продолжить?",
                "Сброс сети",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            
            if (confirm != MessageBoxResult.Yes) return;
            
            try
            {
                Process.Start("cmd.exe", "/c netsh winsock reset && netsh int ip reset && ipconfig /release && ipconfig /renew");
                AddLog("🔄 Запущен сброс сетевых настроек");
                
                MessageBox.Show(
                    "Сетевые настройки сброшены.\n\n" +
                    "Для применения изменений перезагрузите компьютер.",
                    "Готово",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AddLog($"❌ Ошибка: {ex.Message}");
            }
        }
    }
}