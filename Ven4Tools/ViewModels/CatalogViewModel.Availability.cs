using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Services;

namespace Ven4Tools.ViewModels
{
    // Проверка доступности приложений, загрузка списка версий, статус установленности
    // (включая кнопку запуска), альтернативные источники и карточка приложения.
    // Часть CatalogViewModel.
    public sealed partial class CatalogViewModel
    {
        // ── Доступность / установленность / Play ───────────────────────────────

        private bool _isCheckingAvailability;

        // Соответствует RefreshAvailability_Click оригинала: только проверка
        // доступности, лог "Проверка завершена" и снятие флага сразу после —
        // btnRefreshAvailability должна разблокироваться сразу, без ожидания
        // версий/статуса установки. Не объединять с InitialLoadAvailabilityAsync
        // ниже — иначе кнопка остаётся disabled дольше, чем показывает лог
        // (ровно это ловит AuditFixesCatalogFlowTests.Полный_Проход_Каталог).
        private async Task RefreshAvailabilityAsync()
        {
            // Оригинальный RefreshAvailability_Click начинался с этого guard'а — без
            // него InitialLoadAvailabilityAsync и OnSourceOrderChanged могли запустить
            // проверку параллельно и затоптать друг другу _isCheckingAvailability.
            if (_isCheckingAvailability) return;
            _isCheckingAvailability = true;
            // CommandManager.RequerySuggested (см. RelayCommand) перепроверяет CanExecute
            // только на стандартные UI-события (фокус, клавиатура/мышь) — простая смена
            // приватного поля этого не вызывает. Без явного RaiseCanExecuteChanged кнопка
            // могла оставаться закэшированно disabled уже после того, как флаг снят
            // (см. AuditFixesCatalogFlowTests.Полный_Проход_Каталог — ElementNotEnabledException).
            RefreshAvailabilityCommand.RaiseCanExecuteChanged();
            try
            {
                // Без сброса кэша повторное нажатие кнопки в течение TTL (5 минут,
                // см. AvailabilityChecker.cacheDuration) просто повторяло старые
                // результаты — оригинальный RefreshAvailability_Click всегда чистил кэш.
                _availabilityChecker.ClearCache();
                Log("🔄 Запущена свежая проверка доступности...");

                using var sem = new SemaphoreSlim(5);
                var tasks = Apps.Select(row => CheckOneAvailabilityAsync(row, sem)).ToList();
                await Task.WhenAll(tasks);

                Log($"✅ Проверка завершена: {Apps.Count(a => a.Availability == AppRowViewModel.RowAvailability.Available)} доступно, " +
                    $"{Apps.Count(a => a.Availability == AppRowViewModel.RowAvailability.Unavailable)} недоступно");
            }
            finally
            {
                _isCheckingAvailability = false;
                RefreshAvailabilityCommand.RaiseCanExecuteChanged();
            }
        }

        // Путь первичной загрузки каталога (и смены порядка источников) — после
        // самой проверки доступности ЕЩЁ продолжает версиями/статусом установки,
        // как оригинальный LoadApps()/OnSourceOrderChanged, но это НЕ должно
        // держать btnRefreshAvailability заблокированной все эти секунды.
        private async Task InitialLoadAvailabilityAsync()
        {
            await RefreshAvailabilityAsync();
            await FetchVersionsPhase2Async();
            await UpdateInstalledStatusAsync();
        }

