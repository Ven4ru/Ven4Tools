using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.Views.Tabs
{
    public partial class SystemTab : UserControl
    {
        // CurrentControlSet — псевдоним активного набора, а не жёсткий ControlSet001:
        // на системах, где активен ControlSet002 (после отказа предыдущей загрузки),
        // жёсткий путь писал бы в неактивный набор и пункт не появлялся бы в Панели управления.
        private const string TurboBoostRegPath = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\be337238-0d82-4146-a960-4f3749d470c7";

        private const string TurboSubgroup = "54533251-82be-4824-96c1-47b60b740d00";

        private const string TurboSetting  = "be337238-0d82-4146-a960-4f3749d470c7";

        private async void BtnDisableTurboBoost_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ApplyTurboBoostAsync(false);
                AppLogger.Write("⚡ Турбобуст отключён");
                MessageBox.Show("✅ Турбобуст отключён.\nИзменение применено немедленно — перезагрузка не требуется.",
                    "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка при отключении турбобуста: {ex.Message}");
                MessageBox.Show("Не удалось отключить турбобуст. Запустите приложение от имени администратора и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnEnableTurboBoost_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await ApplyTurboBoostAsync(true);
                AppLogger.Write("⚡ Турбобуст включён");
                MessageBox.Show("✅ Турбобуст включён.\nИзменение применено немедленно — перезагрузка не требуется.",
                    "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка при включении турбобуста: {ex.Message}");
                MessageBox.Show("Не удалось включить турбобуст. Запустите приложение от имени администратора и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task ApplyTurboBoostAsync(bool enable)
        {
            int value = enable ? 1 : 0;

            // Применяем для AC (от сети) и DC (от батареи)
            await RunPowerCfgAsync($"-setacvalueindex SCHEME_CURRENT {TurboSubgroup} {TurboSetting} {value}");
            await RunPowerCfgAsync($"-setdcvalueindex SCHEME_CURRENT {TurboSubgroup} {TurboSetting} {value}");

            // Активируем схему чтобы применить изменения
            await RunPowerCfgAsync("-setactive SCHEME_CURRENT");

            // Делаем настройку видимой в панели управления
            SetTurboBoostAttributes(2);
        }

        private async Task<bool?> GetTurboBoostStateAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Ven4Tools.Services.TrustedExecutablePaths.PowerCfgExe,
                    Arguments = $"/query SCHEME_CURRENT {TurboSubgroup} {TurboSetting}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };
                using var process = Process.Start(psi);
                if (process == null) return null;
                // Асинхронное чтение — не блокируем UI-поток
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                // Языконезависимый разбор: powercfg локализует подписи строк
                // («Current AC Power Setting Index» на русской Windows выводится по-русски),
                // но значения «0x...» встречаются только в двух финальных строках —
                // текущий индекс AC (от сети) и DC (от батареи). Берём первый — AC.
                var matches = System.Text.RegularExpressions.Regex.Matches(output, @"0x([0-9A-Fa-f]+)");
                if (matches.Count > 0)
                    return Convert.ToInt32(matches[0].Groups[1].Value, 16) != 0;
            }
            catch { }
            return null;
        }

        private async Task RunPowerCfgAsync(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Ven4Tools.Services.TrustedExecutablePaths.PowerCfgExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi) ?? throw new Exception("Не удалось запустить powercfg");
            // Читаем stdout и stderr асинхронно — иначе WaitForExit зависнет, если буфер
            // любого из них переполнится. WaitForExitAsync не блокирует UI-поток.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await stdoutTask;
            string err = await stderrTask;
            if (process.ExitCode != 0)
                throw new Exception($"powercfg завершился с ошибкой {process.ExitCode}: {err}");
        }

        private void SetTurboBoostAttributes(int value)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(TurboBoostRegPath, writable: true)
                    ?? Registry.LocalMachine.CreateSubKey(TurboBoostRegPath);
                key.SetValue("Attributes", value, RegistryValueKind.DWord);
            }
            catch { }
        }
    }
}
