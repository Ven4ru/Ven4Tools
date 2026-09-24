using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    /// <summary>
    /// «Проверить и восстановить клиент» — диагностика установленной публикации и
    /// пофайловая починка найденных повреждений. Кнопка живёт в окне настроек
    /// (см. SettingsWindow), а вся работа — здесь: и предустановочные проверки, и
    /// журнал, и клиенты загрузки уже принадлежат главному окну, а второй их
    /// экземпляр в окне настроек неизбежно разошёлся бы с оригиналом.
    ///
    /// Путь обычного обновления (TryDeltaUpdateAsync и полная загрузка) этим кодом
    /// не затрагивается: переиспользуются только его строительные блоки.
    /// </summary>
    public partial class MainWindow : IClientRepairExecutor
    {
        // Проверка и починка запускаются одной кнопкой окна настроек, но окно можно
        // открыть повторно, а починка внутри себя ждёт ответа пользователя в диалоге
        // «закрыть клиент?» — за это время вторая проверка успела бы начать хеширование
        // той же папки, которую первая уже переписывает. Гейт один на обе операции.
        private bool _integrityOperationRunning;

        // Папка, для которой построен последний отчёт проверки. Отчёт живёт в окне
        // настроек сколько угодно; если за это время папку клиента сменили, его план
        // относится к другому каталогу, и применять его к текущему нельзя.
        private ClientIntegrityReport? _lastIntegrityReport;
        private string? _lastIntegrityReportClientPath;

        /// <summary>
        /// Запускает проверку целостности установленного клиента. Возвращает отчёт
        /// (в том числе «не установлен» / «не с чем сверять») либо null, если другая
        /// проверка или починка уже идёт.
        /// </summary>
        internal async Task<ClientIntegrityReport?> CheckClientIntegrityAsync(CancellationToken token)
        {
            if (_integrityOperationRunning) return null;
            _integrityOperationRunning = true;
            try
            {
                AddLog("🩺 Проверка целостности установленного клиента...");

                string clientExe = Path.Combine(_clientPath, LauncherPaths.ClientExeName);
                if (!File.Exists(clientExe))
                {
                    var absent = ClientIntegrityReport.NotInstalled();
                    AddLog($"🩺 Проверка: {absent.Summary}");
                    return absent;
                }

                // Пустой/null FileVersion — не «версия неизвестна, но клиент цел»,
                // а прямая улика повреждения: у настоящего собранного проектом exe
                // версия читается всегда. Подставлять здесь фиктивное «0.0.0» и
                // отправлять его дальше по обычному пути (поиск манифеста, сеть)
                // раньше приводило к ложному «сервер недоступен» — на самом деле
                // проблема целиком локальная. Решение принимает CheckAsync.
                string? installedVersion = FileVersionInfo.GetVersionInfo(clientExe).FileVersion;

                // Список версий обычно уже загружен при старте. Если нет (лаунчер был
                // офлайн), тянем его сейчас: иначе отсутствие адреса манифеста выглядело
                // бы как «манифест не опубликован», хотя мы просто ни разу не спросили.
                if (_availableVersions.Count == 0)
                {
                    await LoadVersionsAsync();
                }

                // Эталон нужен именно для УСТАНОВЛЕННОЙ версии, а не для последней:
                // сравнение с манифестом другого релиза объявило бы «повреждённым»
                // весь клиент у любого, кто просто не обновился. При нечитаемой версии
                // сравнивать всё равно не с чем — CheckAsync сам вернёт диагноз ниже.
                var installedRelease = installedVersion == null
                    ? null
                    : _availableVersions.FirstOrDefault(
                        v => VersionComparer.Compare(v.Version, installedVersion) == 0);

                var sources = new ClientIntegritySources
                {
                    ManifestUrl = installedRelease?.ManifestUrl,
                    ManifestSignatureUrl = installedRelease?.ManifestSignatureUrl,
                    FilesBaseUrl = installedRelease?.FilesBaseUrl,
                    FilesBaseMirrorHostingUrl = installedRelease?.FilesBaseMirrorHostingUrl,
                };

                string checkedClientPath = _clientPath;
                var checker = new ClientIntegrityChecker(_httpClient, this);
                var report = await checker.CheckAsync(checkedClientPath, installedVersion, sources, token);
                _lastIntegrityReport = report;
                _lastIntegrityReportClientPath = checkedClientPath;

                AddLog($"🩺 Проверка версии {installedVersion ?? "не читается"}: {report.Summary}");
                if (report.AclCompromised)
                {
                    AddLog("⚠️ Права доступа к папке клиента ослаблены — файлы может изменить любой пользователь этого компьютера");
                }
                if (report.HasRepairableFindings && report.Plan != null)
                {
                    AddLog($"🩺 К восстановлению {report.Plan.ToDownload.Count} файлов " +
                           $"({FormatBytes(report.Plan.DownloadBytes)}), к удалению лишних {report.Plan.ToDelete.Count}");
                }

                return report;
            }
            catch (OperationCanceledException)
            {
                AddLog("⏹ Проверка целостности отменена");
                return null;
            }
            catch (Exception ex)
            {
                // Диагностический экран не должен ронять лаунчер ничем.
                AddLog($"⚠️ Проверка целостности не выполнена: {ex.Message}");
                return null;
            }
            finally
            {
                _integrityOperationRunning = false;
            }
        }

        /// <summary>
        /// Применяет найденную починку. Решение «чинить или нет» принимает
        /// <see cref="ClientIntegrityChecker.RepairAsync"/>; здесь только запуск.
        /// </summary>
        internal async Task<bool> RepairClientIntegrityAsync(
            ClientIntegrityReport report, CancellationToken token)
        {
            if (_integrityOperationRunning)
            {
                report.SetRepairMessage("другая проверка ещё выполняется");
                return false;
            }

            // Починка переписывает файлы в папке клиента — ровно то же, что загрузка,
            // установка из файла и тихое автообновление. Раньше она шла мимо общего
            // слота и могла выполняться одновременно с ними.
            using var lease = TryBeginOperation(
                "Восстановление файлов клиента", Timeout.InfiniteTimeSpan, silent: true);
            if (lease == null)
            {
                report.SetRepairMessage(
                    $"сейчас выполняется другая операция: {_operations.CurrentOperation ?? "загрузка или установка"}");
                return false;
            }

            // Отчёт мог устареть, пока окно настроек было открыто: клиент обновили
            // (тогда план собран по манифесту прежней версии и смешал бы на диске два
            // релиза) или сменили папку клиента (план относится к другому каталогу).
            if (!IsIntegrityReportCurrent(report))
            {
                report.SetRepairMessage("клиент изменился после проверки — запустите проверку заново");
                return false;
            }

            // Запущенный клиент держит свои файлы: без этого шага починка зависала на
            // «Восстановление...», так и не заменив ни одного файла (найдено при ручной
            // проверке на Windows). Установка и обновление перед применением файлов
            // закрывают клиента — здесь тот же штатный путь, с вопросом пользователю.
            if (IsClientRunning())
            {
                var answer = System.Windows.MessageBox.Show(
                    "Ven4Tools сейчас запущен.\n\nЗакрыть клиент, чтобы восстановить его файлы?",
                    "Клиент запущен", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    report.SetRepairMessage("клиент запущен — закройте его и повторите");
                    return false;
                }

                AddLog("🔒 Закрываю клиент перед восстановлением файлов...");
                if (!await TryCloseRunningClientAsync())
                {
                    report.SetRepairMessage("клиент не закрылся (возможно, свёрнут в трей) — закройте его вручную и повторите");
                    return false;
                }
                AddLog("✅ Клиент закрыт, продолжаю восстановление");
            }

            _integrityOperationRunning = true;
            try
            {
                AddLog("🛠 Восстановление файлов клиента...");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lease.Token);
                var checker = new ClientIntegrityChecker(_httpClient, this);
                return await checker.RepairAsync(report, _clientPath, linked.Token);
            }
            finally
            {
                _integrityOperationRunning = false;
            }
        }

        private bool IsIntegrityReportCurrent(ClientIntegrityReport report)
        {
            if (!ReferenceEquals(report, _lastIntegrityReport) ||
                !string.Equals(_lastIntegrityReportClientPath, _clientPath, StringComparison.OrdinalIgnoreCase))
                return false;

            string? manifestVersion = report.RemoteManifest?.Version;
            if (manifestVersion == null) return true; // чинить всё равно не по чему — решит RepairAsync

            try
            {
                string clientExe = Path.Combine(_clientPath, LauncherPaths.ClientExeName);
                string? installed = File.Exists(clientExe)
                    ? FileVersionInfo.GetVersionInfo(clientExe).FileVersion
                    : null;
                return installed != null && VersionComparer.Compare(manifestVersion, installed) == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Рискованная часть починки: скачать недостающие файлы и применить их одной
        /// транзакцией. Полностью повторяет порядок действий блочного обновления
        /// (проверка SHA256 каждого файла → клиент закрыт и путь безопасен →
        /// InstallPartial), потому что риски у них буквально одни и те же: подмена
        /// файлов в той же папке того же клиента.
        /// </summary>
        async Task<bool> IClientRepairExecutor.ApplyAsync(
            Models.ClientFileManifest remoteManifest,
            ClientDeltaPlan plan,
            ClientIntegritySources sources,
            string clientPath,
            CancellationToken cancellationToken)
        {
            string workingDirectory = Path.Combine(
                Path.GetTempPath(), $"Ven4Tools_Repair_{Guid.NewGuid():N}");

            try
            {
                var installer = new ClientDeltaInstaller();
                string ip = CdnService.LastKnownCdnIp ?? IpPinnedHttpClientFactory.FallbackCdnIp;
                HttpClient ipPinned = IpPinnedHttpClientFactory.GetOrCreate(ip, Timeout.InfiniteTimeSpan);

                // using держит FileShare.Read-хендлы на скачанных файлах до конца
                // установки — то же закрытие окна TOCTOU, что и у обновления.
                using var downloaded = await installer.DownloadChangedFilesAsync(
                    plan,
                    sources.FilesBaseUrl!,
                    sources.FilesBaseMirrorHostingUrl,
                    workingDirectory,
                    _downloadSource,
                    _httpClient,
                    ipPinned,
                    fileProgress: null,
                    log: AddLog,
                    cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                AddLog("🔒 Целостность каждого скачанного файла подтверждена (SHA256)");

                // Тот же общий гейт, что и у любого другого способа положить файлы в
                // папку клиента — своей копии этих проверок здесь быть не должно.
                if (!await EnsureClientClosedAndPathSafeAsync(silent: false)) return false;

                // Отчёт мог устареть: окно настроек немодальное, между «Проверить» и
                // «Исправить» клиент успел обновиться. Применять план старой версии
                // поверх новой — значит вернуть её файлы и удалить «лишние», то есть
                // файлы новой версии. Сверяем версию на диске прямо перед записью.
                string? onDisk = FileVersionInfo.GetVersionInfo(
                    Path.Combine(clientPath, LauncherPaths.ClientExeName)).FileVersion;
                if (onDisk == null || remoteManifest.Version == null ||
                    VersionComparer.Compare(onDisk, remoteManifest.Version) != 0)
                {
                    AddLog($"⚠️ Восстановление отменено: в папке уже версия {onDisk ?? "не читается"}, " +
                           $"а отчёт составлен для {remoteManifest.Version} — запустите проверку заново");
                    return false;
                }

                // Транзакция читает и переименовывает файлы публикации — не на UI-потоке.
                await Task.Run(
                    () => installer.Apply(remoteManifest, plan, downloaded, clientPath, AddLog, cancellationToken),
                    cancellationToken);

                AddLog($"✅ Клиент восстановлен: заменено файлов {plan.ToDownload.Count}, " +
                       $"удалено лишних {plan.ToDelete.Count}");
                return true;
            }
            catch (OperationCanceledException)
            {
                AddLog("⏹ Восстановление клиента отменено");
                return false;
            }
            catch (Exception ex)
            {
                // Как и дельта, починка себя не доисправляет: InstallPartial к этому
                // моменту уже откатила транзакцию, а наполовину починенный клиент
                // хуже честного «не удалось, попробуйте переустановить».
                AddLog($"⚠️ Восстановление не удалось: {ex.Message}");
                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workingDirectory)) Directory.Delete(workingDirectory, recursive: true);
                }
                catch
                {
                    // Временный каталог в %TEMP% — его остаток работе лаунчера не мешает.
                }
            }
        }
    }
}
