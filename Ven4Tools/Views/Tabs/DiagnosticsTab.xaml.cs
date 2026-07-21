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

            // Обработчики раздела «Информация о системе» и логов — реализованы в этой задаче.
            // Кнопки диагностики, Turbo Boost, кэша Windows Update и полного отчёта пока не
            // подключены — их обработчики добавляются в последующих задачах (Task 4-6).
            btnCopySystemInfo.Click += BtnCopySystemInfo_Click;
            btnOpenLogs.Click += BtnOpenLogs_Click;
            btnOpenLatestLog.Click += BtnOpenLatestLog_Click;
            btnClearLogs.Click += BtnClearLogs_Click;

            Loaded += DiagnosticsTab_Loaded;
        }

        private async void DiagnosticsTab_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;

            await LoadSystemInfoAsync();
        }
    }
}
