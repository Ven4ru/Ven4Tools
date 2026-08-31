using System;
using System.Linq;
using System.Threading.Tasks;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Логика вкладки «Очистка», перенесённая из code-behind в ViewModel
/// (2026-08-21). Реальные системные операции (DebloatTweakExecutor) здесь не
/// проверяются — только фильтр/выбор/снапшот-контракт, всё чистые методы.
/// </summary>
public class DebloaterViewModelTests
{
    [Fact]
    public void CategoryFilter_ПоУмолчанию_ПоказываетВсеКатегории()
    {
        var vm = new DebloaterViewModel();

        var distinctCategories = vm.FilteredItems.Select(i => i.Category).Distinct().ToList();

        Assert.True(distinctCategories.Count >= 2,
            "По умолчанию (фильтр 'all') должны быть видны твики нескольких категорий, не одной.");
    }

    [Fact]
    public void CategoryFilter_ApplyFiltruet_ТолькоСвоюКатегорию()
    {
        var vm = new DebloaterViewModel();

        vm.CategoryFilter = "app";

        Assert.All(vm.FilteredItems, item => Assert.Equal("app", item.Category));
        Assert.True(vm.FilteredItems.Count > 0, "В каталоге должен быть хотя бы один твик категории app.");
    }

    [Fact]
    public void SelectAllCommand_ОтмечаетТолькоВидимыеФильтром()
    {
        var vm = new DebloaterViewModel();
        vm.CategoryFilter = "app";

        vm.SelectAllCommand.Execute(null);
        var selectedIds = vm.GetSelectedTweakIds();

        // Ни одного твика из других категорий быть не должно — это тот самый баг,
        // который был исправлен до MVVM-переезда (см. комментарий в SelectAll()).
        // Проверяем через полный список (фильтр "all"), а не через побочный эффект
        // внутри Select — так понятнее, что именно проверяется.
        vm.CategoryFilter = "all";
        var otherCategorySelected = vm.FilteredItems
            .Where(i => selectedIds.Contains(i.Id) && i.Category != "app")
            .ToList();

        Assert.True(selectedIds.Count > 0);
        Assert.Empty(otherCategorySelected);
    }

    [Fact]
    public void SelectNoneCommand_СнимаетВсеОтметкиНезависимоОтФильтра()
    {
        var vm = new DebloaterViewModel();
        vm.CategoryFilter = "all";
        vm.SelectAllCommand.Execute(null);
        Assert.NotEmpty(vm.GetSelectedTweakIds());

        vm.SelectNoneCommand.Execute(null);

        Assert.Empty(vm.GetSelectedTweakIds());
    }

    [Fact]
    public void SetSelectedTweakIds_ВосстанавливаетРовноПереданныеИдентификаторы()
    {
        var vm = new DebloaterViewModel();
        var allIds = vm.FilteredItems.Select(i => i.Id).ToList();
        var subset = allIds.Take(2).ToList();

        vm.SetSelectedTweakIds(subset);

        var selected = vm.GetSelectedTweakIds();
        Assert.Equal(subset.OrderBy(x => x), selected.OrderBy(x => x));
    }

    [Fact]
    public void SetSelectedTweakIds_ИгнорируетНеизвестныеИдентификаторы()
    {
        var vm = new DebloaterViewModel();

        vm.SetSelectedTweakIds(new[] { "не-существующий-id-12345" });

        Assert.Empty(vm.GetSelectedTweakIds());
    }

    // Восстановление снапшота конфигурации — второй, отдельный вход в
    // DebloatTweakExecutor: до этой правки оно не считалось с гейтом «Применить»,
    // и снапшот, восстановленный во время идущей очистки, запускал две пачки правок
    // реестра и служб внахлёст.
    [Fact]
    public async Task ApplyTweaksByIdsAsync_ВоВремяОчистки_Отказывает()
    {
        var vm = new DebloaterViewModel();
        vm.ApplyEnabled = false;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => vm.ApplyTweaksByIdsAsync(new[] { "не-существующий-id-12345" }));

        Assert.Contains("уже выполняется", ex.Message);
    }

    // Обратная сторона гейта: если бы флаг не возвращался в finally, кнопка
    // «Применить» оставалась бы заблокированной навсегда после первого же
    // восстановления снапшота.
    [Fact]
    public async Task ApplyTweaksByIdsAsync_ПоЗавершении_ВозвращаетApplyEnabled()
    {
        var vm = new DebloaterViewModel();

        var (succeeded, total) = await vm.ApplyTweaksByIdsAsync(new[] { "не-существующий-id-12345" });

        Assert.Equal(0, succeeded);
        Assert.Equal(0, total);
        Assert.True(vm.ApplyEnabled);
    }
}
