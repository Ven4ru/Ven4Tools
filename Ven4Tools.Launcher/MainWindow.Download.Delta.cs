using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    /// <summary>
    /// Исход попытки блочного (дельта-) обновления клиента.
    /// </summary>
    internal enum DeltaUpdateOutcome
    {
        /// <summary>Обновление применено пофайлово — полный путь не нужен.</summary>
        Installed,

        /// <summary>
        /// Дельта неприменима или не удалась (нет манифеста, нет локального состава,
        /// выгода ниже порога, сбой загрузки/установки) — идём полным путём.
        /// </summary>
        FallBackToFullDownload,

        /// <summary>
        /// Установка невозможна по причине, которую полный путь не устранит
        /// (пользователь не закрыл клиент, небезопасный путь установки, отмена).
        /// Полную загрузку запускать бессмысленно — она упрётся в то же самое.
        /// </summary>
        Aborted,
    }

    public partial class MainWindow
    {
        /// <summary>
        /// Блочное (дельта-) обновление клиента: вместо zip-архива целиком качаются
        /// только те файлы публикации, чей SHA256 отличается от установленного.
        /// На типичном релизе меняется несколько файлов из нескольких сотен, поэтому
        /// экономия трафика — десятки-сотни мегабайт на каждое обновление.
        ///
        /// Строго вспомогательный путь: он либо срабатывает целиком, либо тихо уступает
        /// место обычной полной загрузке. Никаких новых способов отказать пользователю
        /// в обновлении здесь появиться не должно.
        /// </summary>
        private async Task<DeltaUpdateOutcome> TryDeltaUpdateAsync(
            ClientVersionInfo version, CancellationToken token, bool silent)
        {
            // 1. Знает ли CDN файловый манифест для этой версии? Поля опциональны:
            //    релизы до появления этой возможности их не содержат.
            if (string.IsNullOrWhiteSpace(version.ManifestUrl) ||
                string.IsNullOrWhiteSpace(version.ManifestSignatureUrl) ||
                string.IsNullOrWhiteSpace(version.FilesBaseUrl))
            {
                return DeltaUpdateOutcome.FallBackToFullDownload;
            }

            // Обновлять пофайлово нечего, если клиента на диске ещё нет.
            if (!File.Exists(Path.Combine(_clientPath, LauncherPaths.ClientExeName)))
            {
                return DeltaUpdateOutcome.FallBackToFullDownload;
            }

            string workingDirectory = Path.Combine(
                Path.GetTempPath(), $"Ven4Tools_Delta_{version.Version}_{Guid.NewGuid():N}");

            try
            {
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Проверка обновления...");

                // 2. Манифест + подпись. Fail-closed: не прошло — полный путь.
                var fetched = await ClientManifestFetcher.FetchAsync(
                    _httpClient, version.ManifestUrl!, version.ManifestSignatureUrl!, token);
                if (fetched == null)
                {
                    AddLog("ℹ️ Дельта недоступна: файловый манифест не получен или его подпись не подтверждена — полная загрузка");
                    return DeltaUpdateOutcome.FallBackToFullDownload;
                }

                var remote = fetched.Value.Manifest;

                // Манифест обязан описывать ровно ту версию, которую ставим: иначе
                // пофайловая подмена собрала бы на диске смесь из двух релизов.
                if (!string.Equals(remote.Version, version.Version, StringComparison.OrdinalIgnoreCase))
                {
                    AddLog($"ℹ️ Дельта недоступна: манифест описывает версию {remote.Version}, а ставится {version.Version} — полная загрузка");
                    return DeltaUpdateOutcome.FallBackToFullDownload;
                }

                // 3. План: что скачать, что удалить, выгодна ли дельта вообще.
                var store = new InstalledManifestStore();
                var plan = ClientDeltaPlanner.Plan(remote, store.Load());
                if (plan.FullDownloadRecommended)
                {
                    AddLog($"ℹ️ Дельта неприменима: {plan.Reason} — полная загрузка");
                    return DeltaUpdateOutcome.FallBackToFullDownload;
                }

                AddLog($"⚡ Блочное обновление: {plan.Reason}");
                AddLog($"⚡ К загрузке {FormatBytes(plan.DownloadBytes)} вместо полного архива");

                // 4. Загрузка изменившихся файлов и применение одной транзакцией.
                var installer = new ClientDeltaInstaller();
                string ip = CdnService.LastKnownCdnIp ?? IpPinnedHttpClientFactory.FallbackCdnIp;
                HttpClient ipPinned = IpPinnedHttpClientFactory.GetOrCreate(ip, Timeout.InfiniteTimeSpan);

                // using держит FileShare.Read-хендлы на всех скачанных файлах открытыми
                // до конца установки — то же закрытие окна TOCTOU, что и у полного пути.
                using var downloaded = await installer.DownloadChangedFilesAsync(
                    plan,
                    version.FilesBaseUrl!,
                    version.FilesBaseMirrorHostingUrl,
                    workingDirectory,
                    _downloadSource,
                    _httpClient,
                    ipPinned,
                    fileProgress: (current, total) =>
                    {
                        int percent = total == 0 ? 100 : (int)((double)current / total * 100);
                        Dispatcher.BeginInvoke(() =>
                        {
                            progressDownload.Value = percent;
                            txtDownloadStatus.Text = $"Загрузка изменений: {current} из {total}";
                        });
                    },
                    log: AddLog,
                    token);

                token.ThrowIfCancellationRequested();

                Dispatcher.Invoke(() => SetOperationStage(2)); // Проверка целостности
                AddLog("🔒 Целостность каждого файла подтверждена (SHA256)");

                // Те же предустановочные проверки, что и у полного пути — риски
                // одинаковые (клиент запущен, небезопасная папка установки).
                if (!await EnsureClientClosedAndPathSafeAsync(silent)) return DeltaUpdateOutcome.Aborted;

                Dispatcher.Invoke(() =>
                {
                    SetOperationStage(4); // Установка файлов
                    txtDownloadStatus.Text = "Установка изменённых файлов...";
                });

                // Транзакция на UI-поток не выносится: она читает и переименовывает
                // файлы публикации и заметно блокировала бы отрисовку окна.
                await Task.Run(
                    () => installer.Apply(remote, plan, downloaded, _clientPath, AddLog, token),
                    token);

                Dispatcher.Invoke(() =>
                {
                    SetOperationStage(5); // Готово
                    txtDownloadStatus.Text = "Готово";
                    progressDownload.Value = 100;
                    SetLaunchButtonState(LaunchButtonState.Launch);
                });
                AddLog($"✅ Клиент обновлён до {version.Version} блочным обновлением " +
                       $"({plan.ToDownload.Count} файлов, {FormatBytes(plan.DownloadBytes)})");
                _clientUpdateAvailable = false;
                return DeltaUpdateOutcome.Installed;
            }
            catch (OperationCanceledException)
            {
                // Отмена пользователем относится ко всему обновлению, а не только к
                // дельте: перезапускать после неё полную загрузку было бы издевательством.
                throw;
            }
            catch (Exception ex)
            {
                // Единственная реакция на сбой дельты — полный путь. Сама себя дельта
                // не чинит: наполовину применённое обновление опаснее лишнего трафика,
                // а транзакция InstallPartial к этому моменту уже откатила изменения.
                AddLog($"⚠️ Блочное обновление не удалось: {ex.Message} — переключаюсь на полную загрузку");
                return DeltaUpdateOutcome.FallBackToFullDownload;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workingDirectory)) Directory.Delete(workingDirectory, recursive: true);
                }
                catch
                {
                    // Временный каталог в %TEMP% — его остаток не мешает работе лаунчера.
                }
            }
        }

        /// <summary>
        /// Пересчитывает локальный манифест установленной версии по реальному
        /// содержимому папки клиента. Вызывается после ЛЮБОЙ полной установки —
        /// только так следующее обновление сможет пойти дельтой.
        ///
        /// Ошибка пересчёта не является ошибкой установки: кэш в этом случае
        /// удаляется. Устаревший кэш опаснее отсутствующего — по нему дельта сочла
        /// бы неизменившимися файлы, которых на диске уже нет.
        /// </summary>
        private async Task RefreshInstalledManifestAsync(string versionLabel, CancellationToken token)
        {
            var store = new InstalledManifestStore();
            try
            {
                var manifest = await ClientManifestBuilder.BuildFromDirectoryAsync(_clientPath, versionLabel, token);
                if (store.Save(manifest))
                {
                    AddLog($"🧾 Состав установленной версии записан ({manifest.Files?.Count ?? 0} файлов) — следующее обновление может быть блочным");
                    return;
                }

                AddLog("⚠️ Не удалось сохранить состав установленной версии — следующее обновление будет полным");
                store.Invalidate();
            }
            catch (OperationCanceledException)
            {
                store.Invalidate();
                throw;
            }
            catch (Exception ex)
            {
                store.Invalidate();
                AddLog($"⚠️ Не удалось посчитать состав установленной версии ({ex.Message}) — следующее обновление будет полным");
            }
        }

        /// <summary>Объём в привычных единицах для строки журнала.</summary>
        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:F1} ГБ";
            if (bytes >= 1024L * 1024) return $"{bytes / 1024.0 / 1024:F1} МБ";
            if (bytes >= 1024) return $"{bytes / 1024.0:F1} КБ";
            return $"{bytes} Б";
        }
    }
}
