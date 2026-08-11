using System;
using System.Collections.Generic;
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
                    $"upgrade --include-unknown --source winget {WingetArgs.NonInteractiveLine}",
                    TimeSpan.FromMinutes(3));

                var upgradable = ParseUpgradableRows(raw);

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

        // Разбор таблицы winget upgrade — тем же способом, что и в
        // UpdateBackgroundService.CountWingetUpgradesAsync: берём строки между
        // разделителем «---» и футером, разделитель определяем общим
        // WingetRunner.IsTableSeparator.
        //
        // Прежняя версия фильтровала строки по английским префиксам («Name», «The ») —
        // единственный парсер вывода winget в клиенте, не переведённый на общий критерий.
        // Проект принципиально не передаёт --locale en-US, поэтому на русской Windows
        // winget печатает «Имя/ИД/Версия» и русский футер: ни один из префиксов не
        // совпадал, и строка заголовка вместе с футером попадала в список как
        // «доступные обновления», завышая счётчик на экране настроек.
        private static List<string> ParseUpgradableRows(string raw)
        {
            var rows = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return rows;

            var lines = WingetRunner.StripAnsi(raw).Replace("\r", "").Split('\n');
            int sepIdx = Array.FindIndex(lines, WingetRunner.IsTableSeparator);
            if (sepIdx < 0) return rows;

            for (int i = sepIdx + 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) break;      // пустая строка = начался футер
                if (WingetRunner.IsTableSeparator(line)) continue;
                // Строка-суммарник футера winget («32 upgrades available.»,
                // «Доступны обновления: 32.») — не строка таблицы. Отсекаем по
                // локаленезависимому признаку выравнивания колонок, а не по
                // английским словам: на русской Windows футер под прежний шаблон
                // не подходил и показывался в списке как ещё одно обновление.
                if (!WingetRunner.IsTableRow(line)) break;
                rows.Add(line.Trim());
            }
            return rows;
        }
    }
}
