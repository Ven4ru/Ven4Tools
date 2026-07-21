using System;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class DiagnosticsTab : UserControl
    {
        private bool _initialized = false;

        public DiagnosticsTab()
        {
            InitializeComponent();

            // Обработчики разделов «Информация о системе», логов, Turbo Boost и очистки кэша
            // Windows Update — подключены. Кнопки запуска диагностики и полного отчёта
            // (btnRunDiagnostics, btnCopyFullReport) пока не подключены — их обработчики
            // добавляются в оркестраторе (Task 7).
            btnCopySystemInfo.Click += BtnCopySystemInfo_Click;
            btnOpenLogs.Click += BtnOpenLogs_Click;
            btnOpenLatestLog.Click += BtnOpenLatestLog_Click;
            btnClearLogs.Click += BtnClearLogs_Click;
            btnDisableTurboBoost.Click += BtnDisableTurboBoost_Click;
            btnEnableTurboBoost.Click += BtnEnableTurboBoost_Click;
            btnClearWuCache.Click += BtnClearWuCache_Click;

            Loaded += DiagnosticsTab_Loaded;
        }

        private async void DiagnosticsTab_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;

            await LoadSystemInfoAsync();
            await RefreshTurboBoostStatusAsync();
        }
    }
}
