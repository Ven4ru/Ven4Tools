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
                    OnPropertyChanged(nameof(IsClickToRunInstallation));
                    OnPropertyChanged(nameof(InstalledSummaryText));
                    OnPropertyChanged(nameof(IsReplaceBlocked));
                    UninstallCommand.RaiseCanExecuteChanged();
                    ReplaceCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool ShowInstalledCard => InstalledOffice.Kind != OfficeInstallationKind.NotFound;
        public bool IsMsiInstallation => InstalledOffice.Kind == OfficeInstallationKind.Msi;
        public bool IsClickToRunInstallation => InstalledOffice.Kind == OfficeInstallationKind.ClickToRun;

        public string InstalledSummaryText => InstalledOffice.Kind switch
        {
            OfficeInstallationKind.ClickToRun =>
                $"{InstalledOffice.DisplayName} · {InstalledOffice.Platform} · {InstalledOffice.Culture} · {InstalledOffice.Version}",
            OfficeInstallationKind.Msi => $"{InstalledOffice.DisplayName} · {InstalledOffice.Version}",
            _ => ""
        };

        // «Заменить» заблокирована, если выбранные ниже версия И язык совпадают с уже
        // установленными — устанавливать поверх себя же нет смысла (см. спеку).
        // Раньше сравнивалась только версия: русский Office 2021 нельзя было заменить
        // английским Office 2021 — кнопка гасла без объяснения. Язык установленного
        // неизвестен (ClientCulture пуст) — замену не блокируем: лишняя переустановка
        // безвредна, а запрет смены языка — нет.
        public bool IsReplaceBlocked
        {
            get
            {
                if (InstalledOffice.Kind != OfficeInstallationKind.ClickToRun) return false;
                if (!IsSelectedProductInstalled()) return false;
                return string.Equals(InstalledOffice.Culture, SelectedLanguage, StringComparison.OrdinalIgnoreCase);
            }
        }

        private bool IsSelectedProductInstalled()
        {
            var (_, selectedProductId) = GetSelectedVersion();
            foreach (var installedId in InstalledOffice.ProductIds)
                if (string.Equals(installedId, selectedProductId, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
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
            // Та же версия, другой язык — без языков подтверждение читалось бы как
            // «Office 2021 будет заменён на Office 2021».
            if (IsSelectedProductInstalled())
            {
                if (!string.IsNullOrEmpty(InstalledOffice.Culture))
                    oldDisplayName = $"{oldDisplayName} ({InstalledOffice.Culture})";
                newDisplayName = $"{newDisplayName} ({SelectedLanguage})";
            }

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

            // I2: RunInstallAsync могла не запуститься вовсе — например, конкурентная
            // установка из каталога заняла InstallSemaphore в узком окне между его
            // освобождением в RunConfigureFlowAsync.finally (чуть выше) и повторным
            // захватом внутри самой RunInstallAsync: тогда Views.UiGuards.WarnIfInstallBusy()
            // покажет свой обычный MessageBox и вернёт управление сюда молча —
            // HasDownloadedInstaller при этом остаётся true (файл не тронут), потому что
            // ранний возврат RunInstallAsync происходит ДО того, как она сама сбрасывает
            // этот флаг. InstalledOffice всё ещё NotFound, потому что старый Office к
            // этому моменту уже гарантированно удалён (мы дошли сюда только при
            // oldRemoved == true выше), а новый не встал. Тот же признак совпадает и с
            // другими причинами, по которым RunInstallAsync не довела дело до конца
            // (сбой установщика, отмена пользователем) — во всех них машина сейчас без
            // Office, и не сказать об этом явно значило бы дать замене «тихо потеряться
            // в никуда» ровно в момент, когда на машине временно нет никакого Office.
            if (HasDownloadedInstaller && InstalledOffice.Kind == OfficeInstallationKind.NotFound)
            {
                string message =
                    $"{oldDisplayName} удалён, но {newDisplayName} ещё не установлен.\n\n" +
                    "Скачанный установщик сохранён — нажмите «Установить», когда текущая операция " +
                    "(если она сейчас идёт) завершится.";
                AppLogger.Write($"⚠️ {oldDisplayName} удалён, {newDisplayName} не установлен — установка не завершилась. Установщик сохранён, доступна кнопка «Установить».");
                MessageBox.Show(message, "Замена Office", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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

            // I1/I5: prepared (когда ODT реально скачан и распакован) владеет и рабочей
            // папкой ODT, и открытым на чтение хендлом setup.exe (см. OdtPrepareResult) —
            // держим его до самого finally, ПОСЛЕ того как RunConfigureAsync (а значит и
            // Process.Start элевированного процесса внутри неё) уже вернула управление.
            // configHandle защищает тем же приёмом Configuration.xml — тоже до finally.
            // configWorkDir используется только на запасном пути (уже установленный
            // OfficeClickToRun.exe, без ODT) — там нет чужой рабочей папки, которая сама
            // себя уберёт, поэтому создаём и чистим её сами.
            OdtPrepareResult? prepared = null;
            FileStream? configHandle = null;
            string? configWorkDir = null;

            SetProgress(true, startPhase, 0, "");
            AppLogger.Write($"\n{startPhase}");

            await InstallationService.InstallSemaphore.WaitAsync();
            try
            {
                SetPhase("🔐 Подготовка средства удаления/установки Office...");
                var (exePath, error, resolvedPrepared) = await ResolveConfigureExecutableAsync(CancellationToken.None);
                prepared = resolvedPrepared;
                if (exePath == null)
                {
                    AppLogger.Write($"❌ {error}");
                    SetProgress(true, "❌ Не удалось подготовить средство удаления", 0, error);
                    MessageBox.Show(error, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                // TOCTOU у Configuration.xml — тот же риск, что и у setup.exe (см.
                // OfficeDeploymentToolRunner.PrepareAsync): ODT поддерживает
                // <Add SourcePath="…">, так что подмена этого XML тем же пользователем,
                // пока висит запрос UAC, превращает элевированный процесс в установщик
                // произвольного контента. Решаем тем же приёмом, что и installerHandle в
                // OfficeViewModel.Install.cs: пишем файл, сразу открываем на чтение с
                // FileShare.Read и держим хендл открытым до завершения /configure.
                // Кладём файл в рабочую папку ODT (prepared.WorkDir), когда она есть, —
                // тогда prepared.Dispose() ниже уберёт его вместе со всем остальным, а не
                // в голый %TEMP% отдельным потерянным файлом.
                string configDir;
                if (prepared != null)
                {
                    configDir = prepared.WorkDir;
                }
                else
                {
                    configWorkDir = Path.Combine(Path.GetTempPath(), $"Ven4Tools-ODT-Config-{Guid.NewGuid():N}");
                    Directory.CreateDirectory(configWorkDir);
                    configDir = configWorkDir;
                }

                string configPath = Path.Combine(configDir, "Configuration.xml");
                await File.WriteAllTextAsync(configPath, configurationXml);
                configHandle = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);

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

                // Порядок важен: сначала закрыть хендлы (иначе Directory.Delete внутри
                // prepared.Dispose()/ниже упадёт на файле, открытом на чтение), потом
                // убрать папки. К этому моменту RunConfigureAsync уже вернула управление —
                // элевированный /configure либо завершился, либо запуск не удался вовсе,
                // так что подменять здесь уже нечего.
                configHandle?.Dispose();
                if (configWorkDir != null) { try { Directory.Delete(configWorkDir, recursive: true); } catch { } }
                prepared?.Dispose();

                RefreshInstalledOfficeState();
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    IsInstalling = false;
                    CancelVisible = true;
                });
            }
        }

        // I4: не полагаемся на захардкоженный "C:\..." — Program Files может быть не на
        // C: (кастомная установка Windows) или переопределён политикой; на неанглийской
        // Windows сам путь всё равно на английском (Common Files не переводится), но диск
        // и локаль пользователя — не наше дело угадывать заранее.
        private static string ResolveFallbackClickToRunPath() =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                @"Microsoft Shared\ClickToRun\OfficeClickToRun.exe");

        private async Task<(string? ExePath, string Error, OdtPrepareResult? Prepared)> ResolveConfigureExecutableAsync(CancellationToken ct)
        {
            var prepared = await _deploymentToolRunner.PrepareAsync(ct);
            if (prepared.Success) return (prepared.SetupExePath, "", prepared);

            // Неуспешный результат ничем не владеет (WorkDir уже удалён в PrepareAsync),
            // но он всё равно IDisposable — освобождаем явно, чтобы путь не зависел от
            // того, что «Failed ничего не держит», если это когда-нибудь изменится.
            prepared.Dispose();

            AppLogger.Write($"⚠️ ODT недоступен ({prepared.Error}) — пробуем уже установленный OfficeClickToRun.exe");
            string fallbackPath = ResolveFallbackClickToRunPath();
            // I3: для setup.exe (ODT) выход из процесса действительно означает, что
            // /configure завершился — это синхронный инструмент. Для этого запасного
            // OfficeClickToRun.exe это НЕ подтверждено: исторически C2R-клиент передаёт
            // реальную работу отдельному процессу и потенциально может вернуться раньше
            // фактического завершения (см. GetC2RProcessPids/WaitForC2RProcess/
            // MonitorInstallation в OfficeViewModel.Install.cs — готовый механизм ожидания,
            // если понадобится и здесь). Подтвердить на обязательном ручном прогоне, пока
            // этот путь не задействован по-настоящему.
            if (File.Exists(fallbackPath)) return (fallbackPath, "", null);

            return (null, $"ODT недоступен ({prepared.Error}), запасной OfficeClickToRun.exe тоже не найден на этой машине.", null);
        }
    }
}
