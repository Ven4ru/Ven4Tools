using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Ven4Tools.Launcher.Helpers;
using Ven4Tools.Localization;

namespace Ven4Tools.Launcher.Services
{
    /// <summary>
    /// Язык интерфейса лаунчера.
    ///
    /// Русский — в самой программе. Английский — отдельный языковой пакет: установщик
    /// кладёт его рядом с лаунчером, если при установке выбран английский, а при смене
    /// языка в настройках пакет скачивается (CDN, зеркало сайта, GitHub). Выбор языка
    /// общий с клиентом. Язык меняется при запуске: открытые окна на лету не переводятся.
    /// </summary>
    internal static class LauncherLanguage
    {
        /// <summary>Язык, на котором интерфейс показан сейчас.</summary>
        public static string Current { get; private set; } = AppLanguage.Russian;

        /// <summary>Язык, который выбран (пользователем, установщиком или по языку Windows).</summary>
        public static string Wanted { get; private set; } = AppLanguage.Russian;

        private static readonly HttpClient _http = CreateClient();
        private static Task<LanguagePack?>? _download;
        private static string? _clientPackRequested;

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.Add("User-Agent", "Ven4Tools-Launcher");
            return client;
        }

        private static string LauncherVersion =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        private static LanguagePackSource EnglishPack => new(
            LanguagePackInfo.Component, AppLanguage.English, LanguagePackInfo.EnSha256,
            LanguagePackStore.DefaultUrls(LanguagePackInfo.Component, AppLanguage.English, LauncherVersion));

        private static Task Save(string path, byte[] bytes)
        {
            FileHelper.WriteAllBytesAtomic(path, bytes);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Первый шаг запуска, до любых окон: определяет язык и включает перевод. Пакета
        /// на диске нет — коротко ждёт загрузку (<paramref name="wait"/>; ноль — в сеть
        /// не ходить). Не дождался — запуск идёт по-русски, загрузка продолжается в фоне,
        /// и пакет применится при следующем запуске.
        /// </summary>
        public static void Start(TimeSpan wait)
        {
            Wanted = AppLanguage.Resolve(null);
            bool collectOnly = Wanted != AppLanguage.English;
            if (collectOnly && !UiTranslator.CollectRequested) return;

            var pack = LanguagePackStore.TryLoadLocal(EnglishPack, AppContext.BaseDirectory, LauncherLog.Write);
            if (pack == null && !collectOnly && wait > TimeSpan.Zero)
            {
                // Task.Run: продолжения загрузки не должны ждать поток интерфейса,
                // который как раз занят этим ожиданием.
                _download = Task.Run(() => LanguagePackStore.DownloadAsync(EnglishPack, _http, Save, LauncherLog.Write));
                try
                {
                    if (_download.Wait(wait)) pack = _download.Result;
                    else LauncherLog.Write("[Язык] Пакет не успел загрузиться к запуску — интерфейс останется русским до следующего запуска");
                }
                catch (AggregateException ex)
                {
                    LauncherLog.Write($"[Язык] Пакет не получен: {ex.InnerException?.Message ?? ex.Message}");
                }
            }
            if (pack == null) return;

            UiTranslator.Activate(pack, collectOnly);
            if (!collectOnly) Current = AppLanguage.English;
        }

        /// <summary>Выбор для списка в настройках: «auto», «ru» или «en».</summary>
        public static string Setting
        {
            get
            {
                string? saved = AppLanguage.SavedChoice();
                return AppLanguage.IsSupported(saved) ? saved!.ToLowerInvariant() : "auto";
            }
        }

        /// <summary>
        /// Запоминает выбор из настроек. Возвращает true, если показанный сейчас язык
        /// отличается от выбранного и нужен перезапуск.
        /// </summary>
        public static bool Choose(string setting)
        {
            // Проверки интерфейса работают в своей песочнице и общий выбор языка не трогают.
            if (Environment.GetEnvironmentVariable("VEN4TOOLS_UI_TEST") == "1") return false;
            AppLanguage.SaveChoice(setting);
            Wanted = AppLanguage.Resolve(null);
            if (Wanted == AppLanguage.English && !UiTranslator.IsActive)
            {
                // Пакет подтягивается сразу, чтобы после перезапуска он уже был на диске.
                _download ??= Task.Run(() => LanguagePackStore.DownloadAsync(EnglishPack, _http, Save, LauncherLog.Write));
            }
            return Wanted != Current;
        }

        /// <summary>
        /// Пакет клиента — заранее, сразу после установки или обновления клиента: при
        /// первом запуске клиент уже найдёт перевод в профиле и не будет ждать сеть.
        /// Клиент сам проверит, что пакет от его сборки; не подойдёт — скачает свой.
        /// </summary>
        public static void PrefetchClientPack(string clientVersion)
        {
            if (Wanted != AppLanguage.English) return;
            if (!Version.TryParse(clientVersion, out var parsed)) return;
            string version = parsed.ToString(3);
            if (_clientPackRequested == version) return;
            _clientPackRequested = version;

            // Отметка о том, для какой версии клиента пакет уже получен: без неё лаунчер
            // скачивал бы один и тот же файл при каждом своём запуске.
            string marker = Path.Combine(LanguagePackStore.CacheDirectory, "client-en.version");
            try
            {
                if (File.Exists(marker) && File.ReadAllText(marker).Trim() == version) return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            _ = Task.Run(async () =>
            {
                try
                {
                    bool fetched = await LanguagePackStore.PrefetchAsync(
                        "client", AppLanguage.English,
                        LanguagePackStore.DefaultUrls("client", AppLanguage.English, version),
                        _http, Save, LauncherLog.Write);
                    if (fetched) FileHelper.WriteAllTextAtomic(marker, version);
                }
                catch (Exception ex)
                {
                    LauncherLog.Write($"[Язык] Пакет клиента заранее не получен: {ex.Message}");
                }
            });
        }
    }
}
