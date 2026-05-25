using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace Ven4Tools.Services
{
    public static class LocalizationService
    {
        public static string Current { get; private set; } = "ru";

        public static void Init()
        {
            var lang = ProfileService.Current.Language;
            if (lang == "auto")
                lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "ru" : "en";
            Apply(lang);
        }

        public static void Apply(string lang)
        {
            Current = lang;

            var toRemove = new List<ResourceDictionary>();
            foreach (ResourceDictionary d in Application.Current.Resources.MergedDictionaries)
            {
                if (d.Source?.OriginalString.Contains("/Resources/Lang/") == true)
                    toRemove.Add(d);
            }
            foreach (var d in toRemove)
                Application.Current.Resources.MergedDictionaries.Remove(d);

            Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Resources/Lang/{lang}.xaml")
            });
        }

        public static string T(string key)
        {
            try { return Application.Current.FindResource(key)?.ToString() ?? key; }
            catch { return key; }
        }
    }
}
