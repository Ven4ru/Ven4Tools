using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    public sealed partial class OfficeViewModel
    {
        // ── Состояние "что установлено" ─────────────────────────────────────

        // null означает «детекция ещё не выполнялась». Конструктор VM достижим из
        // юнит-тестов (см. публичный OfficeViewModel() → this(new OfficeInstallationService())),
        // а Detect() на реальном детекторе читает живой реестр — HKLM ClickToRun\Configuration,
        // и при отсутствии C2R дополнительно перебирает обе ветки Uninstall подключ за подключом.
        // Поэтому детекция запускается лениво, при первом реальном обращении к карточке
        // (геттер ниже), а не безусловно в конструкторе.
        private InstalledOfficeInfo? _installedOffice;
        public InstalledOfficeInfo InstalledOffice
        {
            get
            {
                if (_installedOffice == null) RefreshInstalledOfficeState();
                return _installedOffice!;
            }
            private set
            {
                if (SetField(ref _installedOffice, value))
                {
                    OnPropertyChanged(nameof(ShowInstalledCard));
                    OnPropertyChanged(nameof(IsMsiInstallation));
                    OnPropertyChanged(nameof(InstalledSummaryText));
                    OnPropertyChanged(nameof(IsReplaceBlocked));
                    UninstallCommand.RaiseCanExecuteChanged();
                    ReplaceCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool ShowInstalledCard => InstalledOffice.Kind != OfficeInstallationKind.NotFound;
        public bool IsMsiInstallation => InstalledOffice.Kind == OfficeInstallationKind.Msi;

        public string InstalledSummaryText => InstalledOffice.Kind switch
        {
            OfficeInstallationKind.ClickToRun =>
                $"{InstalledOffice.DisplayName} · {InstalledOffice.Platform} · {InstalledOffice.Culture} · {InstalledOffice.Version}",
            OfficeInstallationKind.Msi => $"{InstalledOffice.DisplayName} · {InstalledOffice.Version}",
            _ => ""
        };

        // «Заменить» заблокирована, если выбранная ниже версия совпадает с уже
        // установленной — устанавливать поверх себя же нет смысла (см. спеку).
        public bool IsReplaceBlocked
        {
            get
            {
                if (InstalledOffice.Kind != OfficeInstallationKind.ClickToRun) return false;
                var (_, selectedProductId) = GetSelectedVersion();
                foreach (var installedId in InstalledOffice.ProductIds)
                    if (string.Equals(installedId, selectedProductId, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
        }

        public RelayCommand UninstallCommand { get; }
        public RelayCommand ReplaceCommand { get; }
        public RelayCommand OpenAppsFeaturesCommand { get; }

        private void RefreshInstalledOfficeState()
        {
            try { InstalledOffice = _installationDetector.Detect(); }
            catch (Exception ex)
            {
                AppLogger.Write($"⚠️ Не удалось определить установленный Office: {ex.Message}");
                InstalledOffice = InstalledOfficeInfo.None;
            }
        }

        private void OpenAppsFeatures()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:appsfeatures",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { AppLogger.Write($"⚠️ Не удалось открыть «Установленные приложения»: {ex.Message}"); }
        }

        // ── Удаление ─────────────────────────────────────────────────────────

        private async Task RunUninstallAsync()
        {
            if (IsDownloading || IsInstalling) return;
            if (InstalledOffice.Kind != OfficeInstallationKind.ClickToRun) return;
            if (Views.UiGuards.WarnIfInstallBusy()) return;

            var confirm = MessageBox.Show(
                $"Будет полностью удалён {InstalledOffice.DisplayName}.\n\n" +
                "Будут потеряны установленные приложения Office и активация. Документы, " +
                "сохранённые на диске, удаление не затрагивает.\n\nПродолжить?",
                "Удаление Office", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            await RunConfigureFlowAsync(
                OfficeDeploymentToolRunner.BuildRemoveAllConfigurationXml(),
                startPhase: "🗑️ Удаление Office...",
                successMessage: "✅ Office удалён",
                onSuccess: null);
        }

        // ── Замена на выбранную версию: скачать → удалить старый → поставить новый ──

        private async Task RunReplaceAsync()
        {
            if (IsDownloading || IsInstalling) return;
            if (InstalledOffice.Kind != OfficeInstallationKind.ClickToRun) return;
            if (IsReplaceBlocked) return;
            if (Views.UiGuards.WarnIfInstallBusy()) return;

            var (newDisplayName, _) = GetSelectedVersion();
            string oldDisplayName = InstalledOffice.DisplayName;

            var confirm = MessageBox.Show(
                $"{oldDisplayName} будет удалён и заменён на {newDisplayName}.\n\n" +
                "Будут потеряны установленные приложения Office и активация текущей версии. " +
                "Документы, сохранённые на диске, замена не затрагивает.\n\nПродолжить?",
                "Замена Office", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            // Шаг 1: скачать новую версию — существующий путь, вместе с обходом региона.
            // Сбой здесь не трогает старый Office — RunDownloadAsync ничего не удаляет.
            await RunDownloadAsync();
            if (!HasDownloadedInstaller) return; // RunDownloadAsync уже сообщил об ошибке

            // Шаг 2-3: подготовить ODT/подписи и удалить старый — RunConfigureFlowAsync.
            // Шаг 4 (поставить новый) запускается только при успехе шага 2-3, чтобы
            // при сбое подготовки/удаления уже скачанный установщик остался нетронутым
            // и пользователь мог повторить попытку кнопкой «Установить» — как требует спека.
            bool oldRemoved = await RunConfigureFlowAsync(
                OfficeDeploymentToolRunner.BuildRemoveAllConfigurationXml(),
                startPhase: $"🗑️ Удаление {oldDisplayName}...",
                successMessage: $"✅ {oldDisplayName} удалён — устанавливаем {newDisplayName}",
                onSuccess: null);

            if (!oldRemoved)
            {
                AppLogger.Write("ℹ️ Старый Office не удалён — установка новой версии не запускается. Скачанный установщик сохранён, можно повторить через «Установить».");
                return;
            }

            await RunInstallAsync();
        }

        // ── Общий поток "подготовить ODT/фолбэк → запустить /configure" ─────

        /// <summary>
        /// Готовит исполняемый файл для "/configure" (ODT через winget, либо уже
        /// установленный OfficeClickToRun.exe, если ODT недоступен — см.
        /// OfficeDeploymentToolRunner), запускает его с переданным Configuration.xml
        /// и обновляет состояние карточки. Возвращает true при успехе (exit code 0).
        /// </summary>
        private async Task<bool> RunConfigureFlowAsync(
            string configurationXml, string startPhase, string successMessage, Action? onSuccess)
        {
            IsInstalling = true; // тот же признак занятости, что и обычная установка
            CancelVisible = false; // операция ODT/OfficeClickToRun не отменяема после запуска
            string? configPath = null;

            SetProgress(true, startPhase, 0, "");
            AppLogger.Write($"\n{startPhase}");

            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                SetPhase("🔐 Подготовка средства удаления/установки Office...");
                var (exePath, error) = await ResolveConfigureExecutableAsync(CancellationToken.None);
                if (exePath == null)
                {
                    AppLogger.Write($"❌ {error}");
                    SetProgress(true, "❌ Не удалось подготовить средство удаления", 0, error);
                    MessageBox.Show(error, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                configPath = Path.Combine(Path.GetTempPath(), $"Ven4Tools-ODT-Config-{Guid.NewGuid():N}.xml");
                await File.WriteAllTextAsync(configPath, configurationXml);

                SetPhase(startPhase);
                int exitCode = await _deploymentToolRunner.RunConfigureAsync(exePath, configPath, CancellationToken.None);

                if (exitCode != 0)
                {
                    AppLogger.Write($"❌ /configure завершился с кодом {exitCode}");
                    SetProgress(true, $"❌ Сбой (код {exitCode})", 0, "Попробуйте ещё раз или обратитесь к вручную установленному Office.");
                    return false;
                }

                AppLogger.Write(successMessage);
                SetProgress(true, successMessage, 100, "");
                onSuccess?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"❌ Ошибка: {ex.Message}");
                SetProgress(true, "❌ Ошибка", 0, "");
                MessageBox.Show("Операция с Office не удалась. Попробуйте ещё раз.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
                if (configPath != null) { try { File.Delete(configPath); } catch { } }
                RefreshInstalledOfficeState();
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsInstalling = false;
                    CancelVisible = true;
                });
            }
        }

        private async Task<(string? ExePath, string Error)> ResolveConfigureExecutableAsync(CancellationToken ct)
        {
            var prepared = await _deploymentToolRunner.PrepareAsync(ct);
            if (prepared.Success) return (prepared.SetupExePath, "");

            AppLogger.Write($"⚠️ ODT недоступен ({prepared.Error}) — пробуем уже установленный OfficeClickToRun.exe");
            const string fallbackPath = @"C:\Program Files\Common Files\Microsoft Shared\ClickToRun\OfficeClickToRun.exe";
            if (File.Exists(fallbackPath)) return (fallbackPath, "");

            return (null, $"ODT недоступен ({prepared.Error}), запасной OfficeClickToRun.exe тоже не найден на этой машине.");
        }
    }
}
