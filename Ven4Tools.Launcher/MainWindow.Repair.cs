using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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

                string installedVersion = FileVersionInfo.GetVersionInfo(clientExe).FileVersion ?? "0.0.0";

                // Список версий обычно уже загружен при старте. Если нет (лаунчер был
                // офлайн), тянем его сейчас: иначе отсутствие адреса манифеста выглядело
                // бы как «манифест не опубликован», хотя мы просто ни разу не спросили.
                if (_availableVersions.Count == 0)
                {
                    await LoadVersionsAsync();
                }

                // Эталон нужен именно для УСТАНОВЛЕННОЙ версии, а не для последней:
                // сравнение с манифестом другого релиза объявило бы «повреждённым»
                // весь клиент у любого, кто просто не обновился.
                var installedRelease = _availableVersions
                    .FirstOrDefault(v => VersionComparer.Compare(v.Version, installedVersion) == 0);

                var sources = new ClientIntegritySources
                {
                    ManifestUrl = installedRelease?.ManifestUrl,
                    ManifestSignatureUrl = installedRelease?.ManifestSignatureUrl,
                    FilesBaseUrl = installedRelease?.FilesBaseUrl,
                    FilesBaseMirrorHostingUrl = installedRelease?.FilesBaseMirrorHostingUrl,
                };

                var checker = new ClientIntegrityChecker(_httpClient, this);
                var report = await checker.CheckAsync(_clientPath, installedVersion, sources, token);

                AddLog($"🩺 Проверка версии {installedVersion}: {report.Summary}");
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

            _integrityOperationRunning = true;
            try
            {
                AddLog("🛠 Восстановление файлов клиента...");
                var checker = new ClientIntegrityChecker(_httpClient, this);
                return await checker.RepairAsync(report, _clientPath, token);
            }
            finally
            {
                _integrityOperationRunning = false;
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
