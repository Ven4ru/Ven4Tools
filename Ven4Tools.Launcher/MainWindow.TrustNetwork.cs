using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Ven4Tools.Launcher.Helpers;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Launcher
{
    /// <summary>
    /// Л3 «похоже, файл удалил антивирус» и Л5 Smart App Control: крючки, которые
    /// вызываются одной строкой из установки, восстановления и запуска клиента
    /// (места перечислены в договоре стыков лаунчера 3.12). Только чтение и объяснение:
    /// ничего не выключается и не добавляется в исключения за пользователя.
    /// </summary>
    public partial class MainWindow
    {
        // Ключевые файлы сборки клиента: меняются в каждом релизе и первыми попадают под
        // подозрение антивируса (exe — точка запуска, dll — весь код клиента).
        private static readonly string[] KeyClientFiles = { LauncherPaths.ClientExeName, "Ven4Tools.dll" };

        // Антивирус удаляет файл не сразу, а после сканирования записи — даём ему время.
        private static readonly TimeSpan[] VanishCheckDelays = { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(25) };

        // Одно окно объяснения на сеанс для одного и того же случая — без лавины окон.
        private readonly HashSet<string> _trustNoticesShown = new(StringComparer.OrdinalIgnoreCase);

        // Предупреждение о Smart App Control — один раз за сеанс.
        private bool _smartAppControlWarned;

        /// <summary>
        /// Крючок после успешной установки/обновления/восстановления клиента: сразу
        /// снимает SHA256 ключевых файлов, затем дважды проверяет, что они на месте.
        /// Исчезли — объясняет (антивирус или нет доступа) с хешем пропавшего файла.
        /// Ничего не ждёт от вызывающего: запускается как «_ = …».
        /// </summary>
        internal async Task WatchClientFilesAfterWriteAsync(string clientPath)
        {
            if (_isUiTestMode || string.IsNullOrEmpty(clientPath)) return;
            try
            {
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in KeyClientFiles)
                {
                    string path = Path.Combine(clientPath, name);
                    try
                    {
                        if (File.Exists(path))
                            hashes[name] = await FileHashHelper.ComputeSha256Async(path, default).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Не прочитали сразу после записи — это уже признак: проверим ниже.
                        LauncherLog.Write($"[Антивирус] {name} не читается сразу после записи: {ex.GetType().Name}");
                    }
                }

                foreach (var delay in VanishCheckDelays)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    foreach (string name in KeyClientFiles)
                    {
                        string path = Path.Combine(clientPath, name);
                        if (File.Exists(path) && CanOpenForRead(path)) continue;
                        hashes.TryGetValue(name, out var sha);
                        AddLog($"⚠️ Файл клиента {name} пропал или заблокирован вскоре после записи — проверяю, не антивирус ли это");
                        await ShowFileProblemAsync(clientPath, name, sha, "SHA256 файла (снят сразу после установки)",
                            new FileProblemEvidence { FileVanished = true }).ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"[Антивирус] Проверка файлов после записи не выполнена: {ex.Message}");
            }
        }

        private static bool CanOpenForRead(string path)
        {
            try
            {
                using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return true;
            }
            catch (IOException ex) when (AntivirusAdvisor.ExtractWin32Error(ex) is 32 or 33)
            {
                // Файл занят (сканирует антивирус, индексатор) — это не пропажа.
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Крючок в обработчике ошибки установки: если запись отклонена (нет доступа,
        /// «обнаружен вирус», файл исчез посреди установки) — показывает своё окно с
        /// разбором и возвращает true; иначе false, и вызывающий показывает прежнее сообщение.
        /// </summary>
        internal bool ShowWriteFailureExplanation(Exception ex, string? archiveSha256 = null)
        {
            int? code = AntivirusAdvisor.ExtractWin32Error(ex);
            if (code is not (AntivirusAdvisor.ErrorAccessDenied or AntivirusAdvisor.ErrorVirusInfected or
                AntivirusAdvisor.ErrorVirusDeleted or AntivirusAdvisor.ErrorFileNotFound or AntivirusAdvisor.ErrorPathNotFound))
                return false;

            string clientPath = _clientPath;
            _ = ShowFileProblemAsync(clientPath, null, archiveSha256, "SHA256 архива клиента",
                new FileProblemEvidence { Win32Error = code }, probeFolder: true);
            return true;
        }

        /// <summary>
        /// Крючок в обработчике ошибки запуска клиента: политика (Smart App Control,
        /// WDAC, AppLocker) или антивирус. true — объяснение показано.
        /// </summary>
        internal bool ShowLaunchFailureExplanation(Exception ex, string clientExe)
        {
            int? code = AntivirusAdvisor.ExtractWin32Error(ex);
            string clientPath = Path.GetDirectoryName(clientExe) ?? _clientPath;

            if (SmartAppControl.IsPolicyBlock(code))
            {
                int error = code!.Value;
                AddLog(error == SmartAppControl.ErrorSystemIntegrityPolicyViolation
                    ? "⛔ Запуск клиента заблокирован политикой целостности кода Windows (Smart App Control или WDAC)"
                    : "⛔ Запуск клиента запрещён политикой Windows (AppLocker/SRP)");
                _ = Task.Run(() =>
                {
                    var state = SmartAppControl.Read();
                    bool? evt = error == SmartAppControl.ErrorSystemIntegrityPolicyViolation
                        ? SmartAppControl.FindRecentBlockEvent(Path.GetFileName(clientExe), TimeSpan.FromMinutes(5))
                        : null;
                    LauncherLog.Write($"[Smart App Control] Состояние: {SmartAppControl.Describe(state)}; событие 3077: {(evt == null ? "нет данных" : evt.Value ? "есть" : "нет")}");
                    string body = SmartAppControl.BuildLaunchBlockedText(error, state, evt);
                    Dispatcher.InvokeAsync(() => ShowTrustNotice("launch-policy", "Windows заблокировала запуск клиента", body, null, null, null));
                });
                return true;
            }

            if (code is AntivirusAdvisor.ErrorVirusInfected or AntivirusAdvisor.ErrorVirusDeleted
                or AntivirusAdvisor.ErrorFileNotFound or AntivirusAdvisor.ErrorAccessDenied)
            {
                string? sha = ExpectedInstalledHash(LauncherPaths.ClientExeName);
                bool vanished = code == AntivirusAdvisor.ErrorFileNotFound && !File.Exists(clientExe);
                _ = ShowFileProblemAsync(clientPath, LauncherPaths.ClientExeName, sha, "SHA256 файла по записи установки",
                    new FileProblemEvidence { Win32Error = code, FileVanished = vanished }, probeFolder: true);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Крючок «Проверить и восстановить»: exe клиента нет, хотя лаунчер его ставил
        /// (есть запись установки). Объясняет с ожидаемым хешем пропавшего файла.
        /// </summary>
        internal void ExplainMissingClientExe(string clientPath)
        {
            string? sha = ExpectedInstalledHash(LauncherPaths.ClientExeName);
            bool folderHasFiles;
            try { folderHasFiles = Directory.Exists(clientPath) && Directory.EnumerateFileSystemEntries(clientPath).Any(); }
            catch { folderHasFiles = false; }
            // Пустой или отсутствующий каталог без записи установки — клиент просто не ставили.
            if (sha == null && !folderHasFiles) return;
            _ = ShowFileProblemAsync(clientPath, LauncherPaths.ClientExeName, sha, "SHA256 файла по записи установки",
                new FileProblemEvidence { FileVanished = true });
        }

        /// <summary>
        /// Крючок перед ПЕРВОЙ установкой клиента: если Smart App Control может
        /// заблокировать запуск — честно предупреждает и спрашивает, продолжать ли.
        /// false — пользователь отказался. Ничего в Windows не меняет.
        /// </summary>
        internal bool ConfirmSmartAppControlBeforeInstall()
        {
            if (_isUiTestMode || _smartAppControlWarned) return true;
            var state = SmartAppControl.Read();
            LauncherLog.Write($"[Smart App Control] Перед установкой: {SmartAppControl.Describe(state)}");
            if (!SmartAppControl.MayBlock(state)) return true;

            _smartAppControlWarned = true;
            AddLog($"⚠️ Smart App Control: {SmartAppControl.Describe(state)} — запуск неподписанного клиента может быть заблокирован");
            var answer = MessageBox.Show(SmartAppControl.BuildPreInstallWarning(state), "Smart App Control",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return answer == MessageBoxResult.Yes;
        }

        // Хеш файла из записи последней успешной установки (если она относится к этой папке).
        private string? ExpectedInstalledHash(string relativePath)
        {
            try
            {
                var manifest = new InstalledManifestStore().Load();
                return manifest?.Files?.FirstOrDefault(f =>
                    string.Equals(f.Path, relativePath, StringComparison.OrdinalIgnoreCase))?.Sha256;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Сбор признаков (пробная запись, Центр безопасности, детекты Defender) в фоне
        /// и показ окна на UI-потоке.
        /// </summary>
        private async Task ShowFileProblemAsync(string clientPath, string? fileName, string? sha256, string hashLabel,
            FileProblemEvidence evidence, bool probeFolder = false)
        {
            try
            {
                var (kind, products, detections) = await Task.Run(() =>
                {
                    bool? writable = probeFolder || evidence.Win32Error == AntivirusAdvisor.ErrorAccessDenied
                        ? AntivirusAdvisor.ProbeFolderWritable(clientPath)
                        : evidence.FolderWritable;
                    var products = AntivirusAdvisor.QueryAntivirusProducts();
                    var detections = AntivirusAdvisor.QueryDefenderDetections(clientPath);
                    var full = new FileProblemEvidence
                    {
                        Win32Error = evidence.Win32Error,
                        FileVanished = evidence.FileVanished,
                        FolderWritable = writable,
                        DefenderDetectionMatched = detections is { Count: > 0 },
                    };
                    return (AntivirusAdvisor.Classify(full), products, detections);
                }).ConfigureAwait(false);

                LauncherLog.Write($"[Антивирус] Вердикт: {kind}; антивирусы: " +
                    (products == null ? "нет данных" : string.Join(", ", products.Select(p => p.Name))) +
                    $"; детектов Defender в папке клиента: {(detections == null ? "нет данных" : detections.Count.ToString())}");
                if (kind == FileProblemKind.None) return;

                var (title, body) = AntivirusAdvisor.BuildExplanation(kind, products, detections, fileName);
                AddLog($"⚠️ {title}");
                var links = kind == FileProblemKind.AccessDenied
                    ? null
                    : AntivirusAdvisor.BuildLinks(sha256, products?.Select(p => p.Name).ToList());
                string key = $"file:{kind}:{fileName}";
                await Dispatcher.InvokeAsync(() => ShowTrustNotice(key, title, body, kind == FileProblemKind.AccessDenied ? null : sha256, hashLabel, links));
            }
            catch (Exception ex)
            {
                LauncherLog.Write($"[Антивирус] Разбор не выполнен: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void ShowTrustNotice(string key, string title, string body, string? sha256, string? hashLabel,
            IReadOnlyList<AdviceLink>? links)
        {
            if (!_trustNoticesShown.Add(key)) return;
            var win = new TrustNoticeWindow(title, body, sha256, hashLabel, links);
            // Лаунчер может жить в трее (тихое автообновление): окно-объяснение всё равно
            // нужно показать, но без скрытого владельца — иначе оно появится невидимым.
            if (IsVisible) win.Owner = this;
            else win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.Show();
        }
    }
}
