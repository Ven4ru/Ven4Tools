using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    // Установка выбранных приложений (пачкой), общий прогресс, а также список
    // неуспешных установок с причиной и повтором. Часть CatalogViewModel.
    public sealed partial class CatalogViewModel
    {
        // ── Установка ────────────────────────────────────────────────────────────

        public int SelectedCount => Apps.Count(a => a.IsSelected);

        private double _overallProgressPercentage;
        public double OverallProgressPercentage
        {
            get => _overallProgressPercentage;
            set => SetField(ref _overallProgressPercentage, value);
        }

        // Отдельно от StatusText (статус загрузки каталога) — раньше это были два
        // разных TextBlock (txtLoadingStatus сверху и txtOverallStatus в панели
        // установки справа), их нельзя было схлопывать в одно свойство.
        private string _installStatusText = "Готов";
        public string InstallStatusText { get => _installStatusText; set => SetField(ref _installStatusText, value); }

        private async Task InstallSelectedAsync()
        {
            var selected = Apps.Where(a => a.IsSelected && a.IsSelectable).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Выберите хотя бы одну программу!", "Ven4Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (Views.UiGuards.WarnIfInstallBusy()) return;

            InstallProgress.Clear();
            ClearFailedInstalls();
            OverallProgressPercentage = 0;
            IsInstalling = true;

            if (selected.Count >= 2)
            {
                var rpOutcome = await Views.UiGuards.ConfirmAndCreateRestorePointAsync(
                    $"Будет установлено {selected.Count} приложений.\n\nСоздать точку восстановления Windows перед установкой?",
                    "Ven4Tools — перед установкой", Log);
                if (rpOutcome == Views.RestorePointOutcome.Cancelled)
                {
                    IsInstalling = false;
                    return;
                }
            }

            _installCts = new CancellationTokenSource();
            var token = _installCts.Token;
            int completed = 0, failed = 0;
            InstallStatusText = $"⏳ Установка 0/{selected.Count}...";

            var progress = new Progress<AppInstallProgress>(p =>
            {
                var existing = InstallProgress.FirstOrDefault(x => x.AppId == p.AppId);
                if (existing != null)
                {
                    existing.Status = p.Status;
                    existing.Percentage = p.Percentage;
                    // Phase/IsIndeterminate — те же поля, что двигают цвет и режим полоски
                    // в CatalogTab (InstallPhaseToBrushConverter, ProgressBar.IsIndeterminate).
                    // Без копирования сюда защитная ветка "existing != null" тихо теряла бы
                    // смену фазы, если бы когда-нибудь появился сценарий с пересозданием
                    // AppInstallProgress для того же AppId вместо мутации одного экземпляра.
                    existing.Phase = p.Phase;
                    existing.IsIndeterminate = p.IsIndeterminate;
                }
                else InstallProgress.Add(p);

                // EffectiveProgress, а не сырой Percentage — Percentage теперь считается
                // заново в каждой фазе (0-100% скачивание, отдельно 0-100% установка), и
                // усреднение по нему "прыгало" бы назад в момент переключения фаз.
                //
                // Шорткат "всё завершено" сверяется по Phase (Done/Error), а не по
                // Percentage>=100 — после разделения на фазы Percentage достигает 100
                // ещё в середине процесса (конец фазы «Загрузка», см. «🔐 Проверка
                // SHA256...» в InstallationService), когда сама установка (elevated-
                // процесс) ещё даже не запущена. Со старым условием общая полоска
                // «Диск установки» могла ложно показать 100%, пока это же приложение
                // по факту продолжало устанавливаться — ровно тот же класс бага,
                // который и была призвана исправить замена Percentage на
                // EffectiveProgress в Average() ниже, только для сиблингового условия.
                OverallProgressPercentage = InstallProgress.All(x => x.Phase is InstallPhase.Done or InstallPhase.Error)
                    ? 100
                    : InstallProgress.Average(x => x.EffectiveProgress);
            });

            var pmConsentCache = new Dictionary<string, bool>();
            using var pmConsentLock = new SemaphoreSlim(1, 1);
            async Task<bool> ConfirmPmInstall(string pmName)
            {
                await pmConsentLock.WaitAsync();
                try
                {
                    if (pmConsentCache.TryGetValue(pmName, out bool cached)) return cached;
                    bool consented = await Views.UiGuards.ConfirmPackageManagerInstallAsync(pmName);
                    pmConsentCache[pmName] = consented;
                    return consented;
                }
                finally { pmConsentLock.Release(); }
            }

            // Момент старта пачки — граница, по которой из общего журнала сбоев
            // отбираются записи именно этой установки, а не прошлых сеансов.
            var batchStartedUtc = DateTime.UtcNow;
            var failedRows = new List<(AppRowViewModel Row, string Message)>();
            var failedRowsLock = new object();

            var tasks = selected.Select(row => Task.Run(async () =>
            {
                await InstallationService.InstallSemaphore.WaitAsync();
                try
                {
                    if (token.IsCancellationRequested) return;
                    var result = await _installService!.InstallAppAsync(
                        row.App, _wingetSources, token, progress, SelectedInstallDrive, row.PinnedVersion, ConfirmPmInstall);
                    if (result.Success)
                    {
                        completed++;
                        if (row.PinnedVersion != null && row.VersionOptions.Count > 1)
                            _versionTracker.TrackInstall(row.AppId, row.PinnedVersion, row.VersionOptions[1]);
                        row.JustInstalled = true;
                    }
                    else
                    {
                        failed++;
                        lock (failedRowsLock) failedRows.Add((row, result.Message));
                    }
                    InstallStatusText = $"⏳ Установка: {completed + failed}/{selected.Count} (✅ {completed} | ❌ {failed})";
                }
                finally { InstallationService.InstallSemaphore.Release(); }
            }, token));

            try
            {
                await Task.WhenAll(tasks);
                // При ошибках сразу указываем, где смотреть причину и как повторить —
                // иначе итог «ошибок: N» остаётся числом без объяснения.
                InstallStatusText = failed > 0
                    ? $"✅ Установка завершена. Успешно: {completed}, ошибок: {failed} — причины в блоке «Не установлено»"
                    : $"✅ Установка завершена. Успешно: {completed}, ошибок: {failed}";
                Log(InstallStatusText);
                await UpdateInstalledStatusAsync();
            }
            catch (OperationCanceledException) { InstallStatusText = "⏹️ Установка отменена"; }
            finally
            {
                IsInstalling = false;
                _installCts?.Dispose();
                _installCts = null;
                // И после обычного завершения, и после отмены: то, что не встало,
                // пользователь должен увидеть здесь же, а не только в логе.
                PublishFailedInstalls(failedRows, batchStartedUtc);
                _ = UpdateSpaceStatusAsync();
            }
        }

        // ── Неуспешные установки: список причин и повтор ────────────────────────

        public bool HasFailedInstalls => FailedInstalls.Count > 0;

        public string FailedInstallsHeader => $"⚠️ Не установлено: {FailedInstalls.Count}";

        private void ClearFailedInstalls()
        {
            if (FailedInstalls.Count == 0) return;
            FailedInstalls.Clear();
            RaiseFailedInstallsChanged();
        }

        private void RaiseFailedInstallsChanged()
        {
            OnPropertyChanged(nameof(HasFailedInstalls));
            OnPropertyChanged(nameof(FailedInstallsHeader));
        }

        /// <summary>
        /// Собирает сводку неудач пачки: список строится по фактическим результатам
        /// установки (он полный), а способ и причина подтягиваются из журнала сбоев
        /// по AppId и времени. Если записи в журнале нет (например, строгий офлайн без
        /// кэша — там журнал не пишется), показываем сообщение самого установщика.
        /// </summary>
        private void PublishFailedInstalls(
            List<(AppRowViewModel Row, string Message)> failedRows, DateTime batchStartedUtc)
        {
            FailedInstalls.Clear();

            if (failedRows.Count > 0)
            {
                var journal = InstallFailureService.ReadAll();
                foreach (var (row, message) in failedRows)
                {
                    var record = InstallFailureReport.FindLatest(journal, row.AppId, batchStartedUtc);
                    string error = !string.IsNullOrWhiteSpace(record?.Error)
                        ? record!.Error
                        : (string.IsNullOrWhiteSpace(message) ? "Причина неизвестна" : message);

                    FailedInstalls.Add(new FailedInstallViewModel(
                        row.DisplayName,
                        InstallFailureReport.MethodLabel(record?.Method),
                        error,
                        item => RetryFailedInstallAsync(row, item)));
                }
            }

            RaiseFailedInstallsChanged();
        }

        /// <summary>
        /// Повтор одной неудачной установки — тем же путём, что и обычная установка
        /// из каталога (<c>InstallationService.InstallAppAsync</c>), под тем же общим
        /// семафором. Никакой отдельной ветки установки здесь нет.
        /// </summary>
        private async Task RetryFailedInstallAsync(AppRowViewModel row, FailedInstallViewModel item)
        {
            if (Views.UiGuards.WarnIfInstallBusy()) return;

            _installService ??= new InstallationService();
            item.RetryStatus = "⏳ Повторная установка...";
            Log($"🔁 Повтор установки: {row.DisplayName}");

            var retryStartedUtc = DateTime.UtcNow;
            var progress = new Progress<AppInstallProgress>(p => item.RetryStatus = p.Status);

            await InstallationService.InstallSemaphore.WaitAsync();
            bool success;
            string message;
            try
            {
                var result = await _installService.InstallAppAsync(
                    row.App, _wingetSources, CancellationToken.None, progress, SelectedInstallDrive,
                    row.PinnedVersion, Views.UiGuards.ConfirmPackageManagerInstallAsync);
                success = result.Success;
                message = result.Message;
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
            }

            if (success)
            {
                row.JustInstalled = true;
                // Та же запись версии, что и в обычной пакетной установке (строка 990) —
                // повтор должен обновлять "версия, установленная в прошлый раз" наравне
                // с обычным путём, а не только успех с первой попытки.
                if (row.PinnedVersion != null && row.VersionOptions.Count > 1)
                    _versionTracker.TrackInstall(row.AppId, row.PinnedVersion, row.VersionOptions[1]);
                Log($"✅ Повторная установка удалась: {row.DisplayName}");
                FailedInstalls.Remove(item);
                RaiseFailedInstallsChanged();
                await UpdateInstalledStatusAsync();
                _ = UpdateSpaceStatusAsync();
                return;
            }

            // Не встало снова — показываем свежую причину вместо причины первой попытки.
            var record = InstallFailureReport.FindLatest(
                InstallFailureService.ReadAll(), row.AppId, retryStartedUtc);
            item.UpdateFailure(
                InstallFailureReport.MethodLabel(record?.Method),
                !string.IsNullOrWhiteSpace(record?.Error)
                    ? record!.Error
                    : (string.IsNullOrWhiteSpace(message) ? "Причина неизвестна" : message));
            item.RetryStatus = "❌ Повтор не удался";
            Log($"❌ Повторная установка не удалась: {row.DisplayName}");
        }
    }
}
