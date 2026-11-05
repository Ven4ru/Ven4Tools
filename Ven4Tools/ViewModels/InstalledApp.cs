using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Ven4Tools.ViewModels
{
    /// <summary>
    /// Одна строка списка установленных приложений. Перенесено из
    /// Ven4Tools/Views/Tabs/InstalledTab.xaml.cs при MVVM-миграции (2026-08-26,
    /// седьмая вкладка после Debloater/History/About/Activation/Network/Office)
    /// без изменения тела.
    /// </summary>
    public class InstalledApp : INotifyPropertyChanged
    {
        public string Name      { get; set; } = "";
        public string WingetId  { get; set; } = "";
        public string Version   { get; set; } = "";

        private string _available = "";
        public string Available
        {
            get => _available;
            set { _available = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUpdate)); }
        }

        public string Source    { get; set; } = "";

        /// <summary>
        /// Плашка у названия — только у программ, которые стоят в списке дважды и больше
        /// (две «Microsoft Edge», по две «.NET Native»): архитектура «x64»/«x86»/«ARM64» или,
        /// если она одинакова или неизвестна, область установки. Пусто — плашки нет.
        /// Заполняет <see cref="InstalledViewModel.MarkVariants"/> после разбора списка.
        /// </summary>
        public string VariantTag { get; set; } = "";
        public bool HasVariantTag => !string.IsNullOrEmpty(VariantTag);

        /// <summary>
        /// Архитектура из локального ID winget: «ARP\Machine\X64\…», «ARP\User\X86\…»,
        /// «MSIX\Имя_версия_x64__издатель». У пакетов источников (Microsoft.Edge) её в
        /// выводе winget list нет — null. Обрезанный многоточием ID тоже даёт null.
        /// </summary>
        public string? Architecture => ParseArchitecture(WingetId);

        /// <summary>Область установки из «ARP\User\…» / «ARP\Machine\…»; у прочих ID — null.</summary>
        public bool? IsUserScope =>
            WingetId.StartsWith(@"ARP\User\", StringComparison.OrdinalIgnoreCase) ? true
            : WingetId.StartsWith(@"ARP\Machine\", StringComparison.OrdinalIgnoreCase) ? false
            : null;

        internal static string? ParseArchitecture(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Contains('…')) return null;
            if (id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase))
            {
                var parts = id.Split('\\');
                return parts.Length > 3 ? Normalize(parts[2]) : null;
            }
            if (id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase))
            {
                // Полное имя пакета MSIX: Имя_Версия_Архитектура_Ресурс_Издатель.
                var parts = id.Substring(5).Split('_');
                return parts.Length > 2 ? Normalize(parts[2]) : null;
            }
            return null;

            static string? Normalize(string a) => a.ToLowerInvariant() switch
            {
                "x64" => "x64",
                "x86" => "x86",
                "arm64" => "ARM64",
                "arm" => "ARM",
                _ => null // neutral и прочее — различия по архитектуре нет
            };
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
        }

        private bool _isProcessing;
        public bool IsProcessing
        {
            get => _isProcessing;
            set { _isProcessing = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanAct)); }
        }

        public bool HasUpdate        => !string.IsNullOrWhiteSpace(Available) && Available != "Unknown";
        public bool CanAct           => !IsProcessing;
        public bool IsVerified       => Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                                     || Source.Equals("msstore", StringComparison.OrdinalIgnoreCase);
        public bool IsUnknownSource  => string.IsNullOrWhiteSpace(Source) || Source.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

        public string SourceDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Source) || Source.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                    return "❓ Неизвестный";
                if (Source.Equals("winget", StringComparison.OrdinalIgnoreCase))
                    return "✔ winget";
                if (Source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                    return "✔ Store";
                return Source;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
