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

        // Тонкая обёртка над общим Ven4Tools.Shared.WingetOutputParser.ParseUpgradeTableRows
        // (та же петля раньше была продублирована здесь, в UpdateBackgroundService
        // клиента и в UpdateBackgroundService лаунчера) — оставлена под прежним именем/
        // сигнатурой, т.к. на неё завязаны существующие юнит-тесты этого класса.
        internal static List<string> ParseUpgradableRows(string raw) =>
            Ven4Tools.Shared.WingetOutputParser.ParseUpgradeTableRows(raw);
    }
}
