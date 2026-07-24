using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Helpers;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Services
{
    public partial class InstallationService
    {
        // ── Источник: прямая загрузка ──────────────────────────────────────────
        private async Task<(bool Success, string Message, AppInstallProgress Progress)?> InstallFromDirectDownloadAsync(
            AppInfo app, string primaryId, AppInstallProgress appProgress,
            IProgress<AppInstallProgress> progress, string installDrive,
            string outcomeCheckId, InstalledBaseline baseline, CancellationToken token)
        {
            if (!app.InstallerUrls.Any()) return null;

            // Прямые ссылки без SHA256 в каталоге не выполняем:
            // скачанный установщик нечем верифицировать, запускать его небезопасно.
            // Не терминальная неудача (диспетчер пробует следующий источник) —
            // appProgress здесь намеренно не трогаем (ни Status, ни Phase, ни
            // progress.Report), как и в аналогичном "нет SHA256" пропуске в
            // InstallFromCacheAsync выше: выставление Phase.Error для источника,
            // который просто пропускается, а не проваливается, красило бы полоску
            // прогресса в красный на мгновение, пока диспетчер уже перешёл к
            // следующему источнику — вводящая в заблуждение вспышка "ошибки" там,
            // где реальной терминальной неудачи ещё нет.
            if (!HashHelper.HasExpectedHash(app.Sha256))
            {
                Log($"⚠ Прямая ссылка без SHA256 для {app.DisplayName} — источник пропущен, пробую следующий");
                AppLogger.Write($"[InstallationService] ⚠ Прямая ссылка без SHA256 для {app.DisplayName} — источник пропущен, продолжаю через winget");
                InstallFailureService.Append(app.DisplayName, app.Id, "direct", "В каталоге не указан SHA256");
                return null;
            }

            // Несовпадение SHA256 у нескольких зеркал — одна запись
            // об ошибке после перебора всех ссылок, а не по одной на URL.
            int hashMismatchCount = 0;
            foreach (var url in app.InstallerUrls)
            {
                token.ThrowIfCancellationRequested();

                if (!DownloadValidator.ValidateUrl(url))
                {
                    AppLogger.Write($"[InstallationService] ⚠ Пропущен небезопасный URL (не HTTPS): {url}");
                    continue;
                }

                // Начало реальной фазы «Загрузка» — перескалировано на полный 0-100%
                // диапазон (раньше эта фаза жила в промежутке 20-50% общей шкалы,
                // что вместе с фазой установки смотрелось как одна невнятная полоска).
                appProgress.Status = "📥 Скачивание...";
                appProgress.Phase = InstallPhase.Download;
                appProgress.IsIndeterminate = false;
                appProgress.Percentage = 0;
                progress.Report(appProgress);

                string urlExt = Path.GetExtension(new Uri(url).LocalPath).ToLowerInvariant();
                if (string.IsNullOrEmpty(urlExt) || (urlExt != ".exe" && urlExt != ".msi")) urlExt = ".exe";
                // primaryId (AlternativeId ?? app.Id) уже провалидирован выше,
                // но в ветке с AlternativeId сам app.Id остаётся непроверенным — а именно
                // он идёт в имя временного файла. Тот же ValidateId, иначе безопасный плейсхолдер.
                string idForTempFile = CommandLineGuard.ValidateId(app.Id) ? app.Id : "app";
                string tempFile = Path.Combine(Path.GetTempPath(), $"{idForTempFile}_{Guid.NewGuid()}{urlExt}");
                try
                {
                    // Таймаут 30 секунд только на установление соединения и заголовки;
                    // скачивание тела дополнительно ограничено sliding-таймаутом простоя
                    // (idleCts, ниже) — без него сервер, отдающий байты бесконечно медленно
                    // (или вовсе переставший отвечать после заголовков), вешал бы загрузку
                    // до ручной отмены пользователем.
                    using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    headersCts.CancelAfter(TimeSpan.FromSeconds(30));
                    long totalRead = 0;
                    using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headersCts.Token))
                    {
                        response.EnsureSuccessStatusCode();

                        if (!DownloadValidator.ValidateAfterRedirect(response))
                        {
                            AppLogger.Write($"[InstallationService] ⚠ Редирект на небезопасный хост: {response.RequestMessage?.RequestUri?.Host}");
                            throw new InvalidOperationException($"Редирект на небезопасный хост: {response.RequestMessage?.RequestUri?.Host}");
                        }

                        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                        // Сервер не отдал Content-Length — реальный процент скачивания
                        // посчитать нечем. Честно показываем IsIndeterminate вместо
                        // застрявшего на месте (или выдуманного) числа.
                        if (totalBytes <= 0)
                        {
                            appProgress.IsIndeterminate = true;
                            progress.Report(appProgress);
                        }
                        using var contentStream = await response.Content.ReadAsStreamAsync();
                        using var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None);
                        // Сбрасывается после каждого успешного чтения — таймаут на простой между
                        // байтами, а не на всю загрузку целиком (большие легитимные файлы не рвутся).
                        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        idleCts.CancelAfter(TimeSpan.FromSeconds(60));
                        var buf = new byte[8192];
                        int bytesRead;
                        while ((bytesRead = await contentStream.ReadAsync(buf, idleCts.Token)) > 0)
                        {
                            idleCts.CancelAfter(TimeSpan.FromSeconds(60));
                            token.ThrowIfCancellationRequested();
                            await fileStream.WriteAsync(buf, 0, bytesRead, token);
                            totalRead += bytesRead;
                            if (totalBytes > 0)
                            {
                                appProgress.Percentage = (int)((double)totalRead / totalBytes * 100);
                                progress.Report(appProgress);
                            }
                        }
                    }

                    double downloadedMb = Math.Round(totalRead / 1_048_576.0, 1);
                    AppLogger.Write($"📥 Загружен установщик — {downloadedMb} МБ: {Path.GetFileName(tempFile)}");

                    // Всё ещё часть фазы «Загрузка» (файл уже получен, проверяем его
                    // целостность перед запуском) — доводим до 100%, IsIndeterminate
                    // снимаем на случай, если он включился из-за неизвестного Content-Length.
                    appProgress.Status = "🔐 Проверка SHA256...";
                    appProgress.IsIndeterminate = false;
                    appProgress.Percentage = 100;
                    progress.Report(appProgress);

                    bool tempIsMsi = tempFile.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
                    string silentArgs;
                    if (tempIsMsi)
                    {
                        // MSI запускаем через msiexec в тихом режиме
                        silentArgs = $"/i \"{tempFile}\" /quiet /norestart"
                                     + BuildInstallerLocationArg(true, installDrive, app.DisplayName);
                    }
                    else
                    {
                        silentArgs = app.SilentArgs;
                        if (!CommandLineGuard.ValidateSilentArgs(silentArgs))
                        {
                            AppLogger.Write($"[InstallationService] ⚠ SilentArgs содержит недопустимые символы для {app.DisplayName} — использую /S");
                            silentArgs = "/S";
                        }
                        if (string.IsNullOrWhiteSpace(silentArgs) && ProfileService.Current.SilentInstall)
                            silentArgs = "/S";
                        // Best-effort путь установки для прямого EXE
                        silentArgs += BuildInstallerLocationArg(false, installDrive, app.DisplayName);
                    }

                    // Держим temp-файл открытым с FileShare.Read НЕПРЕРЫВНО от проверки
                    // хеша до завершения установки — верификация читает из уже открытого
                    // хендла, а не из отдельного временного (иначе между закрытием
                    // проверочного хендла и открытием защитного остаётся окно для подмены —
                    // TOCTOU). File.Delete ниже вынесен за using: к моменту удаления хендл
                    // уже освобождён. Зеркалирует защиту лаунчера. SHA256 гарантированно
                    // указан (проверено перед циклом) — верификация выполняется всегда.
                    bool hashOk;
                    (bool Ok, bool Reboot, int ExitCode)? run = null;
                    using (var verifiedStream = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        hashOk = await HashHelper.VerifyHashAsync(verifiedStream, app.Sha256!);
                        if (hashOk)
                        {
                            Log($"✅ SHA256 OK: {app.DisplayName}");
                            token.ThrowIfCancellationRequested();
                            // Переключение фазы: Загрузка завершена, начинается Установка —
                            // сбрасываем Percentage на 0 (не тащим хвост от 100%). Гранулярного
                            // прогресса самого установщика нет (elevated чёрный ящик, формат
                            // произвольный) — честно IsIndeterminate, а не выдуманные проценты.
                            appProgress.Status = "⚙️ Установка...";
                            appProgress.Phase = InstallPhase.Installing;
                            appProgress.IsIndeterminate = true;
                            appProgress.Percentage = 0;
                            progress.Report(appProgress);

                            var psi = new ProcessStartInfo
                            {
                                FileName = tempIsMsi ? TrustedExecutablePaths.MsiExec : tempFile,
                                Arguments = silentArgs,
                                UseShellExecute = true, Verb = "runas",
                                WindowStyle = ProcessWindowStyle.Hidden
                            };

                            run = await RunElevatedInstallerAsync(psi, token,
                                pid => AppLogger.Write($"▶ Запущен процесс установки PID {pid}: {Path.GetFileName(psi.FileName)}"));
                        }
                    }
                    if (!hashOk)
                    {
                        Log($"❌ SHA256 mismatch: {app.DisplayName}");
                        try { File.Delete(tempFile); } catch { }
                        appProgress.Status = "⚠ SHA256 не совпал — пробуем следующий источник";
                        appProgress.Phase = InstallPhase.Error;
                        appProgress.IsIndeterminate = false;
                        progress.Report(appProgress);
                        hashMismatchCount++;
                        continue;
                    }
                    if (run != null)
                    {
                        try { File.Delete(tempFile); } catch { }
                        if (run.Value.Ok)
                            return await ReportInstallOutcomeAsync(app, appProgress, progress, outcomeCheckId, baseline,
                                true, run.Value.Reboot, "direct", "прямая ссылка", token, url);
                    }
                }
                // Отмена пользователем — пробрасываем; таймаут заголовков — пробуем следующий источник
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    Log($"❌ Прямая ссылка {url}: {ex.Message}");
                    try { File.Delete(tempFile); } catch { }
                }
            }
            if (hashMismatchCount > 0)
                InstallFailureService.Append(app.DisplayName, app.Id, "direct",
                    hashMismatchCount == 1
                        ? "SHA256 mismatch"
                        : $"SHA256 mismatch ({hashMismatchCount} зеркал)");
            return null;
        }
    }
}
