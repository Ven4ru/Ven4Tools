using System.Collections.Generic;
using Ven4Tools.Services.WindowsUpdate;
using Ven4Tools.ViewModels;
using Xunit;

namespace Ven4Tools.Tests
{
    public class WindowsUpdateViewModelTests
    {
        private static WindowsUpdateItem MakeItem(string id, long sizeBytes = 100) =>
            new() { UpdateId = id, Title = $"Патч {id}", SizeBytes = sizeBytes };

        [Fact]
        public void Конструктор_УстанавливаетДефолты()
        {
            var vm = new WindowsUpdateViewModel();

            Assert.Equal("Обновления ещё не проверялись", vm.LastCheckedText);
            Assert.Equal("", vm.StatusText);
            Assert.Empty(vm.Tree);
            Assert.False(vm.ShowEmptyState);
            Assert.Equal("Список обновлений пуст", vm.EmptyStateTitle);
            Assert.Equal("Нажмите «Проверить обновления», чтобы начать проверку", vm.EmptyStateSubtitle);
            Assert.False(vm.ShowOpenDiagnosticsButton);
            Assert.Equal("Выбрано: 0 патчей, 0 МБ", vm.SelectionSummaryText);
            Assert.False(vm.IsInstallEnabled);
            Assert.False(vm.IsSearching);
            Assert.False(vm.IsInstalling);
        }

        [Fact]
        public void CheckCommand_CanExecute_ИзначальноTrue()
        {
            var vm = new WindowsUpdateViewModel();
            Assert.True(vm.CheckCommand.CanExecute(null));
        }

        [Fact]
        public void OpenDiagnosticsCommand_ПоднимаетСобытие()
        {
            var vm = new WindowsUpdateViewModel();
            bool raised = false;
            vm.GoToDiagnostics += () => raised = true;

            vm.OpenDiagnosticsCommand.Execute(null);

            Assert.True(raised);
        }

        [Theory]
        [InlineData(false, true)]   // снятая категория — выбирается вся
        [InlineData(true, false)]   // полностью выбранная — снимается
        [InlineData(null, false)]   // частично выбранная (indeterminate) — снимается
        public void ToggleCategoryCommand_ВычисляетНовоеСостояниеПоПравилуОригинала(bool? before, bool expectedAfter)
        {
            var vm = new WindowsUpdateViewModel();
            var category = new WindowsUpdateCategoryNode
            {
                Name = "Тест",
                Items = new List<WindowsUpdateItemNode>
                {
                    new() { Item = MakeItem("1"), IsChecked = false },
                    new() { Item = MakeItem("2"), IsChecked = false }
                },
                IsChecked = before
            };

            vm.ToggleCategoryCommand.Execute(category);

            Assert.Equal(expectedAfter, category.IsChecked);
            Assert.All(category.Items, item => Assert.Equal(expectedAfter, item.IsChecked));
        }

        [Fact]
        public void SetTree_ИзменениеIsCheckedПатча_ПересчитываетКатегориюИСводку()
        {
            var vm = new WindowsUpdateViewModel();
            var item1 = new WindowsUpdateItemNode { Item = MakeItem("1"), IsChecked = false };
            var item2 = new WindowsUpdateItemNode { Item = MakeItem("2"), IsChecked = false };
            var category = new WindowsUpdateCategoryNode { Name = "Тест", Items = new List<WindowsUpdateItemNode> { item1, item2 }, IsChecked = false };

            vm.SetTree(new[] { category });

            item1.IsChecked = true;

            Assert.Null(category.IsChecked);
            Assert.StartsWith("Выбрано: 1 патчей", vm.SelectionSummaryText);

            item2.IsChecked = true;

            Assert.True(category.IsChecked);
            Assert.StartsWith("Выбрано: 2 патчей", vm.SelectionSummaryText);
        }

        [Fact]
        public void WindowsUpdateItemNode_IsChecked_ПоднимаетPropertyChanged()
        {
            var node = new WindowsUpdateItemNode { Item = MakeItem("1") };
            bool raised = false;
            node.PropertyChanged += (_, e) => raised = e.PropertyName == nameof(WindowsUpdateItemNode.IsChecked);

            node.IsChecked = true;

            Assert.True(raised);
        }

        [Fact]
        public void WindowsUpdateCategoryNode_HeaderText_ВключаетКоличество()
        {
            var category = new WindowsUpdateCategoryNode
            {
                Name = "Критические",
                Items = new List<WindowsUpdateItemNode> { new() { Item = MakeItem("1") }, new() { Item = MakeItem("2") } }
            };

            Assert.Equal("Критические (2)", category.HeaderText);
        }

        [Fact]
        public void WindowsUpdateItemNode_DisplayText_ВключаетНазвание()
        {
            var node = new WindowsUpdateItemNode { Item = MakeItem("1", 1_048_576) };

            Assert.Contains("Патч 1", node.DisplayText);
        }
    }
}
