using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    // Конструктор SystemViewModel вызывает LoadSettings(), а тот при режиме обновлений
    // "NotSet" дописывает в профиль "NotifyOnly" и сохраняет его. Значит класс трогает
    // статический ProfileService и обязан идти в общей коллекции: иначе он выполнялся бы
    // параллельно с WindowsUpdateBackgroundServiceTests, который выставляет "NotSet" и
    // проверяет, что фоновый поиск не пошёл — чужая нормализация ломала бы этот тест.
    [Collection("ProfileService")]
    public class SystemViewModelTests
    {
        [Fact]
        public void Конструктор_УстанавливаетДефолтыБизиФлагов()
        {
            var vm = new SystemViewModel();

            Assert.False(vm.IsCheckingUpdates);
            Assert.False(vm.IsDownloadingToCache);
            Assert.False(vm.IsSavingSnapshot);
        }

        [Fact]
        public void Конструктор_УстанавливаетДефолтыНастроек()
        {
            var vm = new SystemViewModel();

            Assert.Equal("10 сек", vm.CatalogTimeoutText);
            Assert.Equal("15 сек", vm.CheckTimeoutText);
            Assert.Equal("", vm.DefaultInstallFolderStatusText);
            Assert.Equal("", vm.HiddenAppsStatusText);
            Assert.Equal("", vm.TransferStatusText);
        }

        [Fact]
        public void WindowsUpdateModeTag_ВсегдаОдинИзДвухПунктовСписка()
        {
            // В профиле режим может лежать как "NotSet" (вкладку обновлений ещё не
            // открывали) — такого пункта в ComboBox нет, и SelectedValue тогда не нашёл
            // бы совпадения, оставив список пустым. LoadSettings обязан свести значение
            // к одному из реальных тегов.
            var vm = new SystemViewModel();

            Assert.Contains(vm.WindowsUpdateModeTag, new[] { "NotifyOnly", "NotifyAndDownload" });
        }

        [Fact]
        public void Конструктор_РежимОбновленийNotSet_ЗакрепляетNotifyOnlyВПрофиле()
        {
            // Мало показать «Только уведомлять» в списке: пока в профиле лежит "NotSet",
            // фоновая проверка не идёт вообще, и пользователь, открывший «Настройки» и
            // ничего не тронувший, остался бы без уведомлений об обновлениях Windows,
            // видя выбранным работающий на вид режим.
            string original = ProfileService.Current.WindowsUpdateMode;
            try
            {
                ProfileService.Current.WindowsUpdateMode = "NotSet";

                var vm = new SystemViewModel();

                Assert.Equal("NotifyOnly", ProfileService.Current.WindowsUpdateMode);
                Assert.Equal("NotifyOnly", vm.WindowsUpdateModeTag);
            }
            finally
            {
                ProfileService.Current.WindowsUpdateMode = original;
            }
        }

        [Fact]
        public void Конструктор_ВыбранныйРежимОбновлений_НеПерезаписывается()
        {
            string original = ProfileService.Current.WindowsUpdateMode;
            try
            {
                ProfileService.Current.WindowsUpdateMode = "NotifyAndDownload";

                var vm = new SystemViewModel();

                Assert.Equal("NotifyAndDownload", ProfileService.Current.WindowsUpdateMode);
                Assert.Equal("NotifyAndDownload", vm.WindowsUpdateModeTag);
            }
            finally
            {
                ProfileService.Current.WindowsUpdateMode = original;
            }
        }

        [Fact]
        public void DefaultInstallFolderText_ОтклонённыйПуть_ОткатываетЗначениеСУведомлением()
        {
            var vm = new SystemViewModel();
            string before = vm.DefaultInstallFolderText;
            bool raised = false;
            vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(SystemViewModel.DefaultInstallFolderText);

            vm.DefaultInstallFolderText = @"\\server\share";

            Assert.Equal(before, vm.DefaultInstallFolderText);
            Assert.True(raised);
        }

        [Fact]
        public void Конструктор_УстанавливаетДефолтОбновленийПриложений()
        {
            var vm = new SystemViewModel();

            Assert.Equal("Нажмите «Проверить обновления» для проверки...", vm.UpdatesLogText);
        }

        [Fact]
        public void Конструктор_УстанавливаетДефолтыКэша()
        {
            var vm = new SystemViewModel();

            Assert.Equal("Кэш пуст", vm.CacheStatsText);
            Assert.Empty(vm.FilteredCacheApps);
            Assert.False(vm.ShowCacheProgress);
            Assert.False(vm.ShowCacheLog);
            Assert.False(vm.ShowCancelCacheDownload);
            Assert.True(vm.CanCancelCacheDownload);
        }

        [Fact]
        public void Конструктор_УстанавливаетДефолтыИсточников()
        {
            var vm = new SystemViewModel();

            Assert.True(vm.IsGlobalSourceMode);
            Assert.False(vm.IsPerCategorySourceMode);
            Assert.True(vm.ShowGlobalOrderPanel);
            Assert.False(vm.ShowPerCategoryHint);
            Assert.Empty(vm.SourceItems);
        }

        [Fact]
        public void Конструктор_УстанавливаетДефолтыСнапшотов()
        {
            var vm = new SystemViewModel();

            Assert.Empty(vm.Snapshots);
            Assert.True(vm.ShowSnapshotsEmpty);
            Assert.Equal("", vm.SnapshotStatusText);
        }

        [Fact]
        public void КомандыСCanExecute_ИзначальноTrue()
        {
            var vm = new SystemViewModel();

            Assert.True(vm.CheckUpdatesCommand.CanExecute(null));
            Assert.True(vm.DownloadToCacheCommand.CanExecute(null));
            Assert.True(vm.SaveSnapshotCommand.CanExecute(null));
        }

        [Fact]
        public void КомандыБезCanExecute_ИзначальноTrue()
        {
            var vm = new SystemViewModel();

            Assert.True(vm.BrowseDefaultInstallFolderCommand.CanExecute(null));
            Assert.True(vm.UnhideAllAppsCommand.CanExecute(null));
            Assert.True(vm.ExportSettingsCommand.CanExecute(null));
            Assert.True(vm.ImportSettingsCommand.CanExecute(null));
            Assert.True(vm.SelectAllCacheCommand.CanExecute(null));
            Assert.True(vm.SelectNoneCacheCommand.CanExecute(null));
            Assert.True(vm.BrowseCachePathCommand.CanExecute(null));
            Assert.True(vm.OpenCacheFolderCommand.CanExecute(null));
            Assert.True(vm.ClearCacheCommand.CanExecute(null));
            Assert.True(vm.CancelCacheDownloadCommand.CanExecute(null));
            Assert.True(vm.MoveSourceUpCommand.CanExecute(null));
            Assert.True(vm.MoveSourceDownCommand.CanExecute(null));
            Assert.True(vm.SaveSourceOrderCommand.CanExecute(null));
        }

        [Fact]
        public void RestoreSnapshotCommand_CanExecute_ТребуетSnapshotRowИНеЗанятость()
        {
            var vm = new SystemViewModel();
            var row = new SnapshotRow(new ConfigSnapshotInfo { FilePath = "x", Name = "n" });

            Assert.False(vm.RestoreSnapshotCommand.CanExecute(null));
            Assert.True(vm.RestoreSnapshotCommand.CanExecute(row));

            row.IsRestoring = true;
            Assert.False(vm.RestoreSnapshotCommand.CanExecute(row));
        }

        [Fact]
        public void IsGlobalSourceMode_ИзменениеВFalse_БезусловноПересчитываетПанели()
        {
            var vm = new SystemViewModel();

            vm.IsGlobalSourceMode = false;

            Assert.False(vm.ShowGlobalOrderPanel);
            Assert.True(vm.ShowPerCategoryHint);
        }

        [Fact]
        public void IsPerCategorySourceMode_ИзменениеВTrue_БезусловноПересчитываетПанели()
        {
            var vm = new SystemViewModel();

            vm.IsPerCategorySourceMode = true;

            Assert.False(vm.ShowGlobalOrderPanel);
            Assert.True(vm.ShowPerCategoryHint);
        }

        [Fact]
        public void IsPerCategorySourceMode_УстановкаВTrue_СбрасываетIsGlobalSourceMode()
        {
            var vm = new SystemViewModel();

            vm.IsPerCategorySourceMode = true;

            Assert.False(vm.IsGlobalSourceMode);
        }

        [Fact]
        public void IsGlobalSourceMode_УстановкаВTrue_СбрасываетIsPerCategorySourceMode()
        {
            var vm = new SystemViewModel();
            vm.IsPerCategorySourceMode = true;

            vm.IsGlobalSourceMode = true;

            Assert.False(vm.IsPerCategorySourceMode);
        }

        [Fact]
        public void CacheAppItem_IsSelected_ПоднимаетPropertyChanged()
        {
            var item = new CacheAppItem { Id = "1", DisplayName = "App", DownloadUrl = "http://x", Sha256 = "" };
            bool raised = false;
            item.PropertyChanged += (_, e) => raised = e.PropertyName == nameof(CacheAppItem.IsSelected);

            item.IsSelected = true;

            Assert.True(raised);
        }

        [Fact]
        public void SnapshotRow_DisplayLabel_ПробрасываетИзInfo()
        {
            var info = new ConfigSnapshotInfo { Name = "Тест", TweakCount = 3, PresetCount = 2 };
            var row = new SnapshotRow(info);

            Assert.Equal(info.DisplayLabel, row.DisplayLabel);
        }

        [Fact]
        public void ParseUpgradableRows_РазбираетТаблицуИОтсекаетФутер()
        {
            string raw =
                "Имя              ИД             Версия   Доступно  Источник\n" +
                "----------------- --------------- -------- --------- --------\n" +
                "7-Zip              7zip.7zip       21.07    23.01     winget\n" +
                "Google Chrome      Google.Chrome   119.0    120.0     winget\n" +
                "\n" +
                "Доступны обновления: 2.\n";

            var rows = SystemViewModel.ParseUpgradableRows(raw);

            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, r => r.Contains("7-Zip"));
            Assert.Contains(rows, r => r.Contains("Google Chrome"));
            Assert.DoesNotContain(rows, r => r.Contains("Доступны обновления"));
        }

        [Fact]
        public void ParseUpgradableRows_БезРазделителя_ВозвращаетПусто()
        {
            var rows = SystemViewModel.ParseUpgradableRows("Просто текст без таблицы winget.");
            Assert.Empty(rows);
        }

        [Fact]
        public void ParseUpgradableRows_ПустаяИНеопределённаяСтрока_ВозвращаетПусто()
        {
            Assert.Empty(SystemViewModel.ParseUpgradableRows(""));
            Assert.Empty(SystemViewModel.ParseUpgradableRows(null!));
        }
    }
}
