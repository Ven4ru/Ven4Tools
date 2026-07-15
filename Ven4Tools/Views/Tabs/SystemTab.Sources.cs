using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Shared;

namespace Ven4Tools.Views.Tabs
{
    public partial class SystemTab : UserControl
    {

        // ── Source order ──────────────────────────────────────────────────────────

        private sealed class SourceItem
        {
            public string Id    { get; set; } = "";
            public string Label { get; set; } = "";
        }

        private System.Collections.ObjectModel.ObservableCollection<SourceItem> _sourceItems = new();

        private void LoadSourceOrderUI()
        {
            var settings = SourceOrderService.Current;
            rbSourceGlobal.IsChecked      = settings.Mode == "global";
            rbSourcePerCategory.IsChecked = settings.Mode == "per_category";

            _sourceItems.Clear();
            foreach (var id in settings.GlobalOrder)
                _sourceItems.Add(new SourceItem { Id = id, Label = SourceOrderSettings.Labels.GetValueOrDefault(id, id) });

            lstSourceOrder.ItemsSource = _sourceItems;
            UpdateSourcePanels();
        }

        private void UpdateSourcePanels()
        {
            bool isGlobal = rbSourceGlobal.IsChecked == true;
            pnlGlobalOrder.Visibility      = isGlobal ? Visibility.Visible : Visibility.Collapsed;
            pnlPerCategoryHint.Visibility  = isGlobal ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RbSourceMode_Click(object sender, RoutedEventArgs e) => UpdateSourcePanels();

        private void BtnSrcUp_Click(object sender, RoutedEventArgs e)
        {
            int idx = lstSourceOrder.SelectedIndex;
            if (idx <= 0) return;
            _sourceItems.Move(idx, idx - 1);
            lstSourceOrder.SelectedIndex = idx - 1;
        }

        private void BtnSrcDown_Click(object sender, RoutedEventArgs e)
        {
            int idx = lstSourceOrder.SelectedIndex;
            if (idx < 0 || idx >= _sourceItems.Count - 1) return;
            _sourceItems.Move(idx, idx + 1);
            lstSourceOrder.SelectedIndex = idx + 1;
        }

        private void BtnSaveSourceOrder_Click(object sender, RoutedEventArgs e)
        {
            SourceOrderService.Current.Mode        = rbSourceGlobal.IsChecked == true ? "global" : "per_category";
            SourceOrderService.Current.GlobalOrder = _sourceItems.Select(i => i.Id).ToList();
            SourceOrderService.Save();

            txtSourceOrderStatus.Text = $"✅ Сохранено {DateTime.Now:HH:mm:ss} · Запуск проверки доступности...";
            AppLogger.Write("🔀 Порядок источников сохранён");
        }
    }
}
