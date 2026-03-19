using System;
using System.IO;  // Добавить эту строку
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools
{
    public partial class UpdateCheckWindow : Window
    {
        private readonly UpdateChecker updateChecker;
        private UpdateInfo? updateInfo;

        public UpdateCheckWindow(string yandexFolderUrl)
        {
            try
            {
                File.AppendAllText(@"C:\Users\Ven4\debug_window.log", 
                    $"{DateTime.Now}: Конструктор UpdateCheckWindow вызван\n");
            }
            catch { }
            
            InitializeComponent();
            updateChecker = new UpdateChecker(yandexFolderUrl);
            this.Loaded += async (s, e) => await CheckForUpdate();
        }

        private async Task CheckForUpdate()
        {
            try
            {
                File.AppendAllText(@"C:\Users\Ven4\debug_window.log", 
                    $"{DateTime.Now}: CheckForUpdate начат\n");
                    
                updateInfo = await updateChecker.CheckForUpdateAsync();
                
                File.AppendAllText(@"C:\Users\Ven4\debug_window.log", 
                    $"{DateTime.Now}: updateInfo получен, HasUpdate={updateInfo?.HasUpdate}\n");
                
                progressBar.Visibility = Visibility.Collapsed;
                panelResult.Visibility = Visibility.Visible;

                if (updateInfo != null && updateInfo.HasUpdate)
                {
                    txtResultTitle.Text = "🎉 Доступно обновление!";
                    txtResultTitle.Foreground = System.Windows.Media.Brushes.LightGreen;
                    
                    txtVersionInfo.Text = $"Текущая версия: {updateChecker.GetCurrentVersion()} → Новая версия: {updateInfo.LatestVersion}";
                    
                    if (!string.IsNullOrEmpty(updateInfo.ReleaseNotes))
                    {
                        txtChangelog.Text = updateInfo.ReleaseNotes;
                        scrollChangelog.Visibility = Visibility.Visible;
                    }
                    
                    btnUpdate.Visibility = Visibility.Visible;
                }
                else if (updateInfo != null && !string.IsNullOrEmpty(updateInfo.Error))
                {
                    txtResultTitle.Text = "⚠️ Ошибка проверки";
                    txtResultTitle.Foreground = System.Windows.Media.Brushes.LightCoral;
                    txtVersionInfo.Text = "Не удалось проверить наличие обновлений.";
                }
                else
                {
                    txtResultTitle.Text = "✅ У вас актуальная версия";
                    txtResultTitle.Foreground = System.Windows.Media.Brushes.LightGreen;
                    txtVersionInfo.Text = $"Версия {updateChecker.GetCurrentVersion()} — последняя доступная.";
                }
            }
            catch (Exception ex)
            {
                File.AppendAllText(@"C:\Users\Ven4\debug_window.log", 
                    $"{DateTime.Now}: Ошибка - {ex.Message}\n");
                    
                txtResultTitle.Text = "❌ Ошибка";
                txtVersionInfo.Text = ex.Message;
            }
            finally
            {
                progressBar.Visibility = Visibility.Collapsed;
            }
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (updateInfo?.DownloadUrl == null) return;
            
            btnUpdate.IsEnabled = false;
            txtStatus.Text = "📥 Скачивание обновления...";
            txtStatus.Visibility = Visibility.Visible;
            progressBar.Visibility = Visibility.Visible;
            panelResult.Visibility = Visibility.Collapsed;

            bool downloaded = await updateChecker.DownloadAndRunUpdate(updateInfo.DownloadUrl);

            if (downloaded)
            {
                Application.Current.MainWindow?.Close();
                this.Close();
            }
            else
            {
                MessageBox.Show("Не удалось скачать обновление.", 
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                this.Close();
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}