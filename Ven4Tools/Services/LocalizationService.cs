using System;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Ven4Tools.Helpers;
using Ven4Tools.Localization;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Язык интерфейса клиента.
    ///
    /// Русский — в самой программе. Английский — отдельный языковой пакет: он лежит в
    /// профиле пользователя или рядом с программой, а если его нет — скачивается (CDN,
    /// зеркало сайта, GitHub). Пакет принимается только тот, с которым собрана эта сборка.
    /// Язык меняется при запуске: уже открытые окна на лету не переводятся.
    /// </summary>
    public static class LocalizationService
    {
        /// <summary>Язык, на котором интерфейс показан сейчас.</summary>
        public static string Current { get; private set; } = AppLanguage.Russian;

        /// <summary>Язык, который выбран (пользователем, установщиком или по языку Windows).</summary>
        public static string Wanted { get; private set; } = AppLanguage.Russian;

        private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static Task<LanguagePack?>? _download;

        private static string ClientVersion =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        private static LanguagePackSource EnglishPack => new(
            LanguagePackInfo.Component, AppLanguage.English, LanguagePackInfo.EnSha256,
            LanguagePackStore.DefaultUrls(LanguagePackInfo.Component, AppLanguage.English, ClientVersion));

        /// <summary>
        /// Первый шаг запуска, до любых окон и сообщений: определяет язык и включает
        /// перевод, если пакет уже есть на диске. В сеть не ходит.
        /// </summary>
        public static void Start()
        {
            UiTranslator.Log = AppLogger.Write;
            Wanted = AppLanguage.Resolve(ProfileService.Current.Language);
            bool collectOnly = Wanted != AppLanguage.English;
            if (collectOnly && !UiTranslator.CollectRequested) return;

            var pack = LanguagePackStore.TryLoadLocal(EnglishPack, AppContext.BaseDirectory, AppLogger.Write);
            if (pack == null) return;
            UiTranslator.Activate(pack, collectOnly);
            if (!collectOnly) AppLogger.Write($"[Язык] Включён перевод «{pack.Language}»: строк {pack.LiteralCount}, шаблонов {pack.PatternCount}");
            if (!collectOnly) Current = AppLanguage.English;
        }

        /// <summary>
        /// Докачивает пакет, если выбран английский, а пакета на диске нет. Ждёт не дольше
        /// <paramref name="wait"/>: не успел — запуск идёт дальше по-русски, а загрузка
        /// продолжается в фоне, и пакет применится при следующем запуске.
        /// Вызывать из потока интерфейса до создания окон.
        /// </summary>
        public static async Task EnsurePackAsync(TimeSpan wait)
        {
            if (Wanted != AppLanguage.English || UiTranslator.IsActive) return;
            // Офлайн-режим — обещание не ходить в сеть: пакет возьмётся, когда режим снимут.
            if (OfflineService.IsOffline) return;

            _download ??= LanguagePackStore.DownloadAsync(
                EnglishPack, _http, FileHelper.WriteAllBytesAtomicAsync, AppLogger.Write);
            if (await Task.WhenAny(_download, Task.Delay(wait)) != _download)
            {
                AppLogger.Write("[Язык] Пакет не успел загрузиться к запуску — интерфейс останется русским до следующего запуска");
                return;
            }

            var pack = await _download;
            if (pack == null || UiTranslator.IsActive) return;
            UiTranslator.Activate(pack);
            Current = AppLanguage.English;
        }

        /// <summary>
        /// Запоминает выбор из настроек («ru», «en», «auto») — общий для клиента и лаунчера.
        /// Возвращает true, если показанный сейчас язык отличается от выбранного и нужен перезапуск.
        /// </summary>
        public static bool Choose(string setting)
        {
            AppLanguage.SaveChoice(setting);
            Wanted = AppLanguage.Resolve(setting);
            if (Wanted == AppLanguage.English && !UiTranslator.IsActive && !OfflineService.IsOffline)
            {
                // Пакет подтягивается сразу, чтобы после перезапуска он уже был на диске.
                _download ??= LanguagePackStore.DownloadAsync(
                    EnglishPack, _http, FileHelper.WriteAllBytesAtomicAsync, AppLogger.Write);
            }
            return Wanted != Current;
        }

        /// <summary>
        /// Выбор языка общий с лаунчером: если его сменили там, список в настройках
        /// клиента должен показывать то же самое.
        /// </summary>
        public static void SyncSavedChoice()
        {
            string? saved = AppLanguage.SavedChoice();
            if (AppLanguage.IsSupported(saved)
                && !string.Equals(ProfileService.Current.Language, saved, StringComparison.OrdinalIgnoreCase))
            {
                ProfileService.Current.Language = saved!.ToLowerInvariant();
                ProfileService.Save();
            }
        }
    }
}
