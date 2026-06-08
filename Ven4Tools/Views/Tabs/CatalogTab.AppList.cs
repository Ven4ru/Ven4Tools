using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ven4Tools.Models;
using Ven4Tools.Services;

namespace Ven4Tools.Views.Tabs
{
    public partial class CatalogTab
    {
        private void OnUserSessionChanged()
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                var userApps = appManager.GetAllApps().Where(a => a.IsUserAdded).ToList();
                foreach (var app in userApps)
                {
                    appCheckBoxes.Remove(app.Id);
                    availabilityStatus.Remove(app.Id);
                }
                PanelПользовательские.Children.Clear();
                appManager.ClearUserApps();

                if (UserSession.IsLoggedIn)
                    await SyncUserAppsFromServerAsync();
            });
        }

        private async Task SyncUserAppsFromServerAsync()
        {
            if (!UserSession.IsLoggedIn) return;
            var serverApps = await _userAppsService.FetchAsync(UserSession.UserId);
            int added = 0;
            foreach (var app in serverApps)
            {
                if (appManager.GetAppById(app.Id) != null) continue;
                appManager.AddUserApp(app);
                await Dispatcher.InvokeAsync(() => AddUserAppToUI(app));
                added++;
            }
            if (added > 0)
                AddLog($"☁️ Синхронизировано {added} приложений с аккаунта");
        }

        private void LoadApps()
        {
            try
            {
                foreach (var panel in categoryPanels.Values)
                    panel.Children.Clear();

                appCheckBoxes.Clear();
                availabilityStatus.Clear();
                _versionCombos.Clear();
                _appVersions.Clear();

                if (_catalog == null)
                {
                    AddLog("⚠️ Каталог не загружен");
                    return;
                }

                AddLog($"Каталог содержит {_catalog.Apps.Count} приложений");

                var availabilityTasks = new List<Task>();
                _ruBlockedIds.Clear();

                var appsToShow = ProfileService.Current.DefaultSort switch
                {
                    "alpha"      => _catalog.Apps.OrderBy(a => a.Name).ToList(),
                    "category"   => _catalog.Apps.OrderBy(a => a.Category).ThenBy(a => a.Name).ToList(),
                    "popularity" => _catalog.Apps.OrderByDescending(a => a.Popularity).ThenBy(a => a.Name).ToList(),
                    _            => _catalog.Apps
                };

                foreach (var app in appsToShow)
                {
                    var category = GetCategoryFromString(app.Category);

                    if (categoryPanels.TryGetValue(category, out var panel) && panel != null)
                    {
                        var textBlock = new TextBlock
                        {
                            Text = app.Name,
                            Foreground = Brushes.Gray,
                            Margin = new Thickness(0, 0, 5, 0)
                        };

                        var checkBox = new CheckBox
                        {
                            Content = textBlock,
                            Tag = app.Id,
                            ToolTip = "⏳ Проверка доступности..."
                        };
                        checkBox.Checked   += AppCheckBox_Changed;
                        checkBox.Unchecked += AppCheckBox_Changed;

                        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                        row.Children.Add(checkBox);
                        row.Children.Add(MakeStarButton(app.Id));
                        if (app.RuBlocked)
                        {
                            _ruBlockedIds.Add(app.Id);
                            row.Children.Add(new TextBlock
                            {
                                Text = "⚠ РФ",
                                Foreground = new SolidColorBrush(Color.FromRgb(255, 165, 0)),
                                FontSize = 10,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(4, 0, 0, 0),
                                ToolTip = "Загрузка может быть заблокирована в России"
                            });
                        }
                        if (!string.IsNullOrEmpty(app.WingetId))
                        {
                            var versionCombo = MakeVersionCombo(app.Id);
                            row.Children.Add(versionCombo);
                            _versionCombos[app.Id] = versionCombo;
                        }

                        panel.Children.Add(row);
                        appCheckBoxes[app.Id] = checkBox;
                        availabilityTasks.Add(CheckAppAvailabilityFromCatalog(app));
                    }
                }

                var userApps = appManager.GetAllApps().Where(a => a.IsUserAdded).ToList();
                foreach (var app in userApps)
                    AddUserAppToUI(app);

                AddLog($"Загружено {_catalog.Apps.Count} приложений из каталога, {userApps.Count} пользовательских");
                AddLog("⏳ Проверка доступности запущена...");

                ApplyProfileFilters();
                UpdateInstallButton();

                _ = Task.WhenAll(availabilityTasks).ContinueWith(async _ =>
                {
                    // availabilityStatus заполняется/читается на UI-потоке — агрегируем там,
                    // иначе чтение словаря с пулового потока гонится с записью из Dispatcher.
                    await Dispatcher.InvokeAsync(() =>
                    {
                        int available   = availabilityStatus.Values.Count(s => s == AvailabilityChecker.AvailabilityStatus.Available);
                        int unavailable = availabilityStatus.Values.Count(s => s == AvailabilityChecker.AvailabilityStatus.Unavailable);
                        AddLog($"✅ Проверка завершена: {available} доступно, {unavailable} недоступно");
                    });
                    await FetchVersionsPhase2Async();
                    await UpdateInstalledStatusAsync();
                }).Unwrap().ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        AddLog($"⚠️ Ошибка фоновой задачи: {t.Exception?.InnerException?.Message}");
                });
            }
            catch (Exception ex)
            {
                AddLog($"Ошибка при загрузке приложений: {ex.Message}");
            }
        }

        private void AddUserAppToUI(AppInfo app)
        {
            var textBlock = new TextBlock
            {
                Text = app.DisplayName,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 140, 0)),
                Margin = new Thickness(0, 0, 5, 0)
            };

            var checkBox = new CheckBox
            {
                Content = textBlock,
                Tag = app.Id,
                Margin = new Thickness(0, 2, 0, 2),
                ToolTip = "👤 Пользовательское приложение"
            };
            checkBox.Checked   += AppCheckBox_Changed;
            checkBox.Unchecked += AppCheckBox_Changed;

            var removeButton = new Button
            {
                Content = "❌",
                Width = 20,
                Height = 20,
                Margin = new Thickness(5, 0, 0, 0),
                Tag = app.Id,
                Background = new SolidColorBrush(Color.FromRgb(100, 0, 0)),
                Foreground = Brushes.White,
                FontSize = 10,
                Padding = new Thickness(0),
                ToolTip = "Удалить из списка"
            };
            removeButton.Click += (s, args) => RemoveUserApp(app.Id);

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            panel.Children.Add(checkBox);
            panel.Children.Add(MakeStarButton(app.Id));
            if (!string.IsNullOrEmpty(app.AlternativeId))
            {
                var versionCombo = MakeVersionCombo(app.Id);
                panel.Children.Add(versionCombo);
                _versionCombos[app.Id] = versionCombo;
                _ = Task.Run(() => FetchVersionsForAppAsync(app.Id, app.AlternativeId));
            }
            panel.Children.Add(removeButton);

            PanelПользовательские.Children.Add(panel);
            appCheckBoxes[app.Id] = checkBox;
            availabilityStatus[app.Id] = AvailabilityChecker.AvailabilityStatus.Available;
        }

        private void RemoveUserApp(string appId)
        {
            var result = MessageBox.Show("Удалить приложение из списка?", "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    appManager.RemoveUserApp(appId);
                    if (UserSession.IsLoggedIn)
                        _ = _userAppsService.DeleteAsync(UserSession.UserId, appId);

                    var toRemove = PanelПользовательские.Children
                        .OfType<StackPanel>()
                        .FirstOrDefault(sp => sp.Children
                            .OfType<CheckBox>()
                            .Any(c => c.Tag?.ToString() == appId));

                    if (toRemove != null)
                        PanelПользовательские.Children.Remove(toRemove);

                    appCheckBoxes.Remove(appId);
                    availabilityStatus.Remove(appId);
                    AddLog($"🗑️ Удалено пользовательское приложение");
                }
                catch (Exception ex)
                {
                    AddLog($"❌ Ошибка при удалении: {ex.Message}");
                }
            }
        }

        private void BtnClearAllUserApps_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Вы действительно хотите удалить ВСЕ пользовательские приложения?", "Полная очистка",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    appManager.ClearUserApps();
                    AddLog("✅ Пользовательские приложения очищены");
                    ReloadAllApps();
                }
                catch (Exception ex) { AddLog($"❌ Ошибка при удалении: {ex.Message}"); }
            }
        }

        private void ReloadAllApps()
        {
            try
            {
                PanelБраузеры.Children.Clear(); PanelОфис.Children.Clear(); PanelГрафика.Children.Clear();
                PanelРазработка.Children.Clear(); PanelМессенджеры.Children.Clear(); PanelМультимедиа.Children.Clear();
                PanelСистемные.Children.Clear(); PanelИгровыеСервисы.Children.Clear(); PanelДрайверпаки.Children.Clear();
                PanelДругое.Children.Clear(); PanelПользовательские.Children.Clear();
                appCheckBoxes.Clear(); availabilityStatus.Clear();
                LoadApps();
                AddLog("🔄 Список приложений перезагружен");
            }
            catch (Exception ex) { AddLog($"❌ Ошибка при перезагрузке: {ex.Message}"); }
        }

        public void AddLocalInstallerApp(AppInfo app)
        {
            appManager.AddUserApp(app);
            AddUserAppToUI(app);
            AddLog($"📦 Локальный установщик добавлен: {app.DisplayName}");
        }
    }
}
