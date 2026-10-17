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

        /// <summary>
        /// Отметка строки изменилась. Кроме счётчика, заново опрашиваются команды
        /// («Установить выбранные», «Сохранить выбор»): WPF сам переспрашивает их только
        /// после ввода с мыши или клавиатуры, а отметка меняется и без него — пресетом,
        /// импортом списка, кодом набора, средствами автоматизации. Без явного вызова
        /// кнопки оставались неактивными до следующего движения мыши.
        /// </summary>
        private void OnRowSelectionChanged()
        {
            RefreshSelection();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

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

        /// <summary>Итог пачки установок — нужен тихому режиму для кода возврата и отчёта.</summary>
        internal sealed record InstallBatchResult(
            IReadOnlyList<AppRowViewModel> Installed,
            IReadOnlyList<(AppRowViewModel Row, string Message)> Failed,
            bool Cancelled,
            IReadOnlyList<AppRowViewModel> RebootRequired,
            IReadOnlyList<AppRowViewModel> AlreadyInstalled);

        /// <param name="unattended">
        /// Задание тихого режима. Когда оно задано с <see cref="UnattendedRequest.Silent"/>,
        /// ни один вопрос не показывается: ответы берутся из задания. null — обычная
        /// установка по кнопке.
        /// </param>
        /// <returns>null, если установка не началась (нечего ставить, занято, отказ на вопросе).</returns>
        private async Task<InstallBatchResult?> InstallSelectedAsync(UnattendedRequest? unattended = null)
        {
            bool silent = unattended?.Silent == true;
            // Порядок установки — тот, в котором программы выбрали или назвали в файле
            // ответа; компоненты — первыми, драйверные пакеты, которые почти всегда просят
            // перезагрузку, — в конце.
            var selected = InstallOrder.Arrange(
                Apps.Where(a => a.IsSelected && a.IsSelectable).ToList(),
                row => row.AppId, row => row.App.CategoryString,
                unattended?.AppIds ?? SelectedApps.Select(a => a.AppId).ToList());
            if (selected.Count == 0)
            {
                if (!silent)
                    MessageBox.Show("Выберите хотя бы одну программу!", "Ven4Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }
            if (silent)
            {
                if (InstallationService.IsBusy)
                {
                    Log("⚠️ Тихая установка не начата: уже идёт другая установка");
                    return null;
                }
            }
            else if (Views.UiGuards.WarnIfInstallBusy()) return null;

            InstallProgress.Clear();
            ClearFailedInstalls();
            OverallProgressPercentage = 0;
            IsInstalling = true;

            // Шаг 1: точка восстановления. Отказ на вопросе — установка не начинается.
            if (!await PrepareRestorePointAsync(unattended, silent, selected.Count))
            {
                IsInstalling = false;
                return null;
            }

            // Состав пачки — в очередь на случай перезагрузки: если клиент не дойдёт до
            // конца, при следующем запуске он предложит поставить оставшееся.
            PendingInstallQueue.Default.Begin(selected.Select(r => r.AppId), SelectedInstallDrive.TrimEnd('\\'));

            _installCts = new CancellationTokenSource();
            var token = _installCts.Token;
            InstallStatusText = $"⏳ Установка 0/{selected.Count}...";

            // Шаг 2: приём хода установки от каждой программы.
            var progress = CreateBatchProgress();

            var pmConsentCache = new Dictionary<string, bool>();
            using var pmConsentLock = new SemaphoreSlim(1, 1);
            async Task<bool> ConfirmPmInstall(string pmName)
            {
                await pmConsentLock.WaitAsync();
                try
                {
                    if (pmConsentCache.TryGetValue(pmName, out bool cached)) return cached;
                    bool consented = silent
                        ? unattended!.AllowPackageManagers
                        : await Views.UiGuards.ConfirmPackageManagerInstallAsync(pmName);
                    pmConsentCache[pmName] = consented;
                    return consented;
                }
                finally { pmConsentLock.Release(); }
            }

            // Момент старта пачки — граница, по которой из общего журнала сбоев
            // отбираются записи именно этой установки, а не прошлых сеансов.
            var batchStartedUtc = DateTime.UtcNow;
            var tally = new InstallBatchTally();
            bool cancelled = false;

            // Шаг 3: сами установки.
            // Одна задача на весь набор, программы — строго по очереди. Раньше на каждую
            // программу заводилась своя задача, и порядок определяло то, какая из них
            // первой дождётся общей блокировки установки: он менялся от запуска к запуску.
            // Блокировка по-прежнему берётся на каждую программу отдельно, чтобы между
            // программами набора могли пройти другие установки (повтор, Windows Update).
            // Набор (несколько программ или тихий режим) пропускает уже установленное
            // (InstallationService.InstallAppAsync, skipIfInstalled) и закрывает окна,
            // которые программы открывают сами после установки (NewAppWindowCloser).
            // Одиночная установка по кнопке ведёт себя как прежде.
            bool batchMode = silent || selected.Count > 1;
            var batch = Task.Run(async () =>
            {
              foreach (var row in selected)
              {
                await InstallBatchRowAsync(row, selected.Count, tally, progress, ConfirmPmInstall, batchMode, token);
              }

              // Упавшие из-за занятого Windows Installer (1618) — один повтор в конце набора:
              // к этому времени чужая или зависшая установка обычно уже закончилась.
              if (batchMode)
              {
                List<AppRowViewModel> busy;
                lock (tally.RowsLock)
                {
                    busy = tally.FailedRows.Where(f => InstallerLockGuard.IsMsiBusyFailure(f.Message)).Select(f => f.Row).ToList();
                    tally.FailedRows.RemoveAll(f => busy.Contains(f.Row));
                    tally.Failed -= busy.Count;
                }
                if (busy.Count > 0)
                {
                    Log($"🔁 Повтор в конце набора: {busy.Count} — не встали, пока Windows Installer был занят ({string.Join(", ", busy.Select(r => r.DisplayName))})");
                    foreach (var row in busy)
                        await InstallBatchRowAsync(row, selected.Count, tally, progress, ConfirmPmInstall, batchMode, token);
                }
              }
            }, token);

            try
            {
                await batch;
                // Шаг 4: итог.
                ReportBatchFinished(tally);

                await UpdateInstalledStatusAsync();
            }
            catch (OperationCanceledException) { InstallStatusText = "⏹️ Установка отменена"; cancelled = true; }
            finally
            {
                IsInstalling = false;
                _installCts?.Dispose();
                _installCts = null;
                // Пачка дошла до конца — пусть и с ошибками или отменой: неудачи видны в
                // блоке «Не установлено» с повтором, а очередь нужна только на случай,
                // когда до этого места клиент не добрался.
                PendingInstallQueue.Default.Clear();
                // И после обычного завершения, и после отмены: то, что не встало,
                // пользователь должен увидеть здесь же, а не только в логе.
                PublishFailedInstalls(tally.FailedRows, batchStartedUtc);
                _ = UpdateSpaceStatusAsync();
            }

            return new InstallBatchResult(tally.InstalledRows, tally.FailedRows, cancelled, tally.RebootRows, tally.AlreadyInstalledRows);
        }

        /// <summary>Счётчики и списки одной пачки установок: заполняются по ходу, читаются в конце.</summary>
        private sealed class InstallBatchTally
        {
            public int Completed;
            public int Failed;
            public List<(AppRowViewModel Row, string Message)> FailedRows { get; } = new();
            public List<AppRowViewModel> InstalledRows { get; } = new();
            public List<AppRowViewModel> RebootRows { get; } = new();
            /// <summary>Уже стояли до набора и были пропущены (входят и в InstalledRows).</summary>
            public List<AppRowViewModel> AlreadyInstalledRows { get; } = new();
            /// <summary>Замок на списки: их дополняет задача установки.</summary>
            public object RowsLock { get; } = new();
        }

        /// <summary>
        /// Точка восстановления перед установкой: по ответу из задания либо по вопросу.
        /// </summary>
        /// <returns>false — на вопросе выбрана отмена, установку начинать нельзя.</returns>
        private async Task<bool> PrepareRestorePointAsync(UnattendedRequest? unattended, bool silent, int appCount)
        {
            // Точка восстановления: задание может ответить заранее. Без ответа тихий
            // режим её не создаёт (спросить некого), обычный — спрашивает, как всегда.
            if (unattended?.RestorePoint == true)
            {
                Log("🛡️ Создаю точку восстановления...");
                bool created = await SystemRestoreService.CreateRestorePointAsync("Ven4Tools — перед установкой");
                Log(created ? "✅ Точка восстановления создана" : "⚠️ Точка восстановления не создана (установка продолжается)");
            }
            else if (unattended?.RestorePoint == null && !silent && appCount >= 2)
            {
                var rpOutcome = await Views.UiGuards.ConfirmAndCreateRestorePointAsync(
                    $"Будет установлено {appCount} приложений.\n\nСоздать точку восстановления Windows перед установкой?",
                    "Ven4Tools — перед установкой", Log);
                if (rpOutcome == Views.RestorePointOutcome.Cancelled)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Приёмник хода установки: обновляет строку программы в списке «Прогресс установки»
        /// и общую полоску. Создаётся в потоке интерфейса — туда же приходят и сообщения.
        /// </summary>
        private Progress<AppInstallProgress> CreateBatchProgress() =>
            new Progress<AppInstallProgress>(p =>
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

        /// <summary>Установка одной программы пачки под общей блокировкой установки.</summary>
        private async Task InstallBatchRowAsync(
            AppRowViewModel row, int total, InstallBatchTally tally, IProgress<AppInstallProgress> progress,
            Func<string, Task<bool>> confirmPmInstall, bool batchMode, CancellationToken token)
        {
            // Отмена во время ожидания и между программами — это отмена, а не
            // штатное завершение: иначе итог сообщал бы об успехе без неначатых программ.
            await InstallationService.InstallSemaphore.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                var startedUtc = DateTime.UtcNow;
                var result = await _installService!.InstallAppAsync(
                    row.App, _wingetSources, token, progress, SelectedInstallDrive, row.PinnedVersion, confirmPmInstall,
                    skipIfInstalled: batchMode);
                if (batchMode && result.Success && result.Message != InstallationService.AlreadyInstalledSkippedMessage)
                    NewAppWindowCloser.ScheduleClose(startedUtc, row.DisplayName, Log);
                if (result.Success)
                {
                    tally.Completed++;
                    lock (tally.RowsLock)
                    {
                        tally.InstalledRows.Add(row);
                        if (result.Message == InstallationService.AlreadyInstalledSkippedMessage)
                            tally.AlreadyInstalledRows.Add(row);
                        if (result.Progress.RebootRequired) tally.RebootRows.Add(row);
                    }
                    PendingInstallQueue.Default.MarkDone(row.AppId);
                    if (row.PinnedVersion != null && row.VersionOptions.Count > 1)
                        _versionTracker.TrackInstall(row.AppId, row.PinnedVersion, row.VersionOptions[1]);
                    row.JustInstalled = true;
                }
                else
                {
                    tally.Failed++;
                    lock (tally.RowsLock) tally.FailedRows.Add((row, result.Message));
                }
                InstallStatusText = $"⏳ Установка: {tally.Completed + tally.Failed}/{total} (✅ {tally.Completed} | ❌ {tally.Failed})";
            }
            finally { InstallationService.InstallSemaphore.Release(); }
        }

        /// <summary>Итог пачки: строка состояния, запись в журнал и уведомление.</summary>
        private void ReportBatchFinished(InstallBatchTally tally)
        {
            // При ошибках сразу указываем, где смотреть причину и как повторить —
            // иначе итог «ошибок: N» остаётся числом без объяснения.
            InstallStatusText = tally.Failed > 0
                ? $"✅ Установка завершена. Успешно: {tally.Completed}, ошибок: {tally.Failed} — причины в блоке «Не установлено»"
                : $"✅ Установка завершена. Успешно: {tally.Completed}, ошибок: {tally.Failed}";
            if (tally.AlreadyInstalledRows.Count > 0)
                InstallStatusText += $". Уже были установлены, пропущено: {tally.AlreadyInstalledRows.Count}";
            // Одна просьба о перезагрузке на весь набор — в конце, а не посреди установки.
            if (tally.RebootRows.Count > 0)
                InstallStatusText += $". Нужна перезагрузка: {string.Join(", ", tally.RebootRows.Select(r => r.DisplayName))}";
            Log(InstallStatusText);

            // Чекбокс "Показывать уведомления о завершении установки" на «Настройках»
            // раньше сохранялся, но ничего не включал — уведомления не существовало.
            // Тост идёт через тот же механизм, что и уведомления об обновлениях
            // (трей-балун либо запись в лог как fallback без трея).
            if (tally.Completed > 0 && ProfileService.Current.NotifyInstallComplete)
            {
                UpdateBackgroundService.ShowNotification(
                    "Установка завершена",
                    tally.Failed > 0
                        ? $"Успешно установлено: {tally.Completed}, не удалось: {tally.Failed}."
                        : $"Успешно установлено: {tally.Completed}.");
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
            catch (OperationCanceledException)
            {
                // CancellationToken.None исключает отмену пользователем именно ЗДЕСЬ —
                // долетевшее исключение это внутренний таймаут (например HttpClient.
                // Timeout на прямой загрузке), не связанный с переданным токеном. Раньше
                // этот путь не ловился: строка на весь оставшийся сеанс замирала на
                // "⏳ Повторная установка...", а исключение тихо гасилось центральным
                // RelayCommand.FromAsync без всякого следа для пользователя.
                item.RetryStatus = "❌ Повтор не удался (таймаут)";
                Log($"❌ Повторная установка не удалась (таймаут): {row.DisplayName}");
                return;
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
            }

            if (success)
            {
                row.JustInstalled = true;
                // Та же запись версии, что и в обычной пакетной установке
                // (InstallSelectedAsync выше в этом файле) —
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
