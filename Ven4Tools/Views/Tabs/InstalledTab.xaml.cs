using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
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

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
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

    public partial class InstalledTab : UserControl
    {
        private List<InstalledApp> _allApps = new();

        // Фоновая предзагрузка — запускается из MainWindow.Loaded, до открытия вкладки.
        // Первое открытие вкладки просто awaits уже идущую задачу вместо нового winget list.
        private static Task? _preloadTask;
        private static volatile string? _cachedRawOutput;

        // Синхронизация доступа к _preloadTask и _cachedRawOutput: защита от гонки
        // при одновременных вызовах (предзагрузка из MainWindow vs открытие вкладки vs «Обновить»)
        private static readonly object _preloadLock = new object();

        public InstalledTab()
        {
            InitializeComponent();
            Loaded += (_, _) => _ = LoadAppsAsync();
        }

        public void ShowUpdatesFilter()
        {
            chkOnlyUpdates.IsChecked = true;
            ApplyFilter();
        }

        // ── Вспомогательные ───────────────────────────────────────────────────

        private void ShowState(string state)
        {
            Dispatcher.Invoke(() =>
            {
                pnlLoading.Visibility  = state == "loading" ? Visibility.Visible   : Visibility.Collapsed;
                pnlEmpty.Visibility    = state == "empty"   ? Visibility.Visible   : Visibility.Collapsed;
                listScroll.Visibility  = state == "list"    ? Visibility.Visible   : Visibility.Collapsed;
            });
        }

        // Расшифровка кода выхода winget/COM в единый результат: успех операции,
        // требуется ли перезагрузка и причина неуспеха. Централизует разбор hex-кодов,
        // ранее продублированный в BtnUpgradeAll_Click и UpdateAppAsync.
        // Примечание: деинсталляция (TryUninstallAsync) трактует 0x8A150014 как «пакет
        // не установлен» = успех — иная семантика, поэтому сюда намеренно не сведена.
        private static (bool Success, bool Reboot, string Reason) DescribeWingetExitCode(int code) => code switch
        {
            0                          => (true,  false, ""),
            3010                       => (true,  true,  ""),
            unchecked((int)0x8A15002C) => (true,  true,  ""),
            unchecked((int)0x8A15002B) => (false, false, "обновление недоступно — версия в источнике не подходит для данной системы"),
            unchecked((int)0x8A150014) => (false, false, "обновление недоступно — версия в источнике не подходит для данной системы"),
            unchecked((int)0x80072EE2) => (false, false, "ошибка сети — источник недоступен, попробуйте позже"),
            unchecked((int)0x80072EFE) => (false, false, "ошибка сети — источник недоступен, попробуйте позже"),
            _                          => (false, false, $"winget завершился с кодом {code}")
        };
    }
}
