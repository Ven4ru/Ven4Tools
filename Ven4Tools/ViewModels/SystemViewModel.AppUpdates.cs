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
                var (code, raw) = await WingetRunner.RunAsync(
                    $"upgrade --include-unknown --source winget {WingetArgs.NonInteractiveLine}",
                    TimeSpan.FromMinutes(3));

                var upgradable = ParseUpgradableRows(raw);

                // -1 — синтетический код «winget вообще не отработал» (не найден, не
                // запустился, убит по таймауту), тот же признак, что разбирают
                // «Установленные». Пустой вывод в этом случае раньше читался как
                // «обновлений нет», и пользователь получал зелёное «всё актуально».
                if (code == -1 && upgradable.Count == 0)
                {
                    UpdatesLogText = "⚠ Не удалось проверить обновления: winget не найден или не ответил вовремя";
                    AppLogger.Write("⚠ Проверка обновлений winget не выполнена: winget не отработал");
                }
                else if (upgradable.Count > 0)
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
