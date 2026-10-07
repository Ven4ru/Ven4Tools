using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Ven4Tools.Localization
{
    /// <summary>
    /// Где лежит языковой пакет именно этой сборки программы и чем он проверяется.
    /// Контрольная сумма вшита в программу при сборке: принимается только тот файл
    /// перевода, с которым программа собиралась, откуда бы он ни пришёл.
    /// </summary>
    public sealed record LanguagePackSource(
        string Component,                 // "client" или "launcher"
        string Language,                  // "en"
        string Sha256,                    // ожидаемая контрольная сумма файла пакета
        IReadOnlyList<string> Urls);      // откуда скачивать, по порядку: CDN, зеркало, GitHub

    /// <summary>
    /// Поиск, проверка и загрузка языкового пакета.
    ///
    /// При установке ставится только выбранный язык. Русский не требует файла вовсе (он
    /// в самой программе), а пакет другого языка либо кладётся рядом с программой
    /// установщиком, либо скачивается позже — при первом запуске или при смене языка в
    /// настройках: сначала с CDN, затем с зеркала сайта, затем из релиза на GitHub.
    /// Проверенный пакет сохраняется в профиле пользователя и дальше берётся оттуда.
    /// </summary>
    public static class LanguagePackStore
    {
        private const int MaxPackBytes = 8 * 1024 * 1024;

        public static string CacheDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools", "lang");

        private static string ShortHash(LanguagePackSource source) => source.Sha256.Length >= 12 ? source.Sha256[..12].ToLowerInvariant() : "unknown";

        public static string CachePath(LanguagePackSource source) =>
            Path.Combine(CacheDirectory, $"{source.Component}-{source.Language}-{ShortHash(source)}.json");

        /// <summary>
        /// Пакет без обращения к сети: из профиля пользователя либо из папки программы
        /// (<paramref name="seedDirectory"/>\lang\&lt;язык&gt;.json — туда его кладёт
        /// установщик). null — пакета нет или он не тот: тогда его нужно скачать.
        /// </summary>
        public static LanguagePack? TryLoadLocal(LanguagePackSource source, string? seedDirectory, Action<string>? log = null)
        {
            foreach (string path in LocalCandidates(source, seedDirectory))
            {
                try
                {
                    if (!File.Exists(path) || new FileInfo(path).Length > MaxPackBytes) continue;
                    byte[] bytes = File.ReadAllBytes(path);
                    if (!Matches(bytes, source.Sha256))
                    {
                        log?.Invoke($"[Язык] Пакет {Path.GetFileName(path)} не от этой сборки — пропущен");
                        continue;
                    }
                    return LanguagePack.Parse(bytes);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    log?.Invoke($"[Язык] Пакет {Path.GetFileName(path)} не прочитан: {ex.Message}");
                }
            }
            return null;
        }

        private static IEnumerable<string> LocalCandidates(LanguagePackSource source, string? seedDirectory)
        {
            yield return CachePath(source);
            if (!string.IsNullOrEmpty(seedDirectory))
                yield return Path.Combine(seedDirectory, "lang", source.Language + ".json");
        }

        /// <summary>
        /// Скачивает пакет, проверяет контрольную сумму и сохраняет в профиле пользователя.
        /// <paramref name="save"/> — запись файла средствами самой программы (у клиента и
        /// лаунчера свои защищённые способы записи в профиль).
        /// </summary>
        public static async Task<LanguagePack?> DownloadAsync(
            LanguagePackSource source, HttpClient http, Func<string, byte[], Task> save,
            Action<string>? log = null, CancellationToken ct = default)
        {
            foreach (string url in source.Urls)
            {
                try
                {
                    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        log?.Invoke($"[Язык] {new Uri(url).Host}: ответ {(int)response.StatusCode}");
                        continue;
                    }
                    if (response.Content.Headers.ContentLength is > MaxPackBytes) continue;

                    byte[] bytes = await ReadLimitedAsync(response, ct);
                    if (!Matches(bytes, source.Sha256))
                    {
                        log?.Invoke($"[Язык] {new Uri(url).Host}: контрольная сумма пакета не совпала");
                        continue;
                    }
                    var pack = LanguagePack.Parse(bytes);
                    try
                    {
                        await save(CachePath(source), bytes);
                        RemoveOutdated(source);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Не сохранился — пакет всё равно годен на этот запуск.
                        log?.Invoke($"[Язык] Пакет не сохранён: {ex.Message}");
                    }
                    log?.Invoke($"[Язык] Пакет «{source.Language}» получен: {new Uri(url).Host}");
                    return pack;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
                {
                    if (ct.IsCancellationRequested) return null;
                    log?.Invoke($"[Язык] {new Uri(url).Host}: {ex.Message}");
                }
            }
            return null;
        }

        private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxPackBytes) throw new IOException("Языковой пакет больше допустимого размера.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        private static bool Matches(byte[] bytes, string expectedSha256) =>
            Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Стандартный набор адресов пакета: CDN, зеркало сайта, релиз на GitHub.
        /// Имя файла привязано к версии программы, а годность — к вшитой контрольной сумме.
        /// </summary>
        public static IReadOnlyList<string> DefaultUrls(string component, string language, string version)
        {
            string file = $"{component}-{version}-{language}.json";
            bool launcher = string.Equals(component, "launcher", StringComparison.Ordinal);
            string tag = launcher ? "launcher-v" + version : "v" + version;
            string asset = $"Ven4Tools-{(launcher ? "Launcher" : "Client")}-{version}-lang-{language}.json";
            return new[]
            {
                $"https://cdn.ven4tools.ru/releases/lang/{file}",
                $"https://ven4tools.ru/releases/lang/{file}",
                $"https://github.com/Ven4ru/Ven4Tools/releases/download/{tag}/{asset}",
            };
        }

        /// <summary>
        /// Заранее кладёт в профиль пакет другой программы (лаунчер — пакет клиента сразу
        /// после его установки). Контрольной суммы той программы здесь нет, поэтому файл
        /// сохраняется под собственной суммой: программа найдёт его, только если это её пакет.
        /// </summary>
        public static async Task<bool> PrefetchAsync(
            string component, string language, IReadOnlyList<string> urls, HttpClient http,
            Func<string, byte[], Task> save, Action<string>? log = null, CancellationToken ct = default)
        {
            foreach (string url in urls)
            {
                try
                {
                    using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxPackBytes) continue;
                    byte[] bytes = await ReadLimitedAsync(response, ct);
                    if (!string.Equals(LanguagePack.Parse(bytes).Language, language, StringComparison.OrdinalIgnoreCase)) continue;

                    string sha256 = Convert.ToHexString(SHA256.HashData(bytes));
                    var source = new LanguagePackSource(component, language, sha256, urls);
                    await save(CachePath(source), bytes);
                    RemoveOutdated(source);
                    log?.Invoke($"[Язык] Пакет «{language}» для «{component}» получен заранее: {new Uri(url).Host}");
                    return true;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                               or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    if (ct.IsCancellationRequested) return false;
                    log?.Invoke($"[Язык] {new Uri(url).Host}: {ex.Message}");
                }
            }
            return false;
        }

        // В профиле остаётся один пакет на программу и язык: прежние сборки свои файлы не подчищают.
        private static void RemoveOutdated(LanguagePackSource current)
        {
            try
            {
                string keep = CachePath(current);
                foreach (string path in Directory.EnumerateFiles(CacheDirectory, $"{current.Component}-{current.Language}-*.json"))
                {
                    if (!string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Какой язык показывать. Выбор один на лаунчер и клиент и хранится в реестре
    /// пользователя: его записывает установщик и настройки обеих программ.
    /// </summary>
    public static class AppLanguage
    {
        public const string Russian = "ru";
        public const string English = "en";
        private const string RegistryPath = @"Software\Ven4Tools";
        private const string RegistryValue = "Language";

        /// <summary>
        /// Порядок: общий выбор (установщик, настройки) → <paramref name="fallbackSetting"/>
        /// (значение из собственных настроек программы: «ru», «en», «auto» или пусто) →
        /// язык Windows (русский для русской системы, английский для любой другой).
        /// </summary>
        public static string Resolve(string? fallbackSetting)
        {
            // Для проверок и снимков экрана: язык на один запуск, без записи в настройки.
            string? forced = Environment.GetEnvironmentVariable("VEN4TOOLS_LANG");
            if (IsSupported(forced)) return forced!.ToLowerInvariant();

            string? saved = SavedChoice();
            if (IsSupported(saved)) return saved!.ToLowerInvariant();
            if (IsSupported(fallbackSetting)) return fallbackSetting!.ToLowerInvariant();
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == Russian ? Russian : English;
        }

        public static bool IsSupported(string? language) =>
            string.Equals(language, Russian, StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, English, StringComparison.OrdinalIgnoreCase);

        /// <summary>Сохранённый выбор языка; null — выбора нет («как в системе»).</summary>
        public static string? SavedChoice()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
                return key?.GetValue(RegistryValue) as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }

        /// <summary>Запоминает выбор языка; значение не из списка («auto») выбор снимает.</summary>
        public static void SaveChoice(string? language)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
                if (IsSupported(language)) key.SetValue(RegistryValue, language!.ToLowerInvariant(), RegistryValueKind.String);
                else key.DeleteValue(RegistryValue, throwOnMissingValue: false);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }
    }
}
