using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        private bool _isCheckingUpdates;
        public bool IsCheckingUpdates
        {
            get => _isCheckingUpdates;
            private set { if (SetField(ref _isCheckingUpdates, value)) CheckUpdatesCommand.RaiseCanExecuteChanged(); }
        }

        private string _updatesLogText = "Нажмите «Проверить обновления» для проверки...";
        public string UpdatesLogText { get => _updatesLogText; private set => SetField(ref _updatesLogText, value); }

        private async Task RunCheckUpdatesAsync()
        {
            if (IsCheckingUpdates) return;

            IsCheckingUpdates = true;
            UpdatesLogText = "⏳ Проверка...";
            try
            {
                var (_, raw) = await WingetRunner.RunAsync(
                    $"upgrade --include-unknown --source winget {WingetArgs.NonInteractiveLine}",
                    TimeSpan.FromMinutes(3));

                var upgradable = ParseUpgradableRows(raw);

                if (upgradable.Count > 0)
                {
                    UpdatesLogText = $"🔔 Доступно обновлений: {upgradable.Count}\n\n" + string.Join("\n", upgradable);
                    AppLogger.Write($"🔔 Доступно обновлений winget: {upgradable.Count}");
                }
                else
                {
                    UpdatesLogText = "✅ Все установленные приложения актуальны";
                    AppLogger.Write("✅ Обновлений winget не найдено");
                }
            }
            catch (Exception ex)
            {
                UpdatesLogText = $"❌ Ошибка: {ex.Message}";
                AppLogger.Write($"❌ Ошибка проверки обновлений: {ex.Message}");
            }
            finally
            {
                IsCheckingUpdates = false;
            }
        }

        // Разбор таблицы winget upgrade: строки между разделителем «---» и футером,
        // локаленезависимый критерий (WingetRunner.IsTableSeparator/IsTableRow —
        // внутренний разрыв в 2+ пробела = строка таблицы), не английские префиксы —
        // проект принципиально не передаёт winget --locale en-US, и на русской Windows
        // такие префиксы не совпадали, из-за чего заголовок и футер попадали в список
        // «доступных обновлений», завышая счётчик.
        internal static List<string> ParseUpgradableRows(string raw)
        {
            var rows = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return rows;

            var lines = WingetRunner.StripAnsi(raw).Replace("\r", "").Split('\n');
            int sepIdx = Array.FindIndex(lines, WingetRunner.IsTableSeparator);
            if (sepIdx < 0) return rows;

            for (int i = sepIdx + 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) break;
                if (WingetRunner.IsTableSeparator(line)) continue;
                if (!WingetRunner.IsTableRow(line)) break;
                rows.Add(line.Trim());
            }
            return rows;
        }
    }
}