        private async Task CheckOneAvailabilityAsync(AppRowViewModel row, SemaphoreSlim sem)
        {
            // Сбрасываем счётчик ретраев перед первой проверкой — иначе остаток от
            // предыдущего прогона (RefreshAvailability) показал бы «Повторная
            // проверка...» уже на первой обычной проверке.
            row.RetryAttempt = 0;
            var availability = await CheckAvailabilityOnceAsync(row, sem);

            // Соответствует оригинальному CheckSingleAppAvailability: добавленные
            // пользователем приложения (произвольный winget/choco ID) — единственные,
            // для которых имеет смысл повторить проверку при первом Unavailable,
            // прежде чем показать красный статус. Каталожные приложения не ретраятся —
            // так же вело себя CheckAppAvailabilityFromCatalog в оригинале.
            int attempt = 1;
            while (availability == AppRowViewModel.RowAvailability.Unavailable && row.IsUserAdded && attempt < 3)
            {
                // Номер попытки для тултипа «⏳ Повторная проверка... (attempt/3)» —
                // выставляем до перехода в Checking, чтобы StatusTooltip уже знал счётчик.
                row.RetryAttempt = attempt;
                row.Availability = AppRowViewModel.RowAvailability.Checking;
                try { await Task.Delay(2000, _availabilityCts.Token); }
                catch (OperationCanceledException) { break; }
                attempt++;
                availability = await CheckAvailabilityOnceAsync(row, sem);
            }

            row.RetryAttempt = 0;
            row.Availability = availability;
        }

        private async Task<AppRowViewModel.RowAvailability> CheckAvailabilityOnceAsync(AppRowViewModel row, SemaphoreSlim sem)
        {
            await sem.WaitAsync();
            try
            {
                var (status, sizeMB) = await _availabilityChecker.CheckAppAvailabilityWithSize(row.App);
                if (status == AvailabilityChecker.AvailabilityStatus.Available)
                    row.AvailableSizeMB = sizeMB;
                return status switch
                {
                    AvailabilityChecker.AvailabilityStatus.Available   => AppRowViewModel.RowAvailability.Available,
                    AvailabilityChecker.AvailabilityStatus.Unavailable => AppRowViewModel.RowAvailability.Unavailable,
                    _                                                  => AppRowViewModel.RowAvailability.Unknown
                };
            }
            catch { return AppRowViewModel.RowAvailability.Unknown; }
            finally { sem.Release(); }
        }

        private async Task<bool> FetchVersionsForRowAsync(AppRowViewModel row)
        {
            if (string.IsNullOrEmpty(row.App.AlternativeId)) return false;
            var versions = await WingetVersionsService.FetchVersionsAsync(row.App.AlternativeId);
            if (versions.Count == 0) return false;
            Application.Current?.Dispatcher.Invoke(() =>
            {
                row.VersionOptions.Clear();
                row.VersionOptions.Add("Последняя");
                foreach (var v in versions) row.VersionOptions.Add(v);
                row.SelectedVersionOption = "Последняя";
                row.IsVersionComboEnabled = true;
            });
            return true;
        }

