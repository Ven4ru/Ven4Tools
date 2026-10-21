using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Helpers;
using Ven4Tools.Launcher.Models;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>Результат сверки одного локального хеша с одним опубликованным.</summary>
    internal enum HashCheck
    {
        /// <summary>Опубликованного значения нет (источник недоступен или хеш там не публикуется).</summary>
        NoData,
        /// <summary>Совпал.</summary>
        Match,
        /// <summary>НЕ совпал.</summary>
        Mismatch
    }

    /// <summary>Сверка одного файла с источником.</summary>
    internal sealed record SourceCheck(string Source, HashCheck Result, string? Published, string? Note);

    /// <summary>Файл и итог его сверки по всем источникам.</summary>
    internal sealed class FileAuthenticity
    {
        public string Title { get; init; } = "";
        public string? LocalSha256 { get; init; }
        public string? LocalError { get; init; }
        public List<SourceCheck> Sources { get; } = new();
        public string Verdict => AuthenticityChecker.Summarize(LocalSha256, Sources);
    }

    /// <summary>Полный отчёт «Проверить подлинность».</summary>
    internal sealed class AuthenticityReport
    {
        public string LauncherVersion { get; init; } = "";
        public string? AuthenticodeInfo { get; set; }
        public FileAuthenticity Launcher { get; set; } = new();
        public List<FileAuthenticity> ClientFiles { get; } = new();
        public string? ClientVersion { get; set; }
        public string? ClientNote { get; set; }
        public List<string> PublicationNotes { get; } = new();
        public string ManifestSignatureStatus { get; set; } = "нет данных";
        public string UpdateKeyFingerprint { get; init; } = "";
        public string ClientManifestKeyFingerprint { get; init; } = "";
        public string ReleaseUrl { get; init; } = "";
    }

    /// <summary>
    /// Л7 «Проверить подлинность». Только чтение: SHA256 своего exe и ключевых файлов
    /// клиента, сверка с опубликованными хешами из двух НЕЗАВИСИМЫХ мест —
    ///   * подписанный version.json на CDN (ECDSA; ключ вшит в лаунчер), для клиента —
    ///     подписанный файловый манифест client-manifest.json (свой ключ);
    ///   * релиз на GitHub: SHA256SUMS.txt и поле digest ассетов (считает сам GitHub).
    /// Поддельный CDN не подделает GitHub, и наоборот. Чего нет — честно «нет данных».
    ///
    /// Хеш установленного exe лаунчера публикуется полями
    /// version.json → launcher.installed_exe_sha256 и строкой «Ven4Tools.Launcher.exe» в
    /// SHA256SUMS.txt релиза launcher-vX.Y.Z (с 3.12.0; см. договор стыков). У прежних
    /// релизов там только хеш установщика — для них итог «нет данных», а не «совпало».
    /// </summary>
    internal static class AuthenticityChecker
    {
        private const string VersionUrl = "https://cdn.ven4tools.ru/version.json";
        private const string VersionSignatureUrl = "https://cdn.ven4tools.ru/version.json.sig";
        private const string Repo = "Ven4ru/Ven4Tools";

        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = LauncherHttp.CreateClient(TimeSpan.FromSeconds(20));
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            return client;
        }

        /// <summary>Нормализованный SHA256 (64 hex, нижний регистр) или null.</summary>
        internal static string? NormalizeSha256(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim();
            if (v.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) v = v[7..];
            v = v.ToLowerInvariant();
            return DownloadValidator.IsValidSha256(v) ? v : null;
        }

        /// <summary>Сверка локального хеша с опубликованным. Чистая функция — покрыта unit-тестами.</summary>
        internal static HashCheck Compare(string? local, string? published)
        {
            string? l = NormalizeSha256(local);
            string? p = NormalizeSha256(published);
            if (l == null || p == null) return HashCheck.NoData;
            return l == p ? HashCheck.Match : HashCheck.Mismatch;
        }

        /// <summary>
        /// Итог по файлу. Любое несовпадение важнее любого числа совпадений.
        /// Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static string Summarize(string? localSha256, IReadOnlyCollection<SourceCheck> sources)
        {
            if (NormalizeSha256(localSha256) == null) return "Нет данных: файл не прочитан.";
            int mismatch = sources.Count(s => s.Result == HashCheck.Mismatch);
            int match = sources.Count(s => s.Result == HashCheck.Match);
            if (mismatch > 0)
                return "НЕ СОВПАДАЕТ с опубликованным хешем. Файл изменён или это не официальная сборка. " +
                       "Не запускайте его повторно, скачайте установщик с ven4tools.ru или со страницы релиза GitHub.";
            if (match >= 2) return "Подлинность подтверждена двумя независимыми источниками.";
            if (match == 1) return "Совпадает с одним источником; второй источник — нет данных.";
            return "Нет данных: опубликованный хеш получить не удалось. Это не значит, что файл подделан.";
        }

        /// <summary>
        /// Хеш файла из SHA256SUMS.txt (строки «хеш  имя» или «хеш *имя»).
        /// Чистая функция — покрыта unit-тестами.
        /// </summary>
        internal static string? FindInSha256Sums(string? sums, string fileName)
        {
            if (string.IsNullOrEmpty(sums)) return null;
            foreach (string raw in sums.Split('\n'))
            {
                string line = raw.Trim();
                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                string hash = line[..space];
                string name = line[(space + 1)..].TrimStart(' ', '*');
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                    return NormalizeSha256(hash);
            }
            return null;
        }

        /// <summary>
        /// Отпечаток публичного ключа: SHA256 от SubjectPublicKeyInfo (DER), группами по 4.
        /// Совпадает с `openssl pkey -pubin -outform DER | sha256sum`.
        /// </summary>
        internal static string KeyFingerprint(string publicKeyPem)
        {
            try
            {
                using var key = ECDsa.Create();
                key.ImportFromPem(publicKeyPem);
                string hex = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
                return string.Join(" ", Enumerable.Range(0, hex.Length / 4).Select(i => hex.Substring(i * 4, 4)));
            }
            catch (Exception ex)
            {
                return $"нет данных ({ex.Message})";
            }
        }

        /// <summary>Сведения о подписи Authenticode exe (только чтение, без проверки отзыва).</summary>
        internal static string DescribeAuthenticode(string path)
        {
            try
            {
#pragma warning disable SYSLIB0057
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
                return $"есть, издатель: {cert.GetNameInfo(X509NameType.SimpleName, false)} " +
                       "(проверка цепочки — «Свойства файла → Цифровые подписи»)";
            }
            catch
            {
                return "нет (сборка не подписана сертификатом Authenticode — подлинность подтверждается хешем ниже)";
            }
        }

        public static async Task<AuthenticityReport> RunAsync(string? clientPath, CancellationToken ct)
        {
            string version = LauncherInstallation.GetCurrentVersion();
            var report = new AuthenticityReport
            {
                LauncherVersion = version,
                UpdateKeyFingerprint = KeyFingerprint(UpdateManifestVerifier.PublicKeyPem),
                ClientManifestKeyFingerprint = KeyFingerprint(ClientManifestVerifier.PublicKeyPem),
                ReleaseUrl = $"https://github.com/{Repo}/releases/tag/launcher-v{version}",
            };

            // 1. Свой exe.
            string exe = Environment.ProcessPath ?? LauncherInstallation.GetCurrentExePath();
            string? exeHash = null, exeError = null;
            try { exeHash = await FileHashHelper.ComputeSha256Async(exe, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { exeError = ex.Message; }
            report.AuthenticodeInfo = string.IsNullOrEmpty(exe) ? null : DescribeAuthenticode(exe);
            report.Launcher = new FileAuthenticity
            {
                Title = $"Лаунчер {version} ({LauncherInstallation.ExeName})",
                LocalSha256 = exeHash,
                LocalError = exeError,
            };

            // 2. Подписанный version.json (CDN).
            CdnVersionInfo? cdn = null;
            try
            {
                string json = await BoundedHttpText.GetStringAsync(_http, VersionUrl, ct).ConfigureAwait(false);
                string sig = await BoundedHttpText.GetStringAsync(_http, VersionSignatureUrl, ct).ConfigureAwait(false);
                if (UpdateManifestVerifier.Verify(json, sig))
                {
                    cdn = JsonSerializer.Deserialize<CdnVersionInfo>(json);
                    report.ManifestSignatureStatus = "подпись version.json подтверждена (ECDSA P-256)";
                }
                else
                {
                    report.ManifestSignatureStatus = "ПОДПИСЬ version.json НЕ ПОДТВЕРЖДЕНА — данным CDN лаунчер не доверяет";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                report.ManifestSignatureStatus = $"version.json не получен ({ex.GetType().Name}) — нет данных";
            }

            string? cdnExe = string.Equals(cdn?.Launcher?.Version, version, StringComparison.OrdinalIgnoreCase)
                ? cdn?.Launcher?.InstalledExeSha256
                : null;
            report.Launcher.Sources.Add(new SourceCheck("CDN (подписанный version.json)", Compare(exeHash, cdnExe), NormalizeSha256(cdnExe),
                cdn == null ? "манифест недоступен" :
                cdn.Launcher?.Version != version ? $"на CDN описана версия {cdn.Launcher?.Version ?? "?"}" :
                cdnExe == null ? "хеш установленного exe не публикуется" : null));

            // 3. GitHub: релиз лаунчера этой версии.
            var (ghAssets, ghError) = await GetReleaseAssetsAsync($"launcher-v{version}", ct).ConfigureAwait(false);
            string? sums = null;
            var sumsAsset = ghAssets?.FirstOrDefault(a => string.Equals(a.Name, "SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
            if (sumsAsset?.Url != null && DownloadValidator.IsAllowedDownloadHost(sumsAsset.Url))
            {
                try { sums = await BoundedHttpText.GetStringAsync(_http, sumsAsset.Url, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { ghError ??= ex.Message; }
            }
            string? ghExe = FindInSha256Sums(sums, LauncherInstallation.ExeName);
            report.Launcher.Sources.Add(new SourceCheck("GitHub (SHA256SUMS.txt релиза)", Compare(exeHash, ghExe), ghExe,
                ghAssets == null ? $"релиз недоступен ({ghError})" : ghExe == null ? "хеш установленного exe не публикуется" : null));

            // Согласованность публикации установщика: CDN и GitHub описывают один и тот же файл.
            string setupName = $"Ven4Tools.Setup-{version}.exe";
            string? cdnSetup = string.Equals(cdn?.Launcher?.Version, version, StringComparison.OrdinalIgnoreCase)
                ? NormalizeSha256(cdn?.Launcher?.SetupSha256) : null;
            string? ghSetup = FindInSha256Sums(sums, setupName)
                ?? NormalizeSha256(ghAssets?.FirstOrDefault(a => string.Equals(a.Name, setupName, StringComparison.OrdinalIgnoreCase))?.Digest);
            report.PublicationNotes.Add(DescribePublication($"Установщик {setupName}", cdnSetup, ghSetup));

            // 4. Клиент.
            await CheckClientAsync(report, clientPath, cdn, ct).ConfigureAwait(false);
            return report;
        }

        /// <summary>Строка о согласованности двух опубликованных хешей. Чистая функция — покрыта unit-тестами.</summary>
        internal static string DescribePublication(string what, string? cdnSha, string? githubSha) =>
            Compare(cdnSha, githubSha) switch
            {
                HashCheck.Match => $"{what}: хеши на CDN и GitHub совпадают ({cdnSha![..12]}…).",
                HashCheck.Mismatch => $"{what}: хеши на CDN и GitHub РАЗНЫЕ — сообщите разработчику.",
                _ => $"{what}: сверить CDN и GitHub не удалось — нет данных " +
                     $"(CDN: {(cdnSha == null ? "нет" : "есть")}, GitHub: {(githubSha == null ? "нет" : "есть")}).",
            };

        private static async Task CheckClientAsync(AuthenticityReport report, string? clientPath, CdnVersionInfo? cdn, CancellationToken ct)
        {
            string? exe = string.IsNullOrEmpty(clientPath) ? null : Path.Combine(clientPath, LauncherPaths.ClientExeName);
            if (exe == null || !File.Exists(exe))
            {
                report.ClientNote = "Клиент не установлен — проверять нечего.";
                return;
            }

            string? installed = null;
            try { installed = FileVersionInfo.GetVersionInfo(exe).FileVersion; } catch { /* версия не читается — ниже «нет данных» */ }
            string? installed3 = installed == null ? null : string.Join('.', installed.Split('.').Take(3));
            report.ClientVersion = installed3;

            // Подписанный файловый манифест — только для опубликованной сейчас версии.
            ClientFileManifest? manifest = null;
            string manifestNote;
            var c = cdn?.Client;
            if (c == null)
                manifestNote = "манифест CDN недоступен";
            else if (installed3 == null || VersionComparer.Compare(c.Version ?? "", installed3) != 0)
                manifestNote = $"опубликован манифест только версии {c.Version ?? "?"}, установлена {installed3 ?? "неизвестная"}";
            else if (string.IsNullOrEmpty(c.ManifestUrl) || string.IsNullOrEmpty(c.ManifestSignatureUrl))
                manifestNote = "файловый манифест не опубликован";
            else
            {
                var fetched = await ClientManifestFetcher.FetchAsync(_http, c.ManifestUrl, c.ManifestSignatureUrl, ct).ConfigureAwait(false);
                manifest = fetched?.Manifest;
                manifestNote = manifest == null ? "манифест не получен или его подпись не подтверждена" : "";
            }

            // Запись установки — то, что лаунчер сам положил на диск и проверил при установке.
            ClientFileManifest? installedRecord = null;
            try
            {
                var rec = new InstalledManifestStore().Load();
                if (rec != null && InstalledManifestStore.MatchesClientFolder(rec, clientPath!)) installedRecord = rec;
            }
            catch { /* записи нет — «нет данных» */ }

            foreach (string name in new[] { LauncherPaths.ClientExeName, "Ven4Tools.dll" })
            {
                string path = Path.Combine(clientPath!, name);
                string? hash = null, error = null;
                try { hash = await FileHashHelper.ComputeSha256Async(path, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { error = ex.Message; }

                var file = new FileAuthenticity { Title = $"Клиент {installed3 ?? "?"} ({name})", LocalSha256 = hash, LocalError = error };
                string? signed = manifest?.Files?.FirstOrDefault(f => string.Equals(f.Path, name, StringComparison.OrdinalIgnoreCase))?.Sha256;
                file.Sources.Add(new SourceCheck("CDN (подписанный client-manifest.json)", Compare(hash, signed), NormalizeSha256(signed),
                    manifest == null ? manifestNote : null));
                string? recorded = installedRecord?.Files?.FirstOrDefault(f => string.Equals(f.Path, name, StringComparison.OrdinalIgnoreCase))?.Sha256;
                file.Sources.Add(new SourceCheck("Запись установки лаунчера (проверена при установке)", Compare(hash, recorded), NormalizeSha256(recorded),
                    installedRecord == null ? "записи установки нет" : null));
                report.ClientFiles.Add(file);
            }

            // Архив клиента: CDN (подписанный) и GitHub (digest ассета) описывают одно и то же.
            if (c?.Version != null)
            {
                string zipName = $"Ven4Tools-Client-{c.Version}.zip";
                var (assets, _) = await GetReleaseAssetsAsync($"v{c.Version}", ct).ConfigureAwait(false);
                string? ghZip = NormalizeSha256(assets?.FirstOrDefault(a => string.Equals(a.Name, zipName, StringComparison.OrdinalIgnoreCase))?.Digest);
                report.PublicationNotes.Add(DescribePublication($"Архив {zipName}", NormalizeSha256(c.ZipSha256), ghZip));
            }
        }

        /// <summary>
        /// Есть ли в отчёте несовпадение (окно красит итог красным).
        /// </summary>
        internal static bool HasMismatch(AuthenticityReport r) =>
            r.Launcher.Sources.Concat(r.ClientFiles.SelectMany(f => f.Sources)).Any(s => s.Result == HashCheck.Mismatch);

        /// <summary>Текст отчёта для окна и буфера обмена (без путей и имён ПК/пользователя).</summary>
        internal static string Format(AuthenticityReport r)
        {
            var sb = new System.Text.StringBuilder();
            void AppendFile(FileAuthenticity f)
            {
                sb.AppendLine(f.Title);
                sb.AppendLine($"  SHA256: {f.LocalSha256 ?? "не прочитан" + (f.LocalError != null ? $" ({f.LocalError})" : "")}");
                foreach (var s in f.Sources)
                {
                    string res = s.Result switch
                    {
                        HashCheck.Match => "совпадает",
                        HashCheck.Mismatch => $"НЕ СОВПАДАЕТ (опубликовано {s.Published})",
                        _ => "нет данных" + (s.Note != null ? $" — {s.Note}" : ""),
                    };
                    sb.AppendLine($"  {s.Source}: {res}");
                }
                sb.AppendLine($"  Итог: {f.Verdict}");
                sb.AppendLine();
            }

            AppendFile(r.Launcher);
            if (r.AuthenticodeInfo != null) sb.AppendLine($"Подпись Authenticode лаунчера: {r.AuthenticodeInfo}").AppendLine();
            if (r.ClientNote != null) sb.AppendLine(r.ClientNote).AppendLine();
            foreach (var f in r.ClientFiles) AppendFile(f);

            sb.AppendLine("Публикация:");
            foreach (var n in r.PublicationNotes) sb.AppendLine($"  {n}");
            sb.AppendLine();
            sb.AppendLine($"Манифест обновлений: {r.ManifestSignatureStatus}");
            sb.AppendLine($"Отпечаток ключа подписи version.json (SHA256 SPKI): {r.UpdateKeyFingerprint}");
            sb.AppendLine($"Отпечаток ключа подписи client-manifest.json: {r.ClientManifestKeyFingerprint}");
            return PersonalDataSanitizer.Sanitize(sb.ToString().TrimEnd());
        }

        private sealed record ReleaseAsset(string? Name, string? Url, string? Digest);

        private static async Task<(List<ReleaseAsset>? Assets, string? Error)> GetReleaseAssetsAsync(string tag, CancellationToken ct)
        {
            try
            {
                string json = await BoundedHttpText.GetStringAsync(_http,
                    $"https://api.github.com/repos/{Repo}/releases/tags/{Uri.EscapeDataString(tag)}", ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var list = new List<ReleaseAsset>();
                if (doc.RootElement.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        list.Add(new ReleaseAsset(
                            a.TryGetProperty("name", out var n) ? n.GetString() : null,
                            a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null,
                            a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null));
                    }
                }
                return (list, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return (null, ex is HttpRequestException { StatusCode: { } code } ? $"GitHub ответил {(int)code}" : ex.GetType().Name);
            }
        }
    }
}
