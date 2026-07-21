using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class SystemTab : UserControl
    {
        private async void BtnCheckUpdates_Click(object sender, RoutedEventArgs e)
        {
            btnCheckUpdates.IsEnabled = false;
            txtUpdatesLog.Text = "⏳ Проверка...";
            try
            {
                var (_, raw) = await WingetRunner.RunAsync(
                    "upgrade --include-unknown --source winget",
                    TimeSpan.FromMinutes(3));

                var upgradable = raw.Split('\n')
                    .Select(l => WingetRunner.StripAnsi(l).Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l)
                             && !l.StartsWith("Name")
                             && !l.StartsWith("-")
                             && !l.StartsWith("The ")
                             && l.Contains("  "))
                    .ToList();

                if (upgradable.Count > 0)
                {
                    txtUpdatesLog.Text = $"🔔 Доступно обновлений: {upgradable.Count}\n\n" + string.Join("\n", upgradable);
                    AppLogger.Write($"🔔 Доступно обновлений winget: {upgradable.Count}");
                }
                else
                {
                    txtUpdatesLog.Text = "✅ Все установленные приложения актуальны";
                    AppLogger.Write("✅ Обновлений winget не найдено");
                }
            }
            catch (Exception ex)
            {
                txtUpdatesLog.Text = $"❌ Ошибка: {ex.Message}";
                AppLogger.Write($"❌ Ошибка проверки обновлений: {ex.Message}");
            }
            finally
            {
                btnCheckUpdates.IsEnabled = true;
            }
        }
    }
}