        private async Task FetchVersionsPhase2Async()
        {
            using var sem = new SemaphoreSlim(3);
            var tasks = Apps
                .Where(r => !string.IsNullOrEmpty(r.App.AlternativeId) && r.Availability != AppRowViewModel.RowAvailability.Unavailable)
                .Select(row => Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try { return await FetchVersionsForRowAsync(row); }
                    finally { sem.Release(); }
                }));
            var results = await Task.WhenAll(tasks);
            // Соответствует оригинальному AddLog($"✅ Версии загружены для {loaded}
            // приложений") из удалённого при MVVM-переносе CatalogTab.Availability.cs —
            // потерялась при рефакторинге, её ждёт AuditFixesUiTests (первичная загрузка
            // каталога считается завершённой только по этой строке).
            Log($"✅ Версии загружены для {results.Count(ok => ok)} приложений");
        }

        private async Task UpdateInstalledStatusAsync()
        {
            await _installedAppsService.RefreshAsync();
            AppLaunchResolver.InvalidateCache();
            // Первый TryResolve после InvalidateCache перестраивает весь индекс (реестр +
            // .lnk Start Menu + COM на каждый ярлык) — синхронно это фризило бы UI-поток,
            // поэтому строим индекс на фоне один раз, а сам цикл ниже — уже дешёвый lookup.
            //
            // Индекс нужен ТОЛЬКО для Play-кнопки (row.LaunchPath). Базовый статус
            // «установлено»/«есть обновление» от него не зависит, поэтому сбой построения
            // индекса не должен обрывать весь метод до цикла — иначе ни одна строка не
            // получит IsInstalled/InstalledVersion/HasUpdate. Ловим здесь и продолжаем:
            // при падении row.LaunchPath останется null у всех строк, кнопка «▶ Запустить»
            // в этот раз просто не покажется (ShowPlayButton/CanLaunch завязаны на LaunchPath).
            try
            {
                await AppLaunchResolver.EnsureIndexBuiltAsync();
            }
            catch (Exception ex)
            {
                Log($"⚠️ Не удалось построить индекс для кнопки запуска — сама проверка установленных приложений продолжится, но кнопка «▶ Запустить» в этот раз недоступна: {ex.Message}");
            }

            int installed = 0, outdated = 0, launchable = 0;
            foreach (var row in Apps)
            {
                string wingetId = !string.IsNullOrEmpty(row.App.AlternativeId) ? row.App.AlternativeId! : row.AppId;
                bool isInstalled = _installedAppsService.IsInstalled(wingetId);
                row.IsInstalled = isInstalled;

                if (isInstalled)
                {
                    string version = _installedAppsService.GetInstalledVersion(wingetId);
                    row.InstalledVersion = version;
                    row.HasUpdate = !string.IsNullOrEmpty(version) && row.VersionOptions.Count > 1 && version != row.VersionOptions[1];
                    // Пропуск обновления действует только для той версии, которую
                    // пользователь явно отложил. Вышла более новая — VersionOptions[1]
                    // изменился, совпадения нет, метка возвращается сама (ручная
                    // очистка сохранённой записи не нужна).
                    string? ignoredVersion = _ignoredUpdatesService.GetIgnoredVersion(row.AppId);
                    row.IsUpdateIgnored = row.HasUpdate && ignoredVersion != null
                        && ignoredVersion == row.VersionOptions[1];
                    row.LaunchPath = AppLaunchResolver.TryResolve(row.DisplayName);
                    installed++;
                    if (row.HasUpdate) outdated++;
                    if (row.LaunchPath != null) launchable++;
                }
                else
                {
                    row.InstalledVersion = null;
                    row.HasUpdate = false;
                    row.IsUpdateIgnored = false;
                    row.LaunchPath = null;
                }
            }

            if (installed > 0) Log($"📦 Уже установлено: {installed} из {Apps.Count} приложений (кнопка запуска — у {launchable})");
            if (outdated > 0) Log($"🆙 Доступно обновлений: {outdated}");
            if (ProfileService.Current.HideInstalled) AppsView.Refresh();
        }

        private async Task SuggestAlternativeAsync(AppRowViewModel row)
        {
            Log($"🔍 Поиск альтернативы для: {row.DisplayName}");
            var owner = OwnerWindowProvider?.Invoke();
            var dialog = new AlternativeSourceDialog(row.DisplayName) { Owner = owner };
            if (dialog.ShowDialog() != true) return;

            if (dialog.SelectedPackage != null)
            {
                _appManager.SaveAlternativeSource(row.AppId, dialog.SelectedPackage.Id, null, dialog.UseWingetFirst);
                Log($"✅ Сохранён Winget ID: {dialog.SelectedPackage.Id} для {row.DisplayName}");
            }
            else if (!string.IsNullOrEmpty(dialog.CustomUrl))
            {
                _appManager.SaveAlternativeSource(row.AppId, null, dialog.CustomUrl, dialog.UseUrlFirst);
                Log($"✅ Сохранена ссылка: {dialog.CustomUrl} для {row.DisplayName}");
            }
            await Task.Delay(500);
            using var sem = new SemaphoreSlim(1);
            await CheckOneAvailabilityAsync(row, sem);
        }

        private void OpenCard(AppRowViewModel row)
        {
            var owner = OwnerWindowProvider?.Invoke();

            var cardVm = new AppCardViewModel(row, Views.UiGuards.ConfirmPackageManagerInstallAsync, SelectedInstallDrive);
            var window = new Views.AppCardWindow(cardVm) { Owner = owner };
            window.ShowDialog();
        }
    }
}
