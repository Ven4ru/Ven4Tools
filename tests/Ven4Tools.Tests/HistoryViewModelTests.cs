using System.Linq;
using Ven4Tools.Models;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Логика вкладки «История», перенесённая из code-behind в ViewModel
/// (2026-08-24). Реальная переустановка (InstallationService/CatalogLoaderService)
/// здесь не проверяется — только фильтр/поиск/счётчик, все чистые методы,
/// данные подставляются напрямую через RefreshAsync недоступен без сервиса,
/// поэтому фильтр проверяется через публичное состояние после конструктора
/// (пустой список) и через прямые сеттеры SearchText/SuccessOnly/FailOnly.
/// </summary>
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
    public void ReinstallCommand_НеПереустанавливает_БезЗапущеннойОперации()
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
