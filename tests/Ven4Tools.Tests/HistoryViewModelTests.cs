using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

// Общая коллекция для тестов, которые трогают статический ProfileService.Current.
// Без неё xUnit может выполнять такие классы параллельно, и сохранение профиля
// из одного теста запишет на диск чужие промежуточные правки.
[CollectionDefinition("ProfileService")]
public class ProfileServiceCollection { }

/// <summary>
/// Логика вкладки «История», перенесённая из code-behind в ViewModel
/// (2026-08-24). Реальная переустановка (InstallationService/CatalogLoaderService)
/// здесь не проверяется. Настоящее покрытие предиката фильтрации (совпадение
/// подстроки при поиске, комбинация SuccessOnly/FailOnly на реальных записях)
/// этими тестами тоже не достигается: набор _allEntries наполняется только
/// вызовом InstallHistoryService.GetHistoryAsync() внутри RefreshAsync(), а шва
/// для подстановки тестовых данных без обращения к реальному файлу истории нет.
/// Поэтому здесь проверяются конструирование ViewModel, доступность команд и
/// проброс SaveHistory в ProfileService, а сеттеры SearchText/SuccessOnly/FailOnly
/// — лишь на отсутствие исключений на пустом списке.
/// </summary>
[Collection("ProfileService")]
public class HistoryViewModelTests
{
    [Fact]
    public void FilteredEntries_ПоУмолчанию_Пуст()
    {
        var vm = new HistoryViewModel();

        Assert.Empty(vm.FilteredEntries);
        Assert.Equal("0", vm.HistoryCount);
    }

    [Fact]
    public void SearchText_УстанавливаетсяИДоступноЧерезСвойство()
    {
        var vm = new HistoryViewModel();

        vm.SearchText = "firefox";

        Assert.Equal("firefox", vm.SearchText);
        // Список пуст (нет загруженной истории) — фильтр не должен падать на пустом наборе.
        Assert.Empty(vm.FilteredEntries);
    }

    [Fact]
    public void SuccessOnlyИFailOnly_ОбаВключеныОдновременно_НеПадают()
    {
        var vm = new HistoryViewModel();

        vm.SuccessOnly = true;
        vm.FailOnly = true;

        // Комбинация "оба включены" в исходной логике не фильтрует ни по одному
        // условию (эквивалент "показать всё") — регресс на пустом списке: просто
        // не должно быть исключения, список остаётся пустым.
        Assert.Empty(vm.FilteredEntries);
    }

    [Fact]
    public void SaveHistory_ЧтениеОтражаетProfileService()
    {
        var vm = new HistoryViewModel();
        bool original = Ven4Tools.Services.ProfileService.Current.SaveInstallHistory;

        try
        {
            vm.SaveHistory = !original;
            Assert.Equal(!original, Ven4Tools.Services.ProfileService.Current.SaveInstallHistory);
            Assert.Equal(!original, vm.SaveHistory);
        }
        finally
        {
            // Восстановить исходное значение — тест не должен менять состояние
            // profile.json на диске для остальных тестов сборки.
            vm.SaveHistory = original;
        }
    }

    [Fact]
    public void ReinstallCommand_ДоступнаКогдаПереустановкаНеИдёт()
    {
        var vm = new HistoryViewModel();

        Assert.False(vm.IsReinstalling);
        Assert.True(vm.ReinstallCommand.CanExecute(null),
            "Вне активной переустановки команда должна быть доступна.");
    }

    [Fact]
    public void ClearHistoryCommand_Существует_ИДоступнаПоУмолчанию()
    {
        var vm = new HistoryViewModel();

        Assert.True(vm.ClearHistoryCommand.CanExecute(null));
    }
}
