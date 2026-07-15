using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace Ven4Tools.Services
{
    public static class LocalizationService
    {
        public static string Current { get; private set; } = "ru";

        // Единственные реально существующие словари (Resources/Lang/*.xaml).
        // ProfileService.Current.Language читается из profile.json — файла,
        // который целиком заменяется при импорте настроек (ProfileExportService.Import).
        // Без allowlist невалидное значение ("auto" не пройдёт нормально, либо
        // произвольная строка из повреждённого/специально подделанного архива
        // экспорта) уронило бы приложение уже на старте — pack-URI на
        // несуществующий ресурс кидает исключение при добавлении в MergedDictionaries.
        private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase) { "ru", "en" };

        public static void Init()
        {
            var lang = ProfileService.Current.Language;
            if (lang == "auto")
                lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
            Apply(lang);
        }

        public static void Apply(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang) || !SupportedLanguages.Contains(lang))
                lang = "ru";

            Current = lang;

            var toRemove = new List<ResourceDictionary>();
            foreach (ResourceDictionary d in Application.Current.Resources.MergedDictionaries)
            {
                if (d.Source?.OriginalString.Contains("/Resources/Lang/") == true)
                    toRemove.Add(d);
            }
            foreach (var d in toRemove)
                Application.Current.Resources.MergedDictionaries.Remove(d);

            try
            {
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/Resources/Lang/{lang}.xaml")
                });
            }
            catch (Exception ex)
            {
                // Не должно происходить при валидном lang из allowlist выше — но
                // не даём этому уронить старт приложения, если всё же произойдёт.
                AppLogger.Write($"[LocalizationService] Не удалось загрузить словарь '{lang}': {ex.Message}");
            }
        }

        public static string T(string key)
        {
            try { return Application.Current.FindResource(key)?.ToString() ?? key; }
            catch { return key; }
        }
    }
}
