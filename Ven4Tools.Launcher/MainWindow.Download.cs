using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    public partial class MainWindow
    {
        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_isUiTestMode)
            {
                AddLog("UI test: выбор папки");
                return;
            }

            // Смена папки посреди загрузки/установки уводила _clientPath из-под идущей
            // операции: staging создавался у старой папки, а переносился в новую, и
            // проверки пути относились уже не к тому каталогу. Слот держим и на время
            // диалога — операция, начавшаяся за это время, получила бы ту же проблему.
            using var lease = TryBeginOperation("Смена папки установки", Timeout.InfiniteTimeSpan);
            if (lease == null) return;

            SelectInstallFolder();
        }

        // Сам выбор папки — без слота: его вызывает и «Найти клиент на диске», который
        // уже держит слот операций.
        private void SelectInstallFolder()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description      = "Выберите папку для установки Ven4Tools",
                ShowNewFolderButton = true,
                // Открываем диалог на текущей папке установки. Без этого он каждый раз
                // стартовал с «Рабочего стола», и пользователь заново искал место,
                // которое лаунчер тут же показывает ему в карточке пути.
                //
                // Нужны ОБА свойства: SelectedPath лишь выделяет элемент (диалог при
                // этом открывается в родительской папке — проверено живьём), а внутрь
                // самой папки заходит только InitialDirectory.
                InitialDirectory = Directory.Exists(_installPath) ? _installPath : "",
                SelectedPath     = Directory.Exists(_installPath) ? _installPath : ""
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _installPath = dialog.SelectedPath;
                _clientPath  = Path.Combine(_installPath, "Ven4Tools_Client");
                // Заранее не создаём — выбор папки ещё не означает установку;
                // каталог появится вместе с первой установкой клиента.
                txtInstallPath.Text = _clientPath;
                SaveSettings();
                AddLog($"📁 Папка установки изменена: {_clientPath}");
                CheckExistingClient();
            }
        }

        // Что именно сделала зачистка остатков прерванной установки. Нужен только для
        // journal-строки при старте: сама зачистка молчала, и пользователь не имел
        // никакого способа узнать, что его установку когда-то прервали и что лаунчер
        // вернул на место единственную сохранившуюся копию файлов.
        private readonly struct StaleArtifactCleanupResult
        {
            public StaleArtifactCleanupResult(int restored, int removed)
            {
                Restored = restored;
                Removed = removed;
            }

            /// <summary>Возвращено на штатное место (каталогов и файлов суммарно).</summary>
            public int Restored { get; }

            /// <summary>Удалено осиротевших остатков (каталогов и файлов суммарно).</summary>
            public int Removed { get; }

            public bool AnythingHappened => Restored > 0 || Removed > 0;
        }

        // Осиротевшие ".Ven4Tools_Client.staging-*" / "Ven4Tools_Client.backup-*" — остаются
        // рядом с папкой клиента, если процесс убит посреди DownloadVersionAsync/
        // TransactionalDirectoryInstaller.Install. Однократная зачистка при старте:
        // единственный экземпляр лаунчера (см. App.SingleInstance) гарантирует, что
        // на момент запуска эти каталоги не могут принадлежать активной операции.
        //
        // Логика зачистки не менялась — добавлен только подсчёт сделанного.
        private static StaleArtifactCleanupResult CleanupStaleInstallArtifacts(string clientPath)
        {
            int restored = 0;
            int removed = 0;
            try
            {
                string fullClientPath = Path.GetFullPath(clientPath);
                string? parent = Path.GetDirectoryName(fullClientPath);
                if (parent == null || !Directory.Exists(parent)) return default;

                string clientName = Path.GetFileName(fullClientPath);
                string stagingPrefix = $".{clientName}.staging-";
                string backupPrefix = $"{clientName}.backup-";

                // Материализуем список: ниже возможно перемещение бэкапа обратно в
                // папку клиента (внутри того же родителя), а менять каталог во время
                // ленивого перечисления небезопасно.
                foreach (string dir in Directory.EnumerateDirectories(parent).ToList())
                {
                    string name = Path.GetFileName(dir);
                    bool isStaging = name.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase);
                    bool isBackup = name.StartsWith(backupPrefix, StringComparison.OrdinalIgnoreCase);
                    if (!isStaging && !isBackup) continue;

                    // Есть бэкап предыдущей версии, а самой папки клиента нет — значит
                    // установку прервали между Move(target→backup) и Move(staging→target).
                    // Это не «мусор»: в бэкапе лежит единственная рабочая версия, поэтому
                    // восстанавливаем её обратно в target, а не удаляем.
                    if (isBackup && !Directory.Exists(fullClientPath))
                    {
                        try { Directory.Move(dir, fullClientPath); restored++; }
                        catch { /* не удалось восстановить — оставляем бэкап на месте, не удаляя */ }
                        continue;
                    }

                    // target на месте (установка завершилась) либо это staging —
                    // такой каталог действительно осиротевший, его можно удалить.
                    try { Directory.Delete(dir, recursive: true); removed++; }
                    catch { /* занято/уже удалено — не мешаем запуску лаунчера */ }
                }

                // Остатки прерванного блочного (дельта-) обновления — «файл.new-{id}»
                // и «файл.old-{id}» ВНУТРИ папки клиента. Транзакция InstallPartial
                // убирает их сама, но убитый посреди работы процесс мог не успеть.
                // Опознаются по строгому шаблону (см. IsTransientArtifactName), поэтому
                // настоящие файлы публикации задеть невозможно.
                if (Directory.Exists(fullClientPath))
                {
                    foreach (string file in Directory.EnumerateFiles(fullClientPath, "*", SearchOption.AllDirectories).ToList())
                    {
                        string name = Path.GetFileName(file);
                        if (!TransactionalDirectoryInstaller.TryParseTransientArtifactName(
                                name, out string originalName, out bool isBackup))
                            continue;

                        // «.old-{id}» — сохранённый оригинал. Если файла под штатным
                        // именем рядом нет, значит процесс убили между переименованием
                        // оригинала и установкой нового файла: в остатке лежит
                        // ЕДИНСТВЕННАЯ копия, её надо вернуть на место, а не удалить
                        // (тот же случай, что и с ".backup-*" выше).
                        string original = Path.Combine(Path.GetDirectoryName(file)!, originalName);
                        if (isBackup && !File.Exists(original))
                        {
                            try { File.Move(file, original); restored++; }
                            catch { /* не удалось вернуть — остаток оставляем, не удаляя */ }
                            continue;
                        }

                        try { File.Delete(file); removed++; }
                        catch { /* занято/уже удалено */ }
                    }
                }
            }
            catch { /* зачистка необязательна для работы лаунчера */ }

            // Счётчики возвращаются и при исключении: то, что успели сделать до сбоя,
            // уже сделано, и умалчивать об этом было бы неверно.
            return new StaleArtifactCleanupResult(restored, removed);
        }

        // Строит цепочку источников для скачивания клиента: CDN-домен → CDN прямой IP →
        // хостинг-зеркало → GitHub, с учётом выбранного пользователем предпочтения.
        // Если CDN не знал версию (только GithubUrl) — цепочка вырождается в один GitHub.
        private List<DownloadCandidate> BuildClientCandidates(ClientVersionInfo version)
        {
            string ip = CdnService.LastKnownCdnIp ?? IpPinnedHttpClientFactory.FallbackCdnIp;
            // Для клиентских загрузок клиент с бесконечным таймаутом (как _httpClient):
            // длительность ограничивается CancellationToken на месте вызова.
            HttpClient ipPinned = IpPinnedHttpClientFactory.GetOrCreate(ip, Timeout.InfiniteTimeSpan);
            return FallbackDownloader.BuildCandidates(
                _downloadSource,
                version.CdnUrl,
                version.MirrorHostingUrl,
                version.GithubUrl ?? version.DownloadUrl,
                _httpClient,
                ipPinned);
        }

        // Версия клиента на диске (null — не установлен или не читается): нужна политике
        // установки без подтверждения CDN, чтобы не откатить клиента на старую версию.
        private string? ReadInstalledClientVersion()
        {
            try
            {
                string clientExe = Path.Combine(_clientPath, LauncherPaths.ClientExeName);
                return File.Exists(clientExe) ? FileVersionInfo.GetVersionInfo(clientExe).FileVersion : null;
            }
            catch
            {
                return null;
            }
        }

        private void RefuseUnconfirmedArchive(string version, string reason, string advice, bool silent)
        {
            txtDownloadStatus.Text = "Целостность не подтверждена";
            SetOperationStage(0);
            AddLog($"⛔ Версия {version} не установлена: {reason}");
            if (!silent)
                System.Windows.MessageBox.Show(
                    $"Не удалось подтвердить целостность архива версии {version}: {reason}.\n\n{advice}",
                    "Целостность не подтверждена", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private async Task DownloadVersionAsync(ClientVersionInfo version, CancellationToken token, bool silent = false)
        {
            if (version == null) return;

            // Цепочка источников (CDN-домен → CDN прямой IP → хостинг-зеркало → GitHub)
            // с учётом выбранного предпочтения. Защита от подмены (только доверенные
            // хосты по HTTPS) выполняется внутри FallbackDownloader для каждого кандидата.
            var candidates = BuildClientCandidates(version);
            if (candidates.Count == 0)
            {
                AddLog($"⛔ Нет доверенных источников загрузки — скачивание отменено: {version.DownloadUrl}");
                return;
            }

            AddLog($"📥 Скачивание клиента {version.Version}...");

            string tempZip = Path.Combine(
                Path.GetTempPath(),
                $"Ven4Tools_Client_{version.Version}_{Guid.NewGuid():N}.zip");

            progressDownload.Value    = 0;
            txtDownloadStatus.Text    = "Скачивание: 0%";
            btnCancelDownload.Visibility = Visibility.Visible;
            btnLaunchApp.IsEnabled    = false;
            // Та же блокировка, что и у «Установить Ven4Tools»: иначе кнопка остаётся
            // нажимаемой во время загрузки и запускает вторую установку в тот же каталог.
            btnInstallFromFile.IsEnabled = false;
            // «Установить компоненты» раньше в этот список не входила, хотя её
            // обработчик — такая же долгая операция с тем же прогрессом и той же
            // кнопкой «Отмена».
            btnInstallMissing.IsEnabled = false;
            SetOperationStage(1); // Загрузка

            try
            {
                // Попытка блочного (дельта-) обновления ДО полной загрузки: если CDN
                // отдал подписанный файловый манифест и на диске есть подтверждённый
                // состав установленной версии — качаем только изменившиеся файлы.
                // Любая неудача здесь означает переход к полному пути ниже, а не
                // ошибку для пользователя; отказ пользователя закрыть клиент и
                // небезопасный путь установки (Aborted) полный путь тоже не спасёт —
                // он упёрся бы в те же проверки, только после лишних сотен мегабайт.
                var deltaOutcome = await TryDeltaUpdateAsync(version, token, silent);
                if (deltaOutcome == DeltaUpdateOutcome.Installed)
                {
                    if (!silent)
                        System.Windows.MessageBox.Show(
                            $"Клиент {version.Version} успешно обновлён в:\n{_clientPath}",
                            "Обновление завершено", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                if (deltaOutcome == DeltaUpdateOutcome.Aborted) return;

                // Без SHA256 из подписанного version.json остаётся второй источник
                // доверия — встроенная ECDSA-подпись архива (как у «Установить из
                // файла»): архив проверяется ею после загрузки, см. ниже. Даунгрейд так
                // не ставится никогда — его отсекаем до загрузки, не тратя минуты.
                bool hashConfirmed = DownloadValidator.IsValidSha256(version.ExpectedSha256);
                string? installedVersion = null;
                if (!hashConfirmed)
                {
                    var why = ClientHashAvailability.Explain(version.Version, _cdnManifestLoaded, _cdnClientVersion);
                    installedVersion = ReadInstalledClientVersion();
                    string? refusal = SignedArchiveFallbackPolicy.CheckBeforeDownload(version.Version, installedVersion);
                    if (refusal != null)
                    {
                        RefuseUnconfirmedArchive(version.Version, $"{why.Reason}; {refusal}", why.Advice, silent);
                        return;
                    }
                    AddLog($"⚠️ Для версии {version.Version} нет подтверждённого SHA256: {why.Reason} — архив будет проверен по встроенной подписи");
                }

                var downloader = new FallbackDownloader();
                // using держит FileShare.Read-хендл на tempZip открытым до конца метода
                // (в т.ч. через SafeZipExtractor.ExtractAsync ниже) — закрывает окно TOCTOU
                // между проверкой SHA256 внутри DownloadAsync и распаковкой архива.
                using var downloadResult = await downloader.DownloadAsync(
                    candidates,
                    tempZip,
                    token,
                    hashConfirmed ? version.ExpectedSha256 : null,
                    // FallbackDownloader теперь читает/пишет с ConfigureAwait(false) (не
                    // маршалит каждую итерацию через WPF-контекст) — эти колбэки поэтому
                    // могут прилетать с любого потока пула, не только с UI-потока, и обязаны
                    // маршалить сами. BeginInvoke (асинхронный), а не Invoke — колбэк не
                    // должен блокировать поток загрузки в ожидании отрисовки.
                    progress: (received, total) =>
                    {
                        if (total is > 0)
                        {
                            int percent = (int)((double)received / total.Value * 100);
                            Dispatcher.BeginInvoke(() =>
                            {
                                progressDownload.Value = percent;
                                txtDownloadStatus.Text = $"Скачивание: {percent}%";
                            });
                        }
                    },
                    switchingTo: (label, reason) =>
                    {
                        AddLog($"⚠️ Предыдущий источник {reason}, переключаюсь: {label}...");
                        Dispatcher.BeginInvoke(() =>
                        {
                            progressDownload.Value = 0;
                            txtDownloadStatus.Text = "Скачивание: 0%";
                        });
                    });
                string usedSource = downloadResult.SourceLabel;
                AddLog($"📥 Источник загрузки: {usedSource}");

                token.ThrowIfCancellationRequested();

                // Подтверждённый SHA256 проверен загрузчиком до принятия файла; при
                // несовпадении основного источника автоматически пробовался резервный.
                // Без него архив принимается только по встроенной подписи — fail-closed,
                // как у самообновления лаунчера: без того или другого установки нет.
                SetOperationStage(2); // Проверка целостности
                txtDownloadStatus.Text = "Проверка целостности...";
                if (hashConfirmed)
                {
                    AddLog("🔒 Целостность подтверждена (SHA256)");
                }
                else
                {
                    // Хендл downloadResult (FileShare.Read) держит архив неизменным от
                    // этой проверки до распаковки — та же защита от подмены, что у
                    // проверки SHA256 внутри загрузчика.
                    // Список отзыва VerifyAsync запрашивает сам, прямо сейчас; признак
                    // «не проверен» — сбой ИМЕННО этого запроса (CdnService сообщает о
                    // каждом null через log), а не _cdnManifestLoaded со старта: тот
                    // устаревает в обе стороны и молчал, когда список не пришёл сейчас.
                    string? revocationListFailure = null;
                    using var cdnService = new CdnService(reason => revocationListFailure = reason);
                    var signed = await LocalArchiveVerifier.VerifyAsync(tempZip, cdnService, token);
                    string? refusal = signed.Outcome == LocalArchiveOutcome.Rejected
                        ? signed.RejectionReason
                        : SignedArchiveFallbackPolicy.CheckSignedArchive(
                            signed.Outcome == LocalArchiveOutcome.Offline ? signed.Version : null,
                            version.Version, installedVersion);
                    if (refusal != null)
                    {
                        RefuseUnconfirmedArchive(version.Version, refusal,
                            "Повторите попытку, когда CDN станет доступен.", silent);
                        return;
                    }
                    AddLog($"🔒 Встроенная подпись архива подтверждена (версия {signed.Version})");
                    if (revocationListFailure != null)
                        AddLog($"⚠️ Список отозванных версий на CDN недоступен ({revocationListFailure}) — отзыв этого архива не проверен");
                }

                bool installed = await ExtractAndInstallClientAsync(tempZip, version.Version, token, silent);
                if (!installed) return;

                if (!silent)
                    System.Windows.MessageBox.Show(
                        $"Клиент {version.Version} успешно установлен в:\n{_clientPath}",
                        "Установка завершена", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                txtDownloadStatus.Text = "Отменено";
                progressDownload.Value = 0;
                SetOperationStage(0);
                AddLog("⏹ Загрузка отменена");
            }
            catch (Exception ex)
            {
                txtDownloadStatus.Text = "Ошибка";
                SetOperationStage(0);
                AddLog($"❌ Ошибка скачивания: {ex.Message}");
                if (!silent)
                    System.Windows.MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                // Очистка временных файлов в любом исходе: успех, ошибка, отмена
                // и ранний return (клиент запущен). Несколько попыток на случай,
                // если файл ещё держит распаковщик/антивирус.
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    try
                    {
                        if (File.Exists(tempZip)) File.Delete(tempZip);
                        break;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        try { await Task.Delay(1000); } catch { }
                    }
                    catch { break; }
                }
                btnCancelDownload.Visibility = Visibility.Collapsed;
                btnCancelDownload.IsEnabled  = true;
                btnLaunchApp.IsEnabled       = true;
                btnInstallFromFile.IsEnabled = true;
                btnInstallMissing.IsEnabled  = true;
                // Источник отмены здесь больше не освобождается: им владеет аренда
                // слота операций у вызывающего кода (OperationGate). Раньше этот метод
                // диспоузил общее поле — в том числе когда оно уже принадлежало другой,
                // ещё работающей операции.
            }
        }

        // Общие предустановочные проверки, обязательные для ЛЮБОГО способа положить
        // файлы в папку клиента: клиент не должен быть запущен (иначе его файлы
        // залочены и обновление окажется наполовину применённым) и путь установки
        // не должен пересекаться с папкой данных (иначе установка сотрёт настройки).
        //
        // Вынесены из ExtractAndInstallClientAsync без изменения поведения: блочное
        // (дельта-) обновление подменяет файлы в той же папке и несёт ровно те же
        // риски, а вторая копия этих проверок неизбежно разошлась бы с первой.
        // Возвращает false, если установку продолжать нельзя (сообщение пользователю
        // и запись в журнал уже сделаны).
        private async Task<bool> EnsureClientClosedAndPathSafeAsync(bool silent)
        {
            if (IsClientRunning())
            {
                Dispatcher.Invoke(() => txtDownloadStatus.Text = "Клиент запущен");

                if (silent)
                {
                    Dispatcher.Invoke(() => SetOperationStage(0));
                    AddLog("⏸ Установка отложена: клиент запущен");
                    return false;
                }

                var answer = Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                    "Ven4Tools сейчас запущен.\n\nЗакрыть клиент сейчас, чтобы установить эту версию?",
                    "Клиент запущен", MessageBoxButton.YesNo, MessageBoxImage.Question));

                if (answer != MessageBoxResult.Yes)
                {
                    Dispatcher.Invoke(() => SetOperationStage(0));
                    AddLog("⏹ Установка отменена — клиент не закрыт");
                    return false;
                }

                AddLog("🔒 Закрываю клиент перед установкой...");
                if (!await TryCloseRunningClientAsync())
                {
                    Dispatcher.Invoke(() =>
                    {
                        txtDownloadStatus.Text = "Клиент запущен";
                        SetOperationStage(0);
                    });
                    AddLog("⚠️ Клиент не закрылся за отведённое время — установка отменена");
                    if (!silent)
                        Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                            "Не удалось закрыть клиент автоматически (возможно, он свёрнут в трей).\n\n" +
                            "Закройте его вручную и повторите установку.",
                            "Клиент не закрылся", MessageBoxButton.OK, MessageBoxImage.Warning));
                    return false;
                }
                AddLog("✅ Клиент закрыт, продолжаю установку");
            }

            if (!InstallPathGuard.IsClientPathSafe(_clientPath, _dataFolderPath))
            {
                Dispatcher.Invoke(() =>
                {
                    txtDownloadStatus.Text = "Ошибка пути";
                    SetOperationStage(0);
                });
                AddLog($"⛔ Папка установки клиента пересекается с папкой данных — установка отменена: {_clientPath}");
                if (!silent)
                    Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                        $"Папка установки клиента:\n{_clientPath}\n\nсовпадает или вложена в папку данных Ven4Tools. " +
                        "Установка отменена во избежание потери настроек.\n\nВыберите другую папку установки.",
                        "Небезопасный путь установки", MessageBoxButton.OK, MessageBoxImage.Error));
                return false;
            }

            return true;
        }

        // Общий хвост «распаковка → закрыть запущенный клиент → проверка пути →
        // атомарная установка», используемый и сетевой загрузкой (DownloadVersionAsync),
        // и локальной установкой из файла (InstallFromLocalArchiveAsync), и CLI
        // --install-from — единый путь, чтобы не плодить два параллельных места
        // с риском разойтись друг с другом (тот же класс проблемы, что был найден
        // и исправлен в InstallationService.Choco.cs/.Winget.cs в раунде аудита
        // 2026-08-02).
        // sourceArchivePath — уже проверенный (SHA256 для сетевого пути, LocalArchiveVerifier
        // для локального) архив на диске, готовый к распаковке без дальнейших проверок.
        private async Task<bool> ExtractAndInstallClientAsync(
            string sourceArchivePath, string versionLabel, CancellationToken token, bool silent)
        {
            string clientParent = Path.GetDirectoryName(Path.GetFullPath(_clientPath))
                ?? throw new InvalidOperationException("Не удалось определить каталог установки.");
            string extractPath = Path.Combine(
                // Префикс из имени папки клиента — CleanupStaleInstallArtifacts ищет
                // остатки именно по нему; зашитый «Ven4Tools_Client» не находился у
                // клиента в папке с другим именем («Найти клиент»).
                clientParent, $".{Path.GetFileName(Path.GetFullPath(_clientPath))}.staging-{Guid.NewGuid():N}");

            try
            {
                Dispatcher.Invoke(() =>
                {
                    SetOperationStage(3); // Распаковка
                    txtDownloadStatus.Text = "Распаковка...";
                });
                await SafeZipExtractor.ExtractAsync(sourceArchivePath, extractPath, token);
                AddLog("✅ Архив безопасно распакован");

                // Слепок распакованного состава снимается ДО любого ожидания:
                // ниже EnsureClientClosedAndPathSafeAsync может держать диалог
                // «клиент запущен, закрыть?» сколь угодно долго, а staging всё это
                // время лежит в каталоге, доступном на запись процессам того же
                // пользователя. Хеш архива подтверждал архив, но не распакованные
                // из него файлы — см. StagingIntegritySnapshot.
                var stagingSnapshot = await StagingIntegritySnapshot.CaptureAsync(extractPath, token);

                token.ThrowIfCancellationRequested();

                if (!await EnsureClientClosedAndPathSafeAsync(silent)) return false;

                Dispatcher.Invoke(() =>
                {
                    SetOperationStage(4); // Установка файлов
                    txtDownloadStatus.Text = "Установка файлов...";
                });

                // Сверка вплотную к Install: между ней и переносом каталога не
                // должно быть ни ожидания пользователя, ни сетевых операций.
                string? difference = await stagingSnapshot.FindDifferenceAsync(extractPath, token);
                if (difference != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        txtDownloadStatus.Text = "Ошибка целостности";
                        SetOperationStage(0);
                    });
                    AddLog($"⛔ Распакованные файлы изменились после проверки архива — установка отменена: {difference}");
                    if (!silent)
                        Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                            "Содержимое распакованного архива изменилось между проверкой и установкой " +
                            $"({difference}).\n\nУстановка отменена. Проверьте компьютер антивирусом и " +
                            "повторите установку.",
                            "Целостность нарушена", MessageBoxButton.OK, MessageBoxImage.Error));
                    return false;
                }

                var installer = new TransactionalDirectoryInstaller();
                // Кэш состава сбрасывается до записи в папку клиента — см.
                // ClientDeltaInstaller.Apply: убитый посреди установки процесс не должен
                // оставить описание прошлой версии для следующей дельты.
                new InstalledManifestStore().Invalidate();
                installer.Install(extractPath, _clientPath, token);

                Dispatcher.Invoke(() =>
                {
                    SetOperationStage(5); // Готово
                    txtDownloadStatus.Text = "Готово";
                    progressDownload.Value = 100;
                });
                AddLog($"✅ Клиент {versionLabel} установлен");

                // Пересчёт локального манифеста установленной версии — состав папки
                // клиента только что заменён целиком, и прежний кэш ей больше не
                // соответствует. Без этого шага блочное (дельта-) обновление не
                // включилось бы никогда: ему нужен подтверждённый состав того, что
                // стоит на диске, а появиться он может только здесь — дельта сама
                // без него невозможна.
                //
                // Клиент к этому моменту уже установлен: отмена (или истёкший бюджет
                // операции) во время пересчёта не должна выдавать себя за отмену
                // установки — раньше она уходила наверх как «⏹ Загрузка отменена»,
                // и кнопка с подписью версии оставались в состоянии до установки.
                // Кэш при этом уже сброшен — следующее обновление просто будет полным.
                try
                {
                    await RefreshInstalledManifestAsync(versionLabel, token);
                }
                catch (OperationCanceledException)
                {
                    AddLog("⚠️ Подсчёт состава установленной версии прерван — следующее обновление будет полным");
                }

                // CheckExistingClient, а не голый SetLaunchButtonState: он перечитывает
                // версию с диска и обновляет подпись «Текущая версия». Раньше после
                // успешной установки кнопка становилась «Запустить», а рядом
                // продолжало висеть «Текущая версия: не установлена» — состояние,
                // которое было верным ровно до этого момента. quiet — отчёт об
                // установке в журнале уже есть.
                Dispatcher.Invoke(() => CheckExistingClient(quiet: true));
                _clientUpdateAvailable = false;
                return true;
            }
            finally
            {
                for (int attempt = 1; attempt <= 5; attempt++)
                {
                    try
                    {
                        if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);
                        break;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        try { await Task.Delay(1000); } catch { }
                    }
                    catch { break; }
                }
            }
        }

    }
}
