using Ven4Tools.Models;
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
/// здесь не проверяется. Этот класс покрывает конструирование ViewModel,
/// доступность команд и проброс SaveHistory в ProfileService; сеттеры
/// SearchText/SuccessOnly/FailOnly проверяются здесь лишь на отсутствие
/// исключений на пустом списке — сам предикат фильтрации на реальных записях
/// покрыт в <see cref="HistoryViewModelFilterTests"/>.
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

/// <summary>
/// Настоящее покрытие предиката фильтрации истории: HistoryViewModel.Filter
/// вызывается напрямую на небольшом наборе записей в памяти, поэтому проверяется
/// именно результат отбора, а не «не упало на пустом списке». ProfileService и
/// файл истории здесь не задействованы — коллекция "ProfileService" не нужна.
/// </summary>
public class HistoryViewModelFilterTests
{
    private static List<HistoryEntry> Записи() => new()
    {
        new HistoryEntry { AppId = "Mozilla.Firefox", AppName = "Firefox",    Category = "Браузеры",    Success = true  },
        new HistoryEntry { AppId = "7zip.7zip",      AppName = "7-Zip",       Category = "Архиваторы",  Success = false },
        new HistoryEntry { AppId = "Notepad.Plus",   AppName = "Notepad++",   Category = "Редакторы",   Success = true  }
    };

    [Fact]
    public void Filter_ПоискПоAppName_ВозвращаетТолькоСовпадающие()
    {
        var result = HistoryViewModel.Filter(Записи(), "firefox", false, false);

        Assert.Single(result);
        Assert.Equal("Firefox", result[0].AppName);
    }

    [Fact]
    public void Filter_ПоискПоCategory_ТожеНаходитЗапись()
    {
        // «Архиваторы» встречается только в категории, в названии 7-Zip такой
        // подстроки нет — значит совпадение пришло именно из второго поля.
        var result = HistoryViewModel.Filter(Записи(), "Архиваторы", false, false);

        Assert.Single(result);
        Assert.Equal("7-Zip", result[0].AppName);
    }

    [Fact]
    public void Filter_ПоискБезУчётаРегистра_НаходитЗапись()
    {
        var result = HistoryViewModel.Filter(Записи(), "FiReFoX", false, false);

        Assert.Single(result);
        Assert.Equal("Firefox", result[0].AppName);
    }

    [Fact]
    public void Filter_ПоискСПробелами_ОбрезаетсяПередСравнением()
    {
        var result = HistoryViewModel.Filter(Записи(), "   Notepad   ", false, false);

        Assert.Single(result);
        Assert.Equal("Notepad++", result[0].AppName);
    }

    [Fact]
    public void Filter_ПоискНеСовпадает_ВозвращаетПустойСписок()
    {
        var result = HistoryViewModel.Filter(Записи(), "несуществующее-приложение-zzz", false, false);

        Assert.Empty(result);
    }

    [Fact]
    public void Filter_ТолькоУспешные_ВозвращаетЗаписиСSuccessTrue()
    {
        var result = HistoryViewModel.Filter(Записи(), "", true, false);

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.True(e.Success));
        Assert.DoesNotContain(result, e => e.AppName == "7-Zip");
    }

    [Fact]
    public void Filter_ТолькоНеудачные_ВозвращаетЗаписиСSuccessFalse()
    {
        var result = HistoryViewModel.Filter(Записи(), "", false, true);

        Assert.Single(result);
        Assert.False(result[0].Success);
        Assert.Equal("7-Zip", result[0].AppName);
    }

    [Fact]
    public void Filter_ОбаФильтраВключены_ВозвращаетПолныйСписок()
    {
        // Намеренное поведение вкладки: обе отметки одновременно = «показать всё».
        var result = HistoryViewModel.Filter(Записи(), "", true, true);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Filter_ПоискИФильтрКомбинируются()
    {
        // Латинская «i» есть в Firefox и 7-Zip, но не в Notepad++: поиск отсекает
        // Notepad++, отметка «успешные» — 7-Zip. Остаётся ровно одна запись,
        // то есть условия складываются по «И», а не по «ИЛИ».
        Assert.Equal(2, HistoryViewModel.Filter(Записи(), "i", false, false).Count);

        var result = HistoryViewModel.Filter(Записи(), "i", true, false);

        Assert.Single(result);
        Assert.Equal("Firefox", result[0].AppName);
    }

    [Fact]
    public void Filter_ПустойЗапросБезФильтров_ВозвращаетВсё()
    {
        var result = HistoryViewModel.Filter(Записи(), "   ", false, false);

        Assert.Equal(3, result.Count);
    }
}
