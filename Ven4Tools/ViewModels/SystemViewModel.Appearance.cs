using Ven4Tools.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.ViewModels
{
    public sealed partial class SystemViewModel
    {
        private string _themeTag = "web";
        public string ThemeTag
        {
            get => _themeTag;
            set
            {
                if (_loadingAppearance || _themeTag == value) return;
                SetField(ref _themeTag, value);
                ProfileService.Current.Theme = value;
                ProfileService.Save();
                ThemeService.Apply(value);
                ThemeApplied?.Invoke();
            }
        }

        private string _languageTag = "auto";
        public string LanguageTag
        {
            get => _languageTag;
            set
            {
                if (_loadingAppearance || _languageTag == value) return;
                SetField(ref _languageTag, value);
                ProfileService.Current.Language = value;
                ProfileService.Save();
                // Окна на лету не переводятся: новый язык включается при следующем запуске.
                // Сообщение сразу на двух языках и мимо перевода: его должен понять и тот,
                // кто уходит с языка, и тот, кто на него приходит.
                if (LocalizationService.Choose(value))
                    System.Windows.MessageBox.Show(
                        "Язык интерфейса сменится после перезапуска Ven4Tools.\n\nThe interface language will change after you restart Ven4Tools.",
                        "Ven4Tools", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
        }

        private bool _compactMode;
        public bool CompactMode
        {
            get => _compactMode;
            set
            {
                if (_loadingAppearance || _compactMode == value) return;
                SetField(ref _compactMode, value);
                ProfileService.Current.CompactMode = value;
                ProfileService.Save();
            }
        }

        private bool _reduceMotion;
        public bool ReduceMotion
        {
            get => _reduceMotion;
            set
            {
                if (_loadingAppearance || _reduceMotion == value) return;
                SetField(ref _reduceMotion, value);
                MotionService.Enabled = !value;
                ProfileService.Current.ReduceMotion = value;
                ProfileService.Save();
            }
        }

        // Без гейта _loadingAppearance — оригинальный ChkMinimizeToTray_Click тоже без него
        // (Click, в отличие от SelectionChanged, не срабатывает на программное присваивание).
        private bool _minimizeToTray;
        public bool MinimizeToTray
        {
            get => _minimizeToTray;
            set
            {
                if (_minimizeToTray == value) return;
                SetField(ref _minimizeToTray, value);
                ProfileService.Current.MinimizeToTray = value;
                ProfileService.Save();
            }
        }
    }
}
