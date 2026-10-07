using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    /// <summary>Строка списка выбора режима Turbo Boost.</summary>
    public sealed class TurboBoostModeOption
    {
        public required int Mode { get; init; }

        /// <summary>Название с пометками «текущий» и «по умолчанию в Windows».</summary>
        public required string Label { get; init; }

        public required string Description { get; init; }

        // Имя строки списка для экранного диктора и средств автоматизации: без этого
        // они читают имя класса, а не подпись режима.
        public override string ToString() => Label;
    }

    public sealed partial class DiagnosticsViewModel
    {
        // CurrentControlSet — псевдоним активного набора, а не жёсткий ControlSet001:
        // на системах, где активен ControlSet002 (после отказа предыдущей загрузки),
        // жёсткий путь писал бы в неактивный набор и пункт не появлялся бы в Панели управления.
        private const string TurboBoostRegPath = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings\" + TurboBoostModes.Subgroup + @"\" + TurboBoostModes.Setting;

        // Обновляет список режимов и строку текущего режима. Вызывается при загрузке
        // вкладки и после применения режима.
        private async Task RefreshTurboBoostStatusAsync()
        {
            TurboBoostState state = await QueryTurboBoostStateAsync();
            ApplyTurboBoostState(state, ReadWindowsDefaultMode(state.SchemeGuid));
        }

        /// <summary>
        /// Строит список режимов и строку состояния по тому, что сообщил powercfg.
        /// internal — логика пометок проверяется тестами без запуска powercfg.
        /// </summary>
        internal void ApplyTurboBoostState(TurboBoostState state, int? windowsDefault)
        {
            string currentWord = Tr("текущий");
            string defaultWord = Tr("по умолчанию в Windows");

            // Текущий режим показываем в списке, даже если powercfg не назвал его среди
            // возможных: иначе пометку «текущий» было бы некуда поставить.
            var modes = state.Possible.ToList();
            if (state.Ac is int ac && !modes.Contains(ac)) modes.Add(ac);
            modes.Sort();

            TurboBoostModeOptions.Clear();
            foreach (int mode in modes)
            {
                TurboBoostModeOptions.Add(new TurboBoostModeOption
                {
                    Mode = mode,
                    Label = TurboBoostModes.Label(Tr(TurboBoostModes.Name(mode)),
                        isCurrent: mode == state.Ac, isDefault: mode == windowsDefault, currentWord, defaultWord),
                    Description = Tr(TurboBoostModes.Description(mode)),
                });
            }

            SelectedTurboBoostMode = TurboBoostModeOptions.FirstOrDefault(o => o.Mode == state.Ac);

            if (state.Ac is not int current)
            {
                TurboBoostStatusText = Tr("Текущий режим: не удалось определить");
                return;
            }

            string status = $"{Tr("Текущий режим:")} {Tr(TurboBoostModes.Name(current))}";
            // На ноутбуке режим от батареи может отличаться — молчать об этом было бы неправдой.
            if (state.Dc is int battery && battery != current)
                status += $" ({Tr("от батареи")} — {Tr(TurboBoostModes.Name(battery))})";
            TurboBoostStatusText = status;
        }

        private async Task RunApplyTurboBoostModeAsync()
        {
            // Гейт реентерабельности — одного CanExecute мало: перезапрос доступности
            // публикуется с приоритетом ниже обработки ввода, и между снятием флага и
            // реальным отключением кнопки проходит повторный клик. Здесь это означало бы
            // два параллельных powercfg, правящих одну и ту же схему электропитания
            // (та же схема защиты, что в NetworkViewModel).
            if (IsApplyingTurboBoost) return;
            if (SelectedTurboBoostMode is not { } option) return;
            IsApplyingTurboBoost = true;
            try
            {
                string name = TurboBoostModes.Name(option.Mode);
                await ApplyTurboBoostAsync(option.Mode);
                await RefreshTurboBoostStatusAsync();
                AppLogger.Write($"⚡ Режим Turbo Boost: {name}");
                MessageBox.Show($"✅ Режим Turbo Boost: {Tr(name)}.\nИзменение применено немедленно — перезагрузка не требуется.",
                    "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка при смене режима турбобуста: {ex.Message}");
                MessageBox.Show("Не удалось изменить режим Turbo Boost. Запустите приложение от имени администратора и попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsApplyingTurboBoost = false;
            }
        }

        private async Task ApplyTurboBoostAsync(int mode)
        {
            // Применяем для AC (от сети) и DC (от батареи)
            await RunPowerCfgAsync($"-setacvalueindex SCHEME_CURRENT {TurboBoostModes.Subgroup} {TurboBoostModes.Setting} {mode}");
            await RunPowerCfgAsync($"-setdcvalueindex SCHEME_CURRENT {TurboBoostModes.Subgroup} {TurboBoostModes.Setting} {mode}");

            // Активируем схему чтобы применить изменения
            await RunPowerCfgAsync("-setactive SCHEME_CURRENT");

            // Делаем настройку видимой в панели управления
            SetTurboBoostAttributes(2);
        }

        private static async Task<TurboBoostState> QueryTurboBoostStateAsync()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = TrustedExecutablePaths.PowerCfgExe,
                    // /qh, а не /query: настройка в Windows по умолчанию скрыта, и обычный
                    // запрос её не показывает вовсе — режим оставался «неизвестен», пока
                    // программа сама не делала настройку видимой первым применением.
                    Arguments = $"/qh SCHEME_CURRENT {TurboBoostModes.Subgroup} {TurboBoostModes.Setting}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };
                using var process = Process.Start(psi);
                if (process != null)
                {
                    // Асинхронное чтение — не блокируем UI-поток
                    string output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    return TurboBoostModes.Parse(output);
                }
            }
            catch (Exception ex)
            {
                // Иначе в UI просто появляется «не удалось определить», а причина нигде не
                // остаётся — соседние обработчики турбобуста пишут свои ошибки в журнал так же.
                AppLogger.Write(ex, "❌ Не удалось определить режим турбобуста");
            }
            return TurboBoostModes.Parse(null);
        }

        /// <summary>
        /// Режим, который Windows задаёт этой схеме электропитания сама. Нужен для пометки
        /// «по умолчанию в Windows»: по ней видно, к чему вернуться.
        /// </summary>
        private static int? ReadWindowsDefaultMode(string? schemeGuid)
        {
            if (string.IsNullOrEmpty(schemeGuid)) return null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{TurboBoostRegPath}\DefaultPowerSchemeValues\{schemeGuid}");
                return key?.GetValue("ACSettingIndex") is int value ? value : null;
            }
            catch (Exception ex)
            {
                AppLogger.Write(ex, "❌ Не удалось прочитать режим турбобуста по умолчанию");
                return null;
            }
        }

        private async Task RunPowerCfgAsync(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = TrustedExecutablePaths.PowerCfgExe,
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
            catch { /* только видимость пункта в Панели управления — на сам турбобуст не влияет */ }
        }
    }
}
