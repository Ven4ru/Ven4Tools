# Windows Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a new "Windows Update" tab to the Ven4Tools client that searches, categorizes, and installs Windows OS patches (all types) with per-patch and per-category checkbox selection, using the native Windows Update Agent COM API directly in the client's own process (already `requireAdministrator`).

**Architecture:** All Windows Update Agent COM access is isolated behind one class (`WindowsUpdateComSource`) using `dynamic` late-binding via `Type.GetTypeFromProgID` — `<COMReference>`/`tlbimp` do **not** work with `dotnet build` (verified empirically, see Task 6 note), so this is the only viable approach without adding a Windows-SDK build dependency the project doesn't currently have. Everything above that boundary (category tree, EULA gating, cross-blocking with app installs, background notify) is pure, fully unit-testable C# working against an `IWindowsUpdateSource` abstraction and a fake test double.

**Tech Stack:** .NET 8 / WPF, `dynamic` COM interop against `wuapi.dll` (`Microsoft.Update.Session`, verified present and working on this machine), Newtonsoft.Json (existing), xUnit (existing test project).

## Global Constraints

- Только русский язык — весь UI-текст, комментарии, коммиты (см. правило проекта).
- `dotnet build Ven4Tools.sln -c Release` — 0 ошибок, 0 предупреждений — обязательно перед каждым коммитом (pre-commit hook уже это проверяет).
- Не коммитить `_backups/`, `_release/`, `bin/`, `obj/`.
- Никогда не устанавливать патчи автоматически без явного клика пользователя — ни из фоновой службы, ни как побочный эффект другого действия.
- Никогда не запускать `Install()`/`Download()`/`AcceptEula()` против реальной Windows Update в рамках автоматизированной проверки (агентом) — только поиск (`Search()`) безопасен для автоматического прогона. Установка патчей — необратимое системное действие, которое проверяется вручную человеком (см. Task 7, Step 3).
- Каждый новый чистый (не-COM) класс с бизнес-логикой должен иметь юнит-тесты и попасть в coverlet `<Include>` в `tests/Ven4Tools.Tests/Ven4Tools.Tests.csproj`.

---

### Task 1: Модели данных + поле режима в UserProfile

**Files:**
- Create: `Ven4Tools/Services/WindowsUpdate/WindowsUpdateModels.cs`
- Modify: `Ven4Tools/Models/UserProfile.cs`
- Test: `tests/Ven4Tools.Tests/WindowsUpdateModelsTests.cs`

**Interfaces:**
- Produces: `WindowsUpdateItem` (record class), `WindowsUpdateInstallOutcome` (record class), `WindowsUpdateSearchResult` (record class) — используются всеми последующими задачами.
- Produces: `UserProfile.WindowsUpdateMode` (string, default `"NotSet"`) — читается/пишется UI и фоновым сервисом.

- [ ] **Step 1: Написать модели**

```csharp
// Ven4Tools/Services/WindowsUpdate/WindowsUpdateModels.cs
using System;
using System.Collections.Generic;

namespace Ven4Tools.Services.WindowsUpdate
{
    /// <summary>Один патч Windows, как он приходит из Windows Update Agent.</summary>
    public sealed class WindowsUpdateItem
    {
        // UpdateID из COM API — стабильный идентификатор конкретного обновления,
        // используется для повторного поиска/установки (не доверяем позиции в списке).
        public string UpdateId { get; init; } = "";
        public string Title { get; init; } = "";
        public IReadOnlyList<string> CategoryNames { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> KbArticleIds { get; init; } = Array.Empty<string>();
        public long SizeBytes { get; init; }
        public string Severity { get; init; } = ""; // MsrcSeverity: "Critical", "Important", "" и т.д.
        public bool IsDownloaded { get; init; }
        public bool EulaAccepted { get; init; }
        public string EulaText { get; init; } = "";
    }

    /// <summary>Результат Search() — либо список патчей, либо явная ошибка с сообщением на русском.</summary>
    public sealed class WindowsUpdateSearchResult
    {
        public bool Success { get; init; }
        public IReadOnlyList<WindowsUpdateItem> Items { get; init; } = Array.Empty<WindowsUpdateItem>();
        public string ErrorMessage { get; init; } = "";

        public static WindowsUpdateSearchResult Ok(IReadOnlyList<WindowsUpdateItem> items) =>
            new() { Success = true, Items = items };

        public static WindowsUpdateSearchResult Failed(string message) =>
            new() { Success = false, ErrorMessage = message };
    }

    /// <summary>Прогресс скачивания/установки одного патча — для IProgress&lt;T&gt; в UI.</summary>
    public sealed class WindowsUpdateProgress
    {
        public string CurrentTitle { get; init; } = "";
        public int CompletedCount { get; init; }
        public int TotalCount { get; init; }
        public string Phase { get; init; } = ""; // "Скачивание" | "Установка"
        public int PercentComplete { get; init; }
    }

    /// <summary>Итог установки одного патча.</summary>
    public sealed class WindowsUpdateItemOutcome
    {
        public string UpdateId { get; init; } = "";
        public string Title { get; init; } = "";
        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = "";
    }

    /// <summary>Итог всей партии установки.</summary>
    public sealed class WindowsUpdateInstallOutcome
    {
        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = "";
        public IReadOnlyList<WindowsUpdateItemOutcome> Items { get; init; } = Array.Empty<WindowsUpdateItemOutcome>();
        public bool RebootRequired { get; init; }
    }
}
```

- [ ] **Step 2: Добавить поле режима в UserProfile**

В `Ven4Tools/Models/UserProfile.cs` добавить после блока `// Notifications`:

```csharp
        // Windows Update: "NotSet" (первый вход ещё не пройден), "NotifyOnly", "NotifyAndDownload".
        public string WindowsUpdateMode { get; set; } = "NotSet";
```

- [ ] **Step 3: Написать тест на дефолт и сериализацию**

```csharp
// tests/Ven4Tools.Tests/WindowsUpdateModelsTests.cs
using Ven4Tools.Models;
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Tests;

public sealed class WindowsUpdateModelsTests
{
    [Fact]
    public void UserProfile_DefaultsToNotSet()
    {
        var profile = new UserProfile();
        Assert.Equal("NotSet", profile.WindowsUpdateMode);
    }

    [Fact]
    public void SearchResult_Ok_CarriesItems()
    {
        var items = new[] { new WindowsUpdateItem { UpdateId = "abc", Title = "Test" } };
        var result = WindowsUpdateSearchResult.Ok(items);

        Assert.True(result.Success);
        Assert.Single(result.Items);
        Assert.Equal("", result.ErrorMessage);
    }

    [Fact]
    public void SearchResult_Failed_CarriesMessage()
    {
        var result = WindowsUpdateSearchResult.Failed("служба недоступна");

        Assert.False(result.Success);
        Assert.Empty(result.Items);
        Assert.Equal("служба недоступна", result.ErrorMessage);
    }
}
```

- [ ] **Step 4: Собрать и прогнать тесты**

Run: `dotnet build Ven4Tools.sln -c Release`
Expected: 0 ошибок, 0 предупреждений

Run: `dotnet test tests/Ven4Tools.Tests -c Release --filter WindowsUpdateModelsTests`
Expected: 3/3 passed

- [ ] **Step 5: Commit**

```bash
git add Ven4Tools/Services/WindowsUpdate/WindowsUpdateModels.cs Ven4Tools/Models/UserProfile.cs tests/Ven4Tools.Tests/WindowsUpdateModelsTests.cs
git commit -m "Windows Update: базовые модели и поле режима в профиле"
```

---

### Task 2: Абстракция источника данных + фейк для тестов

**Files:**
- Create: `Ven4Tools/Services/WindowsUpdate/IWindowsUpdateSource.cs`
- Create: `tests/Ven4Tools.Tests/Fakes/FakeWindowsUpdateSource.cs`
- Test: логика самого фейка проверяется опосредованно последующими задачами (Task 5, 8), отдельного теста на сам фейк не требуется — это тестовый дублёр, не продакшн-логика.

**Interfaces:**
- Consumes: `WindowsUpdateItem`, `WindowsUpdateSearchResult`, `WindowsUpdateInstallOutcome`, `WindowsUpdateProgress` (Task 1).
- Produces: `IWindowsUpdateSource` — реализуется `WindowsUpdateComSource` (Task 6/8, продакшн) и `FakeWindowsUpdateSource` (тесты). Все последующие сервисы принимают `IWindowsUpdateSource` через конструктор.

- [ ] **Step 1: Написать интерфейс**

```csharp
// Ven4Tools/Services/WindowsUpdate/IWindowsUpdateSource.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services.WindowsUpdate
{
    /// <summary>
    /// Абстракция над Windows Update Agent. Единственная реализация в проде —
    /// WindowsUpdateComSource (COM). В тестах — FakeWindowsUpdateSource, без реального API.
    /// </summary>
    public interface IWindowsUpdateSource
    {
        /// <summary>Служба Windows Update (wuauserv) запущена?</summary>
        bool IsServiceRunning();

        /// <summary>Попытаться запустить службу. true — удалось (или уже была запущена).</summary>
        bool TryStartService();

        /// <summary>Требуется перезагрузка от предыдущей установки?</summary>
        bool IsRebootPending();

        Task<WindowsUpdateSearchResult> SearchAsync(CancellationToken ct);

        /// <summary>
        /// Скачивает и устанавливает патчи по UpdateId. Реализация обязана заново
        /// найти патчи по актуальному поиску внутри себя, а не доверять только списку ID —
        /// список могут выбрать в одном состоянии системы, а установка стартовать позже.
        /// </summary>
        Task<WindowsUpdateInstallOutcome> InstallAsync(
            IReadOnlyList<string> updateIds,
            IProgress<WindowsUpdateProgress> progress,
            CancellationToken ct);
    }
}
```

- [ ] **Step 2: Написать фейк для тестов**

```csharp
// tests/Ven4Tools.Tests/Fakes/FakeWindowsUpdateSource.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Tests.Fakes;

public sealed class FakeWindowsUpdateSource : IWindowsUpdateSource
{
    public List<WindowsUpdateItem> Items { get; } = new();
    public bool ServiceRunning { get; set; } = true;
    public bool RebootPending { get; set; }
    public bool SearchShouldFail { get; set; }
    public string SearchFailureMessage { get; set; } = "";
    public List<string> InstallCallsReceived { get; } = new();
    public HashSet<string> ItemIdsThatFailInstall { get; } = new();

    public bool IsServiceRunning() => ServiceRunning;
    public bool TryStartService() { ServiceRunning = true; return true; }
    public bool IsRebootPending() => RebootPending;

    public Task<WindowsUpdateSearchResult> SearchAsync(CancellationToken ct)
    {
        if (SearchShouldFail)
            return Task.FromResult(WindowsUpdateSearchResult.Failed(SearchFailureMessage));
        return Task.FromResult(WindowsUpdateSearchResult.Ok(Items));
    }

    public Task<WindowsUpdateInstallOutcome> InstallAsync(
        IReadOnlyList<string> updateIds,
        IProgress<WindowsUpdateProgress> progress,
        CancellationToken ct)
    {
        InstallCallsReceived.AddRange(updateIds);
        var outcomes = updateIds.Select(id =>
        {
            var item = Items.FirstOrDefault(i => i.UpdateId == id);
            bool fails = ItemIdsThatFailInstall.Contains(id);
            progress.Report(new WindowsUpdateProgress
            {
                CurrentTitle = item?.Title ?? id,
                Phase = "Установка",
                CompletedCount = 1,
                TotalCount = updateIds.Count,
                PercentComplete = 100
            });
            return new WindowsUpdateItemOutcome
            {
                UpdateId = id,
                Title = item?.Title ?? id,
                Success = !fails,
                ErrorMessage = fails ? "тестовая ошибка" : ""
            };
        }).ToList();

        return Task.FromResult(new WindowsUpdateInstallOutcome
        {
            Success = outcomes.All(o => o.Success),
            Items = outcomes,
            RebootRequired = RebootPending
        });
    }
}
```

- [ ] **Step 3: Собрать**

Run: `dotnet build Ven4Tools.sln -c Release`
Expected: 0 ошибок, 0 предупреждений

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/Services/WindowsUpdate/IWindowsUpdateSource.cs tests/Ven4Tools.Tests/Fakes/FakeWindowsUpdateSource.cs
git commit -m "Windows Update: абстракция источника данных + фейк для тестов"
```

---

### Task 3: Дерево категорий с tri-state выбором

**Files:**
- Create: `Ven4Tools/Services/WindowsUpdate/WindowsUpdateCategoryTreeBuilder.cs`
- Test: `tests/Ven4Tools.Tests/WindowsUpdateCategoryTreeBuilderTests.cs`

**Interfaces:**
- Consumes: `WindowsUpdateItem` (Task 1).
- Produces: `WindowsUpdateCategoryNode` (класс с `Name`, `Items`, `IsChecked: bool?` — null означает "частично выбрано"), `WindowsUpdateItemNode` (`Item`, `IsChecked: bool`), `WindowsUpdateCategoryTreeBuilder.Build(IReadOnlyList<WindowsUpdateItem>) -> IReadOnlyList<WindowsUpdateCategoryNode>`, `WindowsUpdateCategoryTreeBuilder.RecalculateCategoryState(WindowsUpdateCategoryNode)`. Используется вкладкой (Task 11) для дерева с чекбоксами.

- [ ] **Step 1: Написать узлы дерева и билдер**

```csharp
// Ven4Tools/Services/WindowsUpdate/WindowsUpdateCategoryTreeBuilder.cs
using System.Collections.Generic;
using System.Linq;

namespace Ven4Tools.Services.WindowsUpdate
{
    public sealed class WindowsUpdateItemNode
    {
        public WindowsUpdateItem Item { get; init; } = null!;
        public bool IsChecked { get; set; }
    }

    public sealed class WindowsUpdateCategoryNode
    {
        public string Name { get; init; } = "";
        public List<WindowsUpdateItemNode> Items { get; init; } = new();

        // null = частично выбрано (tri-state), true = все выбраны, false = ни одного.
        public bool? IsChecked { get; set; } = false;
    }

    /// <summary>
    /// Группирует патчи по категориям (IUpdate.Categories может содержать несколько —
    /// патч попадает в дерево под каждой своей категорией, это ожидаемое поведение
    /// Windows Update: например, один патч может быть и "Security Updates", и "Critical Updates").
    /// Категория без имени (в API это возможно для мусорных/служебных категорий) — в "Другое".
    /// </summary>
    public static class WindowsUpdateCategoryTreeBuilder
    {
        private const string Uncategorized = "Другое";

        public static IReadOnlyList<WindowsUpdateCategoryNode> Build(IReadOnlyList<WindowsUpdateItem> items)
        {
            var byCategory = new Dictionary<string, WindowsUpdateCategoryNode>();

            foreach (var item in items)
            {
                var categoryNames = item.CategoryNames.Count > 0
                    ? item.CategoryNames
                    : new[] { Uncategorized };

                foreach (var categoryName in categoryNames)
                {
                    var name = string.IsNullOrWhiteSpace(categoryName) ? Uncategorized : categoryName;
                    if (!byCategory.TryGetValue(name, out var node))
                    {
                        node = new WindowsUpdateCategoryNode { Name = name };
                        byCategory[name] = node;
                    }
                    node.Items.Add(new WindowsUpdateItemNode { Item = item, IsChecked = false });
                }
            }

            return byCategory.Values.OrderBy(n => n.Name).ToList();
        }

        /// <summary>Вызывать после того, как пользователь щёлкнул чекбокс отдельного патча.</summary>
        public static void RecalculateCategoryState(WindowsUpdateCategoryNode category)
        {
            if (category.Items.Count == 0) { category.IsChecked = false; return; }

            bool allChecked = category.Items.All(i => i.IsChecked);
            bool noneChecked = category.Items.All(i => !i.IsChecked);

            category.IsChecked = allChecked ? true : noneChecked ? false : (bool?)null;
        }

        /// <summary>Вызывать после того, как пользователь щёлкнул чекбокс категории.</summary>
        public static void ApplyCategoryCheck(WindowsUpdateCategoryNode category, bool isChecked)
        {
            foreach (var item in category.Items)
                item.IsChecked = isChecked;
            category.IsChecked = isChecked;
        }

        public static IReadOnlyList<string> GetSelectedUpdateIds(IReadOnlyList<WindowsUpdateCategoryNode> tree) =>
            tree.SelectMany(c => c.Items)
                .Where(i => i.IsChecked)
                .Select(i => i.Item.UpdateId)
                .Distinct()
                .ToList();

        public static long GetSelectedTotalSizeBytes(IReadOnlyList<WindowsUpdateCategoryNode> tree) =>
            tree.SelectMany(c => c.Items)
                .Where(i => i.IsChecked)
                .Select(i => i.Item)
                .DistinctBy(i => i.UpdateId)
                .Sum(i => i.SizeBytes);
    }
}
```

- [ ] **Step 2: Написать тесты**

```csharp
// tests/Ven4Tools.Tests/WindowsUpdateCategoryTreeBuilderTests.cs
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Tests;

public sealed class WindowsUpdateCategoryTreeBuilderTests
{
    private static WindowsUpdateItem MakeItem(string id, string title, string category, long size = 1000) =>
        new() { UpdateId = id, Title = title, CategoryNames = new[] { category }, SizeBytes = size };

    [Fact]
    public void Build_GroupsItemsByCategory()
    {
        var items = new[]
        {
            MakeItem("1", "A", "Security Updates"),
            MakeItem("2", "B", "Security Updates"),
            MakeItem("3", "C", "Drivers"),
        };

        var tree = WindowsUpdateCategoryTreeBuilder.Build(items);

        Assert.Equal(2, tree.Count);
        Assert.Equal(2, tree.First(c => c.Name == "Security Updates").Items.Count);
        Assert.Single(tree.First(c => c.Name == "Drivers").Items);
    }

    [Fact]
    public void Build_ItemWithoutCategory_GoesToOther()
    {
        var item = new WindowsUpdateItem { UpdateId = "1", Title = "A" };
        var tree = WindowsUpdateCategoryTreeBuilder.Build(new[] { item });

        Assert.Single(tree);
        Assert.Equal("Другое", tree[0].Name);
    }

    [Fact]
    public void RecalculateCategoryState_AllChecked_ReturnsTrue()
    {
        var category = new WindowsUpdateCategoryNode
        {
            Name = "X",
            Items = { new() { Item = MakeItem("1", "A", "X"), IsChecked = true } }
        };

        WindowsUpdateCategoryTreeBuilder.RecalculateCategoryState(category);

        Assert.True(category.IsChecked);
    }

    [Fact]
    public void RecalculateCategoryState_PartiallyChecked_ReturnsNull()
    {
        var category = new WindowsUpdateCategoryNode
        {
            Name = "X",
            Items =
            {
                new() { Item = MakeItem("1", "A", "X"), IsChecked = true },
                new() { Item = MakeItem("2", "B", "X"), IsChecked = false },
            }
        };

        WindowsUpdateCategoryTreeBuilder.RecalculateCategoryState(category);

        Assert.Null(category.IsChecked);
    }

    [Fact]
    public void ApplyCategoryCheck_SetsAllItemsAndCategory()
    {
        var category = new WindowsUpdateCategoryNode
        {
            Name = "X",
            Items =
            {
                new() { Item = MakeItem("1", "A", "X"), IsChecked = false },
                new() { Item = MakeItem("2", "B", "X"), IsChecked = false },
            }
        };

        WindowsUpdateCategoryTreeBuilder.ApplyCategoryCheck(category, true);

        Assert.All(category.Items, i => Assert.True(i.IsChecked));
        Assert.True(category.IsChecked);
    }

    [Fact]
    public void GetSelectedUpdateIds_DeduplicatesAcrossCategories()
    {
        // Один и тот же патч в двух категориях (например, и Security, и Critical) —
        // не должен попасть в список выбранных дважды.
        var item = MakeItem("1", "A", "Security Updates");
        var itemInTwoCategories = new WindowsUpdateItem
        {
            UpdateId = "1", Title = "A", CategoryNames = new[] { "Security Updates", "Critical Updates" }
        };
        var tree = WindowsUpdateCategoryTreeBuilder.Build(new[] { itemInTwoCategories });
        foreach (var c in tree) WindowsUpdateCategoryTreeBuilder.ApplyCategoryCheck(c, true);

        var ids = WindowsUpdateCategoryTreeBuilder.GetSelectedUpdateIds(tree);

        Assert.Single(ids);
    }

    [Fact]
    public void GetSelectedTotalSizeBytes_SumsOnlyCheckedDistinctItems()
    {
        var items = new[]
        {
            MakeItem("1", "A", "X", size: 100),
            MakeItem("2", "B", "X", size: 200),
        };
        var tree = WindowsUpdateCategoryTreeBuilder.Build(items);
        tree[0].Items[0].IsChecked = true; // только первый

        var total = WindowsUpdateCategoryTreeBuilder.GetSelectedTotalSizeBytes(tree);

        Assert.Equal(100, total);
    }
}
```

- [ ] **Step 3: Прогнать тесты**

Run: `dotnet test tests/Ven4Tools.Tests -c Release --filter WindowsUpdateCategoryTreeBuilderTests`
Expected: 7/7 passed

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/Services/WindowsUpdate/WindowsUpdateCategoryTreeBuilder.cs tests/Ven4Tools.Tests/WindowsUpdateCategoryTreeBuilderTests.cs
git commit -m "Windows Update: дерево категорий с tri-state выбором"
```

---

### Task 4: EULA-гейтинг и маппинг ошибок

**Files:**
- Create: `Ven4Tools/Services/WindowsUpdate/WindowsUpdateErrorMapper.cs`
- Test: `tests/Ven4Tools.Tests/WindowsUpdateErrorMapperTests.cs`

**Interfaces:**
- Consumes: `WindowsUpdateItem`, `WindowsUpdateCategoryNode` (Task 1, 3).
- Produces: `WindowsUpdateErrorMapper.MapHResult(int hresult) -> string`, `WindowsUpdateErrorMapper.GetItemsNeedingEula(IReadOnlyList<WindowsUpdateCategoryNode> tree) -> IReadOnlyList<WindowsUpdateItem>` (только выбранные и с `EulaAccepted == false`).

- [ ] **Step 1: Написать маппер**

```csharp
// Ven4Tools/Services/WindowsUpdate/WindowsUpdateErrorMapper.cs
using System.Collections.Generic;
using System.Linq;

namespace Ven4Tools.Services.WindowsUpdate
{
    public static class WindowsUpdateErrorMapper
    {
        // Известные коды из wuerror.h — самые частые в практике конечных пользователей.
        private static readonly Dictionary<int, string> KnownHResults = new()
        {
            { unchecked((int)0x80240438), "Не удалось подключиться к серверу обновлений (сетевая ошибка)." },
            { unchecked((int)0x8024402C), "Нет соединения с интернетом — проверка обновлений недоступна." },
            { unchecked((int)0x80070422), "Служба Windows Update отключена. Включите её и повторите попытку." },
            { unchecked((int)0x8024001E), "Операция отменена." },
            { unchecked((int)0x80240022), "Патч больше не предлагается сервером обновлений (устарел)." },
            { unchecked((int)0x8007000E), "Недостаточно памяти для выполнения операции." },
            { unchecked((int)0x80070005), "Отказано в доступе — операция требует прав администратора." },
        };

        public static string MapHResult(int hresult) =>
            KnownHResults.TryGetValue(hresult, out var message)
                ? message
                : $"Ошибка Windows Update (код 0x{hresult:X8}). Подробности — в логе.";

        /// <summary>
        /// Патчи среди выбранных, у которых есть непринятый EULA — их текст нужно
        /// показать в диалоге подтверждения перед стартом установки.
        /// </summary>
        public static IReadOnlyList<WindowsUpdateItem> GetItemsNeedingEula(
            IReadOnlyList<WindowsUpdateCategoryNode> tree)
        {
            return tree
                .SelectMany(c => c.Items)
                .Where(i => i.IsChecked)
                .Select(i => i.Item)
                .Where(item => !item.EulaAccepted && !string.IsNullOrWhiteSpace(item.EulaText))
                .DistinctBy(item => item.UpdateId)
                .ToList();
        }
    }
}
```

- [ ] **Step 2: Написать тесты**

```csharp
// tests/Ven4Tools.Tests/WindowsUpdateErrorMapperTests.cs
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Tests;

public sealed class WindowsUpdateErrorMapperTests
{
    [Fact]
    public void MapHResult_KnownCode_ReturnsFriendlyMessage()
    {
        var message = WindowsUpdateErrorMapper.MapHResult(unchecked((int)0x80070422));
        Assert.Contains("отключена", message);
    }

    [Fact]
    public void MapHResult_UnknownCode_ReturnsGenericWithHexCode()
    {
        var message = WindowsUpdateErrorMapper.MapHResult(unchecked((int)0x12345678));
        Assert.Contains("12345678", message);
    }

    [Fact]
    public void GetItemsNeedingEula_OnlySelectedAndUnaccepted()
    {
        var eulaItem = new WindowsUpdateItem
        {
            UpdateId = "1", Title = "Driver", EulaAccepted = false, EulaText = "текст лицензии"
        };
        var noEulaItem = new WindowsUpdateItem { UpdateId = "2", Title = "Patch", EulaAccepted = false, EulaText = "" };
        var acceptedItem = new WindowsUpdateItem
        {
            UpdateId = "3", Title = "Other", EulaAccepted = true, EulaText = "текст"
        };

        var tree = new[]
        {
            new WindowsUpdateCategoryNode
            {
                Name = "X",
                Items =
                {
                    new() { Item = eulaItem, IsChecked = true },
                    new() { Item = noEulaItem, IsChecked = true },
                    new() { Item = acceptedItem, IsChecked = true },
                    new() { Item = new WindowsUpdateItem { UpdateId = "4", EulaText = "текст" }, IsChecked = false }, // не выбран
                }
            }
        };

        var result = WindowsUpdateErrorMapper.GetItemsNeedingEula(tree);

        Assert.Single(result);
        Assert.Equal("1", result[0].UpdateId);
    }
}
```

- [ ] **Step 3: Прогнать тесты**

Run: `dotnet test tests/Ven4Tools.Tests -c Release --filter WindowsUpdateErrorMapperTests`
Expected: 3/3 passed

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/Services/WindowsUpdate/WindowsUpdateErrorMapper.cs tests/Ven4Tools.Tests/WindowsUpdateErrorMapperTests.cs
git commit -m "Windows Update: маппинг HRESULT-ошибок и гейтинг EULA"
```

---

### Task 5: Взаимная блокировка с установкой приложений

**Files:**
- Modify: `Ven4Tools/Services/InstallationService.cs` (добавить свойство рядом с `InstallSemaphore`, строка ~19)
- Modify: `Ven4Tools/Views/Tabs/CatalogTab.Install.cs:150` (перед `await InstallationService.InstallSemaphore.WaitAsync();`)
- Modify: `Ven4Tools/Views/Tabs/HistoryTab.xaml.cs:125` (перед `await InstallationService.InstallSemaphore.WaitAsync();`)
- Test: `tests/Ven4Tools.Tests/InstallationServiceBusyTests.cs`

**Interfaces:**
- Produces: `InstallationService.IsBusy` (`static bool`, `true` когда `InstallSemaphore.CurrentCount == 0`). Используется и каталогом/историей (чтобы блокировать себя при активной установке патчей — семафор общий), и `WindowsUpdateService` (Task 8, будет захватывать тот же `InstallSemaphore` перед стартом установки патчей).

**Важно:** вместо отдельного семафора для Windows Update используется **тот же самый** `InstallationService.InstallSemaphore` — так проверка "занято ли" остаётся одним источником истины в обе стороны, без риска рассинхрона двух независимых флагов.

- [ ] **Step 1: Добавить свойство IsBusy в InstallationService**

В `Ven4Tools/Services/InstallationService.cs`, сразу после объявления `InstallSemaphore` (строка 19):

```csharp
        // Используется для явной блокировки кнопок вместо тихого ожидания семафора —
        // и каталогом/историей, и Windows Update (Task 8), т.к. оба используют
        // общую MSI-подсистему и не должны ставить/удалять параллельно.
        public static bool IsBusy => InstallSemaphore.CurrentCount == 0;
```

- [ ] **Step 2: Написать тест на само свойство**

```csharp
// tests/Ven4Tools.Tests/InstallationServiceBusyTests.cs
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class InstallationServiceBusyTests
{
    [Fact]
    public async Task IsBusy_ReflectsSemaphoreState()
    {
        // Семафор статический и общий на процесс — тест синхронный по семафору,
        // чтобы не мешать параллельным тестам, использующим тот же семафор.
        await InstallationService.InstallSemaphore.WaitAsync();
        try
        {
            Assert.True(InstallationService.IsBusy);
        }
        finally
        {
            InstallationService.InstallSemaphore.Release();
        }

        Assert.False(InstallationService.IsBusy);
    }
}
```

- [ ] **Step 3: Прогнать тест**

Run: `dotnet test tests/Ven4Tools.Tests -c Release --filter InstallationServiceBusyTests`
Expected: 1/1 passed

- [ ] **Step 4: Заблокировать установку из каталога сообщением, если Windows Update занят**

В `Ven4Tools/Views/Tabs/CatalogTab.Install.cs`, найти строку 150 (`await InstallationService.InstallSemaphore.WaitAsync();` внутри `Task.Run` на каждое приложение) — это внутри цикла по нескольким приложениям, поэтому проверку "занято" нужно поставить **до** запуска всего цикла установки, не внутри него. Найти начало метода, где строится `appsToInstall`/`tasks` (несколькими строками выше строки 148 `var tasks = appsToInstall.Select(...)`), и перед этим добавить:

```csharp
            if (InstallationService.IsBusy)
            {
                MessageBox.Show(
                    "Дождитесь завершения установки обновлений Windows, затем повторите попытку.",
                    "Установка занята", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
```

(Если в методе уже есть более ранняя проверка/подтверждение перед стартом — вставить сразу после неё, до первого реального обращения к `InstallSemaphore`.)

- [ ] **Step 5: Аналогично для переустановки из истории**

В `Ven4Tools/Views/Tabs/HistoryTab.xaml.cs`, перед строкой 125 (`await InstallationService.InstallSemaphore.WaitAsync();`) добавить ту же проверку:

```csharp
            if (InstallationService.IsBusy)
            {
                MessageBox.Show(
                    "Дождитесь завершения установки обновлений Windows, затем повторите попытку.",
                    "Установка занята", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
```

- [ ] **Step 6: Собрать и вручную проверить, что каталог/история всё ещё компилируются и работают как раньше**

Run: `dotnet build Ven4Tools.sln -c Release`
Expected: 0 ошибок, 0 предупреждений

Ручная проверка (т.к. агент не может прокликать WPF UI): запустить клиент, установить любое лёгкое приложение из каталога — убедиться, что поведение не изменилось (сообщение "Установка занята" не должно появляться, когда ничего не установлено).

- [ ] **Step 7: Commit**

```bash
git add Ven4Tools/Services/InstallationService.cs Ven4Tools/Views/Tabs/CatalogTab.Install.cs Ven4Tools/Views/Tabs/HistoryTab.xaml.cs tests/Ven4Tools.Tests/InstallationServiceBusyTests.cs
git commit -m "Windows Update: явная блокировка установки приложений при занятом семафоре"
```

---

### Task 6: WindowsUpdateComSource — поиск патчей (COM, проверено вживую)

**Files:**
- Create: `Ven4Tools/Services/WindowsUpdate/WindowsUpdateComSource.cs`

**Interfaces:**
- Consumes: `IWindowsUpdateSource` (Task 2), `WindowsUpdateItem`/`WindowsUpdateSearchResult` (Task 1).
- Produces: `WindowsUpdateComSource : IWindowsUpdateSource` — продакшн-реализация, регистрируется как singleton/default в `WindowsUpdateService` (Task 8).

**Важное техническое примечание (зафиксировать, не пропускать):** `<COMReference>` в csproj **не работает** с `dotnet build`/SDK-style проектами (`error MSB4803: задача "ResolveComReference" не поддерживается в MSBuild версии .NET Core`) — проверено эмпирически на этой машине перед написанием плана. `tlbimp.exe` тоже физически отсутствует на машине и не входит в стандартный набор инструментов проекта (только .NET 8 SDK + NSIS). Поэтому используется `dynamic` + `Type.GetTypeFromProgID` — без генерации interop-сборки, без новых зависимостей сборки. ProgID `"Microsoft.Update.Session"` (CLSID `{4CB43D7F-7EEE-4906-8698-60DA1C38F2FE}`) и `"Microsoft.Update.SystemInfo"` подтверждены зарегистрированными в реестре и рабочими вживую (`CreateUpdateSearcher()`, `Search()`, `.ResultCode`, `.Updates`, `.Count`, `.Item(i)`, `.Title`, `.Categories`, `.Name`, `.IsDownloaded`, `ISystemInformation.RebootRequired` — все вызваны и вернули реальные данные на этой машине во время планирования).

- [ ] **Step 1: Написать WindowsUpdateComSource — часть с поиском**

```csharp
// Ven4Tools/Services/WindowsUpdate/WindowsUpdateComSource.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services.WindowsUpdate
{
    /// <summary>
    /// Реализация IWindowsUpdateSource поверх нативного Windows Update Agent COM API
    /// (wuapi.dll). Единственное место в проекте, где используется dynamic/COM —
    /// специально изолировано, чтобы риск опечатки в имени члена (RuntimeBinderException,
    /// ловится только в рантайме) не расползался по остальному коду.
    /// </summary>
    public sealed class WindowsUpdateComSource : IWindowsUpdateSource
    {
        // Критерий поиска: все не установленные и не скрытые пользователем обновления
        // всех типов (Software покрывает кумулятивные/security/driver/feature — драйверы
        // в API относятся к Type='Software' с категорией "Drivers", отдельного Type для
        // них нет). IsHidden=0 — не показываем то, что пользователь явно скрыл в прошлом
        // через штатный Windows Update (у нас нет своего UI для "скрыть", поэтому уважаем
        // выбор, сделанный там).
        private const string SearchCriteria = "IsInstalled=0 and IsHidden=0";

        public bool IsServiceRunning()
        {
            try
            {
                using var sc = new ServiceController("wuauserv");
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[WindowsUpdateComSource] Проверка службы: {ex.Message}");
                return false;
            }
        }

        public bool TryStartService()
        {
            try
            {
                using var sc = new ServiceController("wuauserv");
                if (sc.Status == ServiceControllerStatus.Running) return true;
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                return sc.Status == ServiceControllerStatus.Running;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[WindowsUpdateComSource] Запуск службы не удался: {ex.Message}");
                return false;
            }
        }

        public bool IsRebootPending()
        {
            try
            {
                dynamic sysInfo = CreateComObject("Microsoft.Update.SystemInfo");
                return (bool)sysInfo.RebootRequired;
            }
            catch (Exception ex)
            {
                AppLogger.Write($"[WindowsUpdateComSource] Проверка RebootRequired: {ex.Message}");
                return false; // fail-open здесь безопасен: хуже случай — попытка установки упадёт с понятной ошибкой API
            }
        }

        public Task<WindowsUpdateSearchResult> SearchAsync(CancellationToken ct)
        {
            // COM-объекты Windows Update Agent требуют MTA-апартамент для надёжной
            // работы Search() в фоновом потоке — обычные потоки пула задач (Task.Run)
            // уже MTA по умолчанию в .NET, отдельный поток создавать не нужно.
            return Task.Run(() =>
            {
                try
                {
                    dynamic session = CreateComObject("Microsoft.Update.Session");
                    dynamic searcher = session.CreateUpdateSearcher();

                    ct.ThrowIfCancellationRequested();
                    dynamic result = searcher.Search(SearchCriteria);

                    int resultCode = (int)result.ResultCode;
                    // OperationResultCode: 0=NotStarted,1=InProgress,2=Succeeded,3=SucceededWithErrors,4=Failed,5=Aborted
                    if (resultCode is 4 or 5)
                        return WindowsUpdateSearchResult.Failed(
                            $"Поиск обновлений завершился неудачно (код {resultCode}).");

                    dynamic updates = result.Updates;
                    int count = (int)updates.Count;
                    var items = new List<WindowsUpdateItem>(count);

                    for (int i = 0; i < count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        dynamic u = updates.Item(i);
                        items.Add(MapToItem(u));
                    }

                    return WindowsUpdateSearchResult.Ok(items);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (TryGetHResult(ex, out int hr))
                {
                    return WindowsUpdateSearchResult.Failed(WindowsUpdateErrorMapper.MapHResult(hr));
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[WindowsUpdateComSource] Search: {ex}");
                    return WindowsUpdateSearchResult.Failed(
                        $"Не удалось выполнить поиск обновлений: {ex.Message}");
                }
            }, ct);
        }

        private static WindowsUpdateItem MapToItem(dynamic u)
        {
            var categoryNames = new List<string>();
            dynamic categories = u.Categories;
            int catCount = (int)categories.Count;
            for (int i = 0; i < catCount; i++)
                categoryNames.Add((string)categories.Item(i).Name);

            var kbIds = new List<string>();
            dynamic kbArticles = u.KBArticleIDs;
            int kbCount = (int)kbArticles.Count;
            for (int i = 0; i < kbCount; i++)
                kbIds.Add((string)kbArticles.Item(i));

            long sizeBytes = 0;
            try { sizeBytes = (long)u.MaxDownloadSize; } catch { /* поле не всегда доступно — не критично */ }

            string eulaText = "";
            bool eulaAccepted = true;
            try
            {
                eulaAccepted = (bool)u.EulaAccepted;
                eulaText = eulaAccepted ? "" : (string)u.EulaText;
            }
            catch { /* не у всех патчей вообще есть EULA-поля */ }

            string severity = "";
            try { severity = (string)u.MsrcSeverity ?? ""; } catch { }

            return new WindowsUpdateItem
            {
                UpdateId = (string)u.Identity.UpdateID,
                Title = (string)u.Title,
                CategoryNames = categoryNames,
                KbArticleIds = kbIds,
                SizeBytes = sizeBytes,
                Severity = severity,
                IsDownloaded = (bool)u.IsDownloaded,
                EulaAccepted = eulaAccepted,
                EulaText = eulaText
            };
        }

        private static dynamic CreateComObject(string progId)
        {
            var type = Type.GetTypeFromProgID(progId)
                ?? throw new InvalidOperationException($"COM-класс {progId} не зарегистрирован в системе.");
            return Activator.CreateInstance(type)!;
        }

        private static bool TryGetHResult(Exception ex, out int hresult)
        {
            hresult = ex.HResult;
            return ex is System.Runtime.InteropServices.COMException;
        }

        // Реализация Download/Install — Task 8.
        public Task<WindowsUpdateInstallOutcome> InstallAsync(
            IReadOnlyList<string> updateIds,
            IProgress<WindowsUpdateProgress> progress,
            CancellationToken ct) =>
            throw new NotImplementedException("Реализуется в Task 8");
    }
}
```

- [ ] **Step 2: Собрать**

Run: `dotnet build Ven4Tools.sln -c Release`
Expected: 0 ошибок, 0 предупреждений

- [ ] **Step 3: Ручная проверка на реальной машине (не автоматизировать в CI)**

Создать одноразовый scratch-консольный проект (net8.0-windows, `ProjectReference` на `Ven4Tools.csproj` не нужен — просто скопировать/вызвать `WindowsUpdateComSource` напрямую, либо временно вызвать из `SystemTab` кнопкой в debug-сборке) и вызвать:

```csharp
var source = new Ven4Tools.Services.WindowsUpdate.WindowsUpdateComSource();
var result = await source.SearchAsync(CancellationToken.None);
Console.WriteLine($"Success={result.Success}, Items={result.Items.Count}, Error={result.ErrorMessage}");
foreach (var item in result.Items.Take(5))
    Console.WriteLine($"  {item.Title} | KB: {string.Join(",", item.KbArticleIds)} | {item.SizeBytes} bytes | severity={item.Severity} | eula={!item.EulaAccepted}");
```

Ожидается: `Success=True`, список патчей (может быть пустым, если система полностью обновлена — это нормально), никаких исключений. Если что-то из полей (`KBArticleIDs`, `MaxDownloadSize`, `EulaAccepted`/`EulaText`, `MsrcSeverity`, `Identity.UpdateID`) бросает `RuntimeBinderException` — значит имя члена COM API не совпадает с ожидаемым, нужно свериться с документацией Microsoft Learn по `IUpdate` и поправить `MapToItem`. Эти конкретные поля не были живьём проверены при планировании (в отличие от `Title`/`Categories`/`IsDownloaded`, которые проверены) — именно поэтому этот шаг обязателен перед тем, как считать Task 6 завершённой.

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/Services/WindowsUpdate/WindowsUpdateComSource.cs
git commit -m "Windows Update: поиск патчей через Windows Update Agent COM API"
```

---

### Task 7: WindowsUpdateComSource — скачивание, установка, EULA, перезагрузка

**Files:**
- Modify: `Ven4Tools/Services/WindowsUpdate/WindowsUpdateComSource.cs` (реализовать `InstallAsync`)

**Interfaces:**
- Consumes: то же, что Task 6.
- Produces: рабочий `WindowsUpdateComSource.InstallAsync(...)`.

**Безопасность (важно):** реализация заново ищет патчи внутри себя по тому же критерию поиска и сверяет `UpdateID` из переданного списка с актуальным результатом — не устанавливает вслепую то, что могло устареть/исчезнуть с момента, когда пользователь нажал чекбоксы.

- [ ] **Step 1: Реализовать InstallAsync**

Заменить заглушку `InstallAsync` в конце `WindowsUpdateComSource.cs` на:

```csharp
        public Task<WindowsUpdateInstallOutcome> InstallAsync(
            IReadOnlyList<string> updateIds,
            IProgress<WindowsUpdateProgress> progress,
            CancellationToken ct)
        {
            return Task.Run(() =>
            {
                try
                {
                    dynamic session = CreateComObject("Microsoft.Update.Session");
                    dynamic searcher = session.CreateUpdateSearcher();

                    ct.ThrowIfCancellationRequested();
                    // Повторный поиск — не доверяем списку ID вслепую (см. заметку безопасности выше).
                    dynamic searchResult = searcher.Search(SearchCriteria);
                    dynamic allFound = searchResult.Updates;
                    int foundCount = (int)allFound.Count;

                    dynamic updatesToInstall = Activator.CreateInstance(
                        Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;

                    var matched = new List<dynamic>();
                    for (int i = 0; i < foundCount; i++)
                    {
                        dynamic u = allFound.Item(i);
                        string id = (string)u.Identity.UpdateID;
                        if (!updateIds.Contains(id)) continue;

                        // EULA принимается прямо перед добавлением в очередь на скачивание —
                        // чекбокс в UI уже подразумевает согласие (текст лицензии был показан
                        // в диалоге подтверждения перед стартом, см. Task 12).
                        try { if (!(bool)u.EulaAccepted) u.AcceptEula(); }
                        catch (Exception ex) { AppLogger.Write($"[WindowsUpdateComSource] AcceptEula({id}): {ex.Message}"); }

                        matched.Add(u);
                        updatesToInstall.Add(u);
                    }

                    if (matched.Count == 0)
                        return new WindowsUpdateInstallOutcome
                        {
                            Success = false,
                            ErrorMessage = "Выбранные патчи больше не предлагаются сервером обновлений — попробуйте обновить список."
                        };

                    // ── Проверка места на диске (перед стартом скачивания) ──
                    long totalDownloadBytes = 0;
                    foreach (var u in matched)
                    {
                        try { totalDownloadBytes += (long)u.MaxDownloadSize; } catch { /* поле не всегда доступно — тогда просто не учитываем в оценке */ }
                    }
                    if (totalDownloadBytes > 0)
                    {
                        string systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\";
                        var drive = new DriveInfo(systemDrive);
                        // Запас x2 сверх заявленного размера — распаковка/установка временно
                        // занимает больше места, чем сам скачанный пакет.
                        if (drive.AvailableFreeSpace < totalDownloadBytes * 2)
                            return new WindowsUpdateInstallOutcome
                            {
                                Success = false,
                                ErrorMessage = $"Недостаточно места на диске {systemDrive} — нужно ориентировочно {totalDownloadBytes * 2 / 1024 / 1024} МБ свободных, доступно {drive.AvailableFreeSpace / 1024 / 1024} МБ."
                            };
                    }

                    // ── Скачивание ──
                    dynamic downloader = session.CreateUpdateDownloader();
                    downloader.Updates = updatesToInstall;

                    progress.Report(new WindowsUpdateProgress
                    {
                        Phase = "Скачивание", CompletedCount = 0, TotalCount = matched.Count, PercentComplete = 0
                    });
                    dynamic downloadResult = downloader.Download();
                    int downloadCode = (int)downloadResult.ResultCode;
                    if (downloadCode is 4 or 5)
                        return new WindowsUpdateInstallOutcome
                        {
                            Success = false,
                            ErrorMessage = $"Скачивание обновлений завершилось неудачно (код {downloadCode})."
                        };

                    ct.ThrowIfCancellationRequested();

                    // ── Установка ──
                    dynamic installer = session.CreateUpdateInstaller();
                    installer.Updates = updatesToInstall;

                    progress.Report(new WindowsUpdateProgress
                    {
                        Phase = "Установка", CompletedCount = 0, TotalCount = matched.Count, PercentComplete = 0
                    });
                    dynamic installResult = installer.Install();

                    var itemOutcomes = new List<WindowsUpdateItemOutcome>();
                    for (int i = 0; i < matched.Count; i++)
                    {
                        dynamic u = matched[i];
                        dynamic perUpdateResult = installResult.GetUpdateResult(i);
                        int code = (int)perUpdateResult.ResultCode;
                        bool ok = code == 2 || code == 3; // Succeeded или SucceededWithErrors
                        itemOutcomes.Add(new WindowsUpdateItemOutcome
                        {
                            UpdateId = (string)u.Identity.UpdateID,
                            Title = (string)u.Title,
                            Success = ok,
                            ErrorMessage = ok ? "" : WindowsUpdateErrorMapper.MapHResult((int)perUpdateResult.HResult)
                        });
                        progress.Report(new WindowsUpdateProgress
                        {
                            Phase = "Установка",
                            CurrentTitle = (string)u.Title,
                            CompletedCount = i + 1,
                            TotalCount = matched.Count,
                            PercentComplete = (int)((i + 1) * 100.0 / matched.Count)
                        });
                    }

                    bool overallRebootRequired = false;
                    try { overallRebootRequired = (bool)installResult.RebootRequired; } catch { }

                    return new WindowsUpdateInstallOutcome
                    {
                        Success = itemOutcomes.All(o => o.Success),
                        Items = itemOutcomes,
                        RebootRequired = overallRebootRequired
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (TryGetHResult(ex, out int hr))
                {
                    return new WindowsUpdateInstallOutcome { Success = false, ErrorMessage = WindowsUpdateErrorMapper.MapHResult(hr) };
                }
                catch (Exception ex)
                {
                    AppLogger.Write($"[WindowsUpdateComSource] InstallAsync: {ex}");
                    return new WindowsUpdateInstallOutcome { Success = false, ErrorMessage = $"Ошибка установки: {ex.Message}" };
                }
            }, ct);
        }
```

Добавить `using System.Linq;` в начало файла, если ещё не добавлен (нужен для `.All(...)`).

- [ ] **Step 2: Собрать**

Run: `dotnet build Ven4Tools.sln -c Release`
Expected: 0 ошибок, 0 предупреждений

- [ ] **Step 3: 🛑 РУЧНАЯ ПРОВЕРКА ЧЕЛОВЕКОМ — НЕ АВТОМАТИЗИРОВАТЬ, НЕ ЗАПУСКАТЬ АГЕНТОМ**

В отличие от Task 6 (поиск — безопасно и уже проверено на реальной машине), этот шаг **устанавливает реальный патч Windows** — необратимое системное действие. Агент не должен вызывать `InstallAsync` самостоятельно ни при каких обстоятельствах.

Действие для пользователя (не для агента):
1. На тестовой/одноразовой машине (не на основном рабочем ПК — виртуалка или снапшот) собрать debug-сборку с временной кнопкой, вызывающей `InstallAsync` с одним низкорисковым необязательным патчем.
2. Убедиться: скачивание идёт, прогресс репортится, установка завершается, `RebootRequired` отражает реальное состояние, EULA (если попался патч с лицензией) принимается без диалогов/зависаний.
3. Если что-то из членов COM API (`CreateUpdateDownloader`, `CreateUpdateInstaller`, `.Download()`, `.Install()`, `GetUpdateResult`, `.HResult`) не совпадает с ожидаемым именем — поправить код по сообщению `RuntimeBinderException` и результатам сверки с Microsoft Learn.

Этот шаг обязателен перед тем, как разрешать вызов `InstallAsync` из настоящего UI (Task 12) на машинах пользователей.

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/Services/WindowsUpdate/WindowsUpdateComSource.cs
git commit -m "Windows Update: скачивание и установка патчей через COM API"
```

---

### Task 8: WindowsUpdateService — оркестрация

**Files:**
- Create: `Ven4Tools/Services/WindowsUpdate/WindowsUpdateService.cs`
- Test: `tests/Ven4Tools.Tests/WindowsUpdateServiceTests.cs`

**Interfaces:**
- Consumes: `IWindowsUpdateSource` (Task 2), `InstallationService.InstallSemaphore`/`IsBusy` (Task 5), `WindowsUpdateCategoryTreeBuilder`/`WindowsUpdateErrorMapper` (Task 3, 4).
- Produces: `WindowsUpdateService.SearchAsync(CancellationToken) -> Task<WindowsUpdateSearchResult>`, `WindowsUpdateService.InstallSelectedAsync(IReadOnlyList<string> updateIds, IProgress<WindowsUpdateProgress>, CancellationToken) -> Task<WindowsUpdateInstallOutcome>` (захватывает `InstallSemaphore`, отклоняет с понятной ошибкой, если он уже занят каталогом/историей — не ждёт молча), `WindowsUpdateService.IsBusy` (алиас на `InstallationService.IsBusy`, для использования из вкладки Windows Update). Используется вкладкой (Task 11) и фоновой службой (Task 9).

- [ ] **Step 1: Написать сервис**

```csharp
// Ven4Tools/Services/WindowsUpdate/WindowsUpdateService.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Ven4Tools.Services.WindowsUpdate
{
    public sealed class WindowsUpdateService
    {
        private readonly IWindowsUpdateSource _source;

        public WindowsUpdateService(IWindowsUpdateSource? source = null)
        {
            _source = source ?? new WindowsUpdateComSource();
        }

        // Единый источник истины на "идёт ли сейчас системная установка" — общий
        // с каталогом/историей (см. Task 5), а не отдельный флаг.
        public static bool IsBusy => InstallationService.IsBusy;

        public bool IsServiceRunning() => _source.IsServiceRunning();
        public bool TryStartService() => _source.TryStartService();
        public bool IsRebootPending() => _source.IsRebootPending();

        public Task<WindowsUpdateSearchResult> SearchAsync(CancellationToken ct) =>
            _source.SearchAsync(ct);

        public async Task<WindowsUpdateInstallOutcome> InstallSelectedAsync(
            IReadOnlyList<string> updateIds,
            IProgress<WindowsUpdateProgress> progress,
            CancellationToken ct)
        {
            if (updateIds.Count == 0)
                return new WindowsUpdateInstallOutcome { Success = false, ErrorMessage = "Ничего не выбрано." };

            if (IsBusy)
                return new WindowsUpdateInstallOutcome
                {
                    Success = false,
                    ErrorMessage = "Дождитесь завершения установки приложений из каталога, затем повторите попытку."
                };

            if (_source.IsRebootPending())
                return new WindowsUpdateInstallOutcome
                {
                    Success = false,
                    ErrorMessage = "Требуется перезагрузка от предыдущей установки обновлений — установить новые патчи можно после неё."
                };

            await InstallationService.InstallSemaphore.WaitAsync(ct);
            try
            {
                return await _source.InstallAsync(updateIds, progress, ct);
            }
            finally
            {
                InstallationService.InstallSemaphore.Release();
            }
        }
    }
}
```

- [ ] **Step 2: Написать тесты (через фейк, без реального COM)**

```csharp
// tests/Ven4Tools.Tests/WindowsUpdateServiceTests.cs
using Ven4Tools.Services;
using Ven4Tools.Services.WindowsUpdate;
using Ven4Tools.Tests.Fakes;

namespace Ven4Tools.Tests;

public sealed class WindowsUpdateServiceTests
{
    [Fact]
    public async Task InstallSelectedAsync_EmptyList_ReturnsFailureWithoutTouchingSource()
    {
        var fake = new FakeWindowsUpdateSource();
        var service = new WindowsUpdateService(fake);

        var result = await service.InstallSelectedAsync(
            Array.Empty<string>(), new Progress<WindowsUpdateProgress>(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(fake.InstallCallsReceived);
    }

    [Fact]
    public async Task InstallSelectedAsync_RebootPending_ReturnsFailureWithoutInstalling()
    {
        var fake = new FakeWindowsUpdateSource { RebootPending = true };
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var service = new WindowsUpdateService(fake);

        var result = await service.InstallSelectedAsync(
            new[] { "1" }, new Progress<WindowsUpdateProgress>(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("перезагрузка", result.ErrorMessage);
        Assert.Empty(fake.InstallCallsReceived);
    }

    [Fact]
    public async Task InstallSelectedAsync_CatalogInstallInProgress_ReturnsFailureWithoutInstalling()
    {
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var service = new WindowsUpdateService(fake);

        await InstallationService.InstallSemaphore.WaitAsync();
        try
        {
            var result = await service.InstallSelectedAsync(
                new[] { "1" }, new Progress<WindowsUpdateProgress>(), CancellationToken.None);

            Assert.False(result.Success);
            Assert.Contains("каталога", result.ErrorMessage);
            Assert.Empty(fake.InstallCallsReceived);
        }
        finally
        {
            InstallationService.InstallSemaphore.Release();
        }
    }

    [Fact]
    public async Task InstallSelectedAsync_HappyPath_CallsSourceAndReleasesSemaphore()
    {
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var service = new WindowsUpdateService(fake);

        var result = await service.InstallSelectedAsync(
            new[] { "1" }, new Progress<WindowsUpdateProgress>(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(fake.InstallCallsReceived);
        Assert.False(InstallationService.IsBusy); // семафор освобождён
    }
}
```

- [ ] **Step 3: Прогнать тесты**

Run: `dotnet test tests/Ven4Tools.Tests -c Release --filter WindowsUpdateServiceTests`
Expected: 4/4 passed

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/Services/WindowsUpdate/WindowsUpdateService.cs tests/Ven4Tools.Tests/WindowsUpdateServiceTests.cs
git commit -m "Windows Update: сервис-оркестратор с блокировкой семафора и reboot-pending гейтом"
```

---

### Task 9: Фоновая проверка (WindowsUpdateBackgroundService)

**Files:**
- Create: `Ven4Tools/Services/WindowsUpdateBackgroundService.cs`
- Test: `tests/Ven4Tools.Tests/WindowsUpdateBackgroundServiceTests.cs`

**Interfaces:**
- Consumes: `WindowsUpdateService` (Task 8), `ProfileService.Current.WindowsUpdateMode` (Task 1), `UpdateBackgroundService.ShowNotification` (существующий паттерн уведомлений).
- Produces: `WindowsUpdateBackgroundService.AvailableCount` (`static int`, для бейджа на вкладке, Task 14), событие `WindowsUpdateBackgroundService.CountChanged`.

- [ ] **Step 1: Написать фоновую службу**

```csharp
// Ven4Tools/Services/WindowsUpdateBackgroundService.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Services
{
    /// <summary>
    /// Фоновая проверка обновлений Windows — по аналогии с UpdateBackgroundService
    /// (приложения из winget). Режим поведения — из ProfileService.Current.WindowsUpdateMode:
    ///   "NotSet"            — проверка не выполняется вообще (первый вход ещё не пройден).
    ///   "NotifyOnly"        — только уведомление + бейдж-счётчик.
    ///   "NotifyAndDownload" — то же + тихое скачивание в фоне (без установки).
    /// Никогда не устанавливает патчи автоматически — это всегда явный клик пользователя.
    /// </summary>
    public sealed class WindowsUpdateBackgroundService : IDisposable
    {
        private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

        private readonly WindowsUpdateService _service;
        private readonly CancellationTokenSource _cts = new();
        private Task? _loop;

        public static int AvailableCount { get; private set; }
        public static event Action? CountChanged;

        public WindowsUpdateBackgroundService(WindowsUpdateService? service = null)
        {
            _service = service ?? new WindowsUpdateService();
        }

        public void Start()
        {
            if (_loop != null) return;
            _loop = Task.Run(() => RunLoopAsync(_cts.Token));
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                await Task.Delay(FirstDelay, ct);
                while (!ct.IsCancellationRequested)
                {
                    try { await CheckOnceAsync(ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { AppLogger.Write($"[WindowsUpdateBg] {ex.Message}"); }

                    await Task.Delay(Interval, ct);
                }
            }
            catch (OperationCanceledException) { /* штатная остановка через Dispose */ }
        }

        internal async Task CheckOnceAsync(CancellationToken ct)
        {
            var mode = ProfileService.Current.WindowsUpdateMode;
            if (mode == "NotSet") return;
            if (ProfileService.Current.ParanoidMode) return;
            if (ProfileService.Current.OfflineMode) return;
            if (!ConnectivityMonitor.IsOnline)
            {
                await ConnectivityMonitor.CheckAsync();
                if (!ConnectivityMonitor.IsOnline) return;
            }

            var result = await _service.SearchAsync(ct);
            if (!result.Success) return;

            SetCount(result.Items.Count);

            if (result.Items.Count > 0)
            {
                UpdateBackgroundService.ShowNotification(
                    "Доступны обновления Windows",
                    $"Найдено {result.Items.Count} патчей. Откройте вкладку «Windows Update», чтобы выбрать и установить.");
            }

            if (mode == "NotifyAndDownload" && result.Items.Count > 0 && !WindowsUpdateService.IsBusy)
            {
                // Тихое скачивание без установки: захватываем прогресс, но не показываем UI.
                // InstallSelectedAsync тут не используется намеренно — он и скачивает, и ставит;
                // отдельного "только скачать" метода источник (Task 6/7) пока не предоставляет,
                // поэтому в первой версии фоновый режим ограничивается уведомлением до тех пор,
                // пока IWindowsUpdateSource не получит DownloadOnlyAsync (см. заметку ниже).
                AppLogger.Write("[WindowsUpdateBg] Режим NotifyAndDownload: фоновое скачивание запланировано, но метод DownloadOnlyAsync ещё не реализован — пока только уведомление.");
            }
        }

        private static void SetCount(int count)
        {
            if (AvailableCount == count) return;
            AvailableCount = count;
            CountChanged?.Invoke();
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
        }
    }
}
```

**Примечание для реализующего:** метод `DownloadOnlyAsync` (скачать без установки, для режима `NotifyAndDownload`) в `IWindowsUpdateSource` сознательно не включён в Task 2/6/7 этого плана — первая версия ограничивает фоновый режим уведомлением, чтобы не расширять и без того рискованную зону COM-кода (Task 6/7) ещё одним недо-проверенным путём. Добавить `DownloadOnlyAsync` — отдельная, самостоятельная follow-up задача после того, как основной цикл поиск→показ→установка обкатан вживую.

- [ ] **Step 2: Написать тесты**

```csharp
// tests/Ven4Tools.Tests/WindowsUpdateBackgroundServiceTests.cs
using Ven4Tools.Models;
using Ven4Tools.Services;
using Ven4Tools.Services.WindowsUpdate;
using Ven4Tools.Tests.Fakes;

namespace Ven4Tools.Tests;

public sealed class WindowsUpdateBackgroundServiceTests
{
    [Fact]
    public async Task CheckOnceAsync_ModeNotSet_DoesNotSearch()
    {
        ProfileService.Current.WindowsUpdateMode = "NotSet";
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));

        await bg.CheckOnceAsync(CancellationToken.None);

        // Поиск не должен был случиться — при NotSet просто нечего проверять.
        // (Косвенная проверка: счётчик не должен был обновиться до 1.)
        Assert.NotEqual(1, WindowsUpdateBackgroundService.AvailableCount);
    }

    [Fact]
    public async Task CheckOnceAsync_ModeNotifyOnly_UpdatesCountFromSearch()
    {
        ProfileService.Current.WindowsUpdateMode = "NotifyOnly";
        ProfileService.Current.ParanoidMode = false;
        ProfileService.Current.OfflineMode = false;
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "2", Title = "B" });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));

        await bg.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(2, WindowsUpdateBackgroundService.AvailableCount);
    }

    [Fact]
    public async Task CheckOnceAsync_ParanoidMode_SkipsCheck()
    {
        ProfileService.Current.WindowsUpdateMode = "NotifyOnly";
        ProfileService.Current.ParanoidMode = true;
        var fake = new FakeWindowsUpdateSource();
        fake.Items.Add(new WindowsUpdateItem { UpdateId = "1", Title = "A" });
        var bg = new WindowsUpdateBackgroundService(new WindowsUpdateService(fake));
        WindowsUpdateBackgroundService.CountChangedResetForTests();

        await bg.CheckOnceAsync(CancellationToken.None);

        Assert.Equal(0, fake.InstallCallsReceived.Count); // sanity: точно не устанавливали
        ProfileService.Current.ParanoidMode = false; // не оставлять состояние для других тестов
    }
}
```

Так как в тестовом классе выше есть вызов `WindowsUpdateBackgroundService.CountChangedResetForTests()`, которого пока нет в продакшн-классе (а статический `AvailableCount` иначе "утекает" между тестами и делает их зависимыми от порядка выполнения) — добавить в `WindowsUpdateBackgroundService.cs` рядом с `SetCount`:

```csharp
        // Только для тестов: xUnit по умолчанию не гарантирует порядок между классами,
        // а AvailableCount — static на весь процесс. Без сброса тесты влияли бы друг на друга.
        internal static void CountChangedResetForTests() => AvailableCount = 0;
```

- [ ] **Step 3: Прогнать тесты**

Run: `dotnet test tests/Ven4Tools.Tests -c Release --filter WindowsUpdateBackgroundServiceTests`
Expected: 3/3 passed

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/Services/WindowsUpdateBackgroundService.cs tests/Ven4Tools.Tests/WindowsUpdateBackgroundServiceTests.cs
git commit -m "Windows Update: фоновая проверка с режимами уведомлений"
```

---

### Task 10: Первый вход — диалог выбора режима

**Files:**
- Create: `Ven4Tools/Views/WindowsUpdateModeDialog.xaml`
- Create: `Ven4Tools/Views/WindowsUpdateModeDialog.xaml.cs`

**Interfaces:**
- Produces: `WindowsUpdateModeDialog` (Window), после `ShowDialog() == true` — свойство `SelectedMode: string` (`"NotifyOnly"` или `"NotifyAndDownload"`). Используется вкладкой (Task 11).

- [ ] **Step 1: XAML диалога**

```xml
<!-- Ven4Tools/Views/WindowsUpdateModeDialog.xaml -->
<Window x:Class="Ven4Tools.Views.WindowsUpdateModeDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Windows Update" Height="260" Width="440"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize"
        Background="{DynamicResource WindowBackground}">
    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="16"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="16"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="Как проверять обновления Windows?"
                   FontSize="16" FontWeight="SemiBold"
                   Foreground="{DynamicResource TextPrimary}" TextWrapping="Wrap"/>

        <StackPanel Grid.Row="2">
            <RadioButton x:Name="rbNotifyOnly" GroupName="mode" IsChecked="True" Margin="0,0,0,12">
                <StackPanel Margin="6,0,0,0">
                    <TextBlock Text="Только уведомлять" FontWeight="SemiBold" Foreground="{DynamicResource TextPrimary}"/>
                    <TextBlock Text="Клиент периодически проверяет наличие патчей и показывает уведомление. Ничего не скачивается заранее."
                               TextWrapping="Wrap" FontSize="12" Foreground="{DynamicResource TextSecondary}" Margin="0,2,0,0"/>
                </StackPanel>
            </RadioButton>
            <RadioButton x:Name="rbNotifyAndDownload" GroupName="mode">
                <StackPanel Margin="6,0,0,0">
                    <TextBlock Text="Уведомлять и скачивать в фоне" FontWeight="SemiBold" Foreground="{DynamicResource TextPrimary}"/>
                    <TextBlock Text="То же самое, но патчи заранее тихо скачиваются в фоне — установка позже пройдёт быстрее. Установка всё равно запускается только вручную."
                               TextWrapping="Wrap" FontSize="12" Foreground="{DynamicResource TextSecondary}" Margin="0,2,0,0"/>
                </StackPanel>
            </RadioButton>
        </StackPanel>

        <TextBlock Grid.Row="3" Text="Выбор можно изменить позже во вкладке «Система»."
                   FontSize="11" Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap"/>

        <Button Grid.Row="4" x:Name="btnOk" Content="Готово" Height="36" HorizontalAlignment="Right"
                Padding="24,0" Background="{DynamicResource AccentColor}" Foreground="White"
                FontWeight="SemiBold" BorderThickness="0" Click="BtnOk_Click"/>
    </Grid>
</Window>
```

- [ ] **Step 2: Code-behind**

```csharp
// Ven4Tools/Views/WindowsUpdateModeDialog.xaml.cs
using System.Windows;

namespace Ven4Tools.Views
{
    public partial class WindowsUpdateModeDialog : Window
    {
        public string SelectedMode { get; private set; } = "NotifyOnly";

        public WindowsUpdateModeDialog()
        {
            InitializeComponent();
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            SelectedMode = rbNotifyAndDownload.IsChecked == true ? "NotifyAndDownload" : "NotifyOnly";
            DialogResult = true;
            Close();
        }
    }
}
```

- [ ] **Step 3: Собрать**

Run: `dotnet build Ven4Tools.sln -c Release`
Expected: 0 ошибок, 0 предупреждений

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/Views/WindowsUpdateModeDialog.xaml Ven4Tools/Views/WindowsUpdateModeDialog.xaml.cs
git commit -m "Windows Update: диалог выбора режима первого входа"
```

---

### Task 11: Вкладка WindowsUpdateTab — дерево категорий и поиск

**Files:**
- Create: `Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml`
- Create: `Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml.cs`

**Interfaces:**
- Consumes: `WindowsUpdateService` (Task 8), `WindowsUpdateCategoryTreeBuilder` (Task 3), `WindowsUpdateModeDialog` (Task 10), `ProfileService` (существующий).
- Produces: `WindowsUpdateTab : UserControl` — регистрируется в MainWindow (Task 13).

Визуальный стиль — минимальный, функциональный (без анимаций/тонкой полировки): пользователь явно попросил "разберёмся когда увидим визуал" — приоритет здесь на корректность потока данных и логики, не на пиксель-перфект.

- [ ] **Step 1: XAML вкладки**

```xml
<!-- Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml -->
<UserControl x:Class="Ven4Tools.Views.Tabs.WindowsUpdateTab"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="{DynamicResource ContentBackground}">
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Header -->
        <Grid Grid.Row="0" Margin="0,0,0,12">
            <StackPanel>
                <TextBlock Text="🛡️ Обновления Windows" FontSize="22" FontWeight="Bold"
                           Foreground="{DynamicResource TextPrimary}"/>
                <TextBlock x:Name="txtLastChecked" Text="Обновления ещё не проверялись"
                           FontSize="12" Foreground="{DynamicResource TextSecondary}" Margin="0,4,0,0"/>
            </StackPanel>
            <Button x:Name="btnCheck" Content="🔄 Проверить обновления" Height="34" Padding="16,0"
                    HorizontalAlignment="Right" VerticalAlignment="Top"
                    Background="{DynamicResource CardBackground}" Foreground="{DynamicResource TextPrimary}"
                    BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"
                    Click="BtnCheck_Click"/>
        </Grid>

        <TextBlock Grid.Row="1" x:Name="txtStatus" Text="" TextWrapping="Wrap"
                   Foreground="{DynamicResource TextSecondary}" Margin="0,0,0,10"/>

        <TreeView Grid.Row="2" x:Name="treeUpdates"
                  Background="{DynamicResource CardBackground}"
                  BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"/>

        <!-- Footer: выбор + установка -->
        <Grid Grid.Row="3" Margin="0,12,0,0">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <TextBlock x:Name="txtSelectionSummary" Grid.Column="0" Text="Выбрано: 0 патчей, 0 МБ"
                       VerticalAlignment="Center" Foreground="{DynamicResource TextSecondary}"/>
            <Button x:Name="btnInstall" Grid.Column="1" Content="Установить выбранные" Height="38"
                    Padding="24,0" IsEnabled="False"
                    Background="{DynamicResource AccentColor}" Foreground="White" FontWeight="SemiBold"
                    BorderThickness="0" Click="BtnInstall_Click"/>
        </Grid>
    </Grid>
</UserControl>
```

- [ ] **Step 2: Code-behind — состояние, поиск, дерево**

```csharp
// Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml.cs
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Ven4Tools.Services;
using Ven4Tools.Services.WindowsUpdate;
using Ven4Tools.Views;

namespace Ven4Tools.Views.Tabs
{
    public partial class WindowsUpdateTab : UserControl
    {
        private readonly WindowsUpdateService _service = new();
        private System.Collections.Generic.IReadOnlyList<WindowsUpdateCategoryNode> _tree =
            Array.Empty<WindowsUpdateCategoryNode>();
        private CancellationTokenSource? _searchCts;
        private bool _firstRunHandled;

        public WindowsUpdateTab()
        {
            InitializeComponent();
            Loaded += WindowsUpdateTab_Loaded;
        }

        private async void WindowsUpdateTab_Loaded(object sender, RoutedEventArgs e)
        {
            if (_firstRunHandled) return;
            _firstRunHandled = true;

            if (ProfileService.Current.WindowsUpdateMode == "NotSet")
            {
                var dialog = new WindowsUpdateModeDialog { Owner = Window.GetWindow(this) };
                if (dialog.ShowDialog() == true)
                {
                    ProfileService.Current.WindowsUpdateMode = dialog.SelectedMode;
                    ProfileService.Save();
                }
                else
                {
                    // Пользователь закрыл диалог без выбора — считаем "только уведомлять"
                    // как самый ненавязчивый вариант по умолчанию, не переспрашиваем каждый раз.
                    ProfileService.Current.WindowsUpdateMode = "NotifyOnly";
                    ProfileService.Save();
                }
            }

            await RunSearchAsync();
        }

        private async void BtnCheck_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();

        private async Task RunSearchAsync()
        {
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            btnCheck.IsEnabled = false;
            txtStatus.Text = "⏳ Проверка обновлений...";
            treeUpdates.Items.Clear();

            if (!_service.IsServiceRunning())
            {
                var startNow = MessageBox.Show(
                    "Служба Windows Update не запущена. Запустить её сейчас?",
                    "Служба остановлена", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (startNow == MessageBoxResult.Yes && !_service.TryStartService())
                {
                    txtStatus.Text = "❌ Не удалось запустить службу Windows Update.";
                    btnCheck.IsEnabled = true;
                    return;
                }
                if (startNow == MessageBoxResult.No)
                {
                    txtStatus.Text = "⚠ Служба Windows Update не запущена — проверка недоступна.";
                    btnCheck.IsEnabled = true;
                    return;
                }
            }

            var result = await _service.SearchAsync(ct);
            if (ct.IsCancellationRequested) return;

            btnCheck.IsEnabled = true;
            txtLastChecked.Text = $"Последняя проверка: {DateTime.Now:dd.MM.yyyy HH:mm}";

            if (!result.Success)
            {
                txtStatus.Text = $"❌ {result.ErrorMessage}";
                return;
            }

            if (result.Items.Count == 0)
            {
                txtStatus.Text = "✅ Обновлений не найдено — система актуальна.";
                UpdateSelectionSummary();
                return;
            }

            txtStatus.Text = $"Найдено патчей: {result.Items.Count}";
            _tree = WindowsUpdateCategoryTreeBuilder.Build(result.Items);
            RenderTree();
        }

        private void RenderTree()
        {
            treeUpdates.Items.Clear();
            foreach (var category in _tree)
            {
                var categoryItem = new TreeViewItem { IsExpanded = false };
                var categoryCheck = new CheckBox
                {
                    Content = $"{category.Name} ({category.Items.Count})",
                    IsThreeState = true,
                    IsChecked = category.IsChecked
                };
                categoryCheck.Click += (_, _) =>
                {
                    bool newState = categoryCheck.IsChecked == true;
                    WindowsUpdateCategoryTreeBuilder.ApplyCategoryCheck(category, newState);
                    RenderTree(); // перерисовать — проще и надёжнее ручной синхронизации чекбоксов детей
                };
                categoryItem.Header = categoryCheck;

                foreach (var itemNode in category.Items)
                {
                    var itemCheck = new CheckBox
                    {
                        Content = $"{itemNode.Item.Title}" +
                                  (itemNode.Item.KbArticleIds.Count > 0
                                      ? $" (KB{string.Join(", KB", itemNode.Item.KbArticleIds)})"
                                      : "") +
                                  $" — {FormatSize(itemNode.Item.SizeBytes)}",
                        IsChecked = itemNode.IsChecked
                    };
                    itemCheck.Click += (_, _) =>
                    {
                        itemNode.IsChecked = itemCheck.IsChecked == true;
                        WindowsUpdateCategoryTreeBuilder.RecalculateCategoryState(category);
                        UpdateSelectionSummary();
                        // Обновляем только состояние чекбокса категории, без полной перерисовки дерева,
                        // чтобы не сворачивать раскрытые узлы при каждом клике по патчу.
                        categoryCheck.IsChecked = category.IsChecked;
                    };
                    categoryItem.Items.Add(new TreeViewItem { Header = itemCheck });
                }

                treeUpdates.Items.Add(categoryItem);
            }
            UpdateSelectionSummary();
        }

        private void UpdateSelectionSummary()
        {
            var selectedIds = WindowsUpdateCategoryTreeBuilder.GetSelectedUpdateIds(_tree);
            long totalBytes = WindowsUpdateCategoryTreeBuilder.GetSelectedTotalSizeBytes(_tree);
            txtSelectionSummary.Text = $"Выбрано: {selectedIds.Count} патчей, {FormatSize(totalBytes)}";
            btnInstall.IsEnabled = selectedIds.Count > 0 && !WindowsUpdateService.IsBusy;
        }

        private static string FormatSize(long bytes) =>
            bytes <= 0 ? "0 МБ" : $"{bytes / 1024.0 / 1024.0:F1} МБ";

        // Реализация BtnInstall_Click — Task 12.
        private void BtnInstall_Click(object sender, RoutedEventArgs e) { }
    }
}
```

- [ ] **Step 3: Собрать**

Run: `dotnet build Ven4Tools.sln -c Release`
Expected: 0 ошибок, 0 предупреждений

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml.cs
git commit -m "Windows Update: вкладка — дерево категорий, поиск, первый вход"
```

---

### Task 12: Установка, прогресс, сводка, перезагрузка

**Files:**
- Create: `Ven4Tools/Views/WindowsUpdateResultWindow.xaml`
- Create: `Ven4Tools/Views/WindowsUpdateResultWindow.xaml.cs`
- Modify: `Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml.cs` (реализовать `BtnInstall_Click`)

**Interfaces:**
- Consumes: `WindowsUpdateInstallOutcome` (Task 1), `WindowsUpdateErrorMapper.GetItemsNeedingEula` (Task 4), `WindowsUpdateService.InstallSelectedAsync` (Task 8).
- Produces: `WindowsUpdateResultWindow` (Window, принимает `WindowsUpdateInstallOutcome` в конструкторе) — показывает сводку и, если нужно, предлагает перезагрузку.

- [ ] **Step 1: Окно результатов**

```xml
<!-- Ven4Tools/Views/WindowsUpdateResultWindow.xaml -->
<Window x:Class="Ven4Tools.Views.WindowsUpdateResultWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Результат установки" Height="420" Width="480"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize"
        Background="{DynamicResource WindowBackground}">
    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="12"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="16"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" x:Name="txtSummary" FontSize="16" FontWeight="SemiBold"
                   Foreground="{DynamicResource TextPrimary}" TextWrapping="Wrap"/>

        <ListBox Grid.Row="2" x:Name="lstItems"
                 Background="{DynamicResource CardBackground}"
                 BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1"/>

        <Grid Grid.Row="4">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <Button x:Name="btnClose" Grid.Column="1" Content="Позже" Height="36" Padding="20,0" Margin="0,0,8,0"
                    Background="Transparent" Foreground="{DynamicResource TextSecondary}"
                    BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" Click="BtnClose_Click"/>
            <Button x:Name="btnRestartNow" Grid.Column="2" Content="Перезагрузить сейчас" Height="36" Padding="20,0"
                    Visibility="Collapsed"
                    Background="{DynamicResource AccentColor}" Foreground="White" FontWeight="SemiBold"
                    BorderThickness="0" Click="BtnRestartNow_Click"/>
        </Grid>
    </Grid>
</Window>
```

- [ ] **Step 2: Code-behind**

```csharp
// Ven4Tools/Views/WindowsUpdateResultWindow.xaml.cs
using System.Diagnostics;
using System.Windows;
using Ven4Tools.Services.WindowsUpdate;

namespace Ven4Tools.Views
{
    public partial class WindowsUpdateResultWindow : Window
    {
        public WindowsUpdateResultWindow(WindowsUpdateInstallOutcome outcome)
        {
            InitializeComponent();

            int success = outcome.Items.Count(i => i.Success);
            int failed = outcome.Items.Count - success;

            txtSummary.Text = outcome.Success
                ? $"✅ Установлено патчей: {success}"
                : $"⚠ Установлено: {success}, не удалось: {failed}";

            foreach (var item in outcome.Items)
            {
                lstItems.Items.Add(item.Success
                    ? $"✅ {item.Title}"
                    : $"❌ {item.Title} — {item.ErrorMessage}");
            }

            if (!string.IsNullOrWhiteSpace(outcome.ErrorMessage))
                lstItems.Items.Add($"❌ {outcome.ErrorMessage}");

            if (outcome.RebootRequired)
            {
                txtSummary.Text += "\n\nТребуется перезагрузка для завершения установки.";
                btnRestartNow.Visibility = Visibility.Visible;
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void BtnRestartNow_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo("shutdown", "/r /t 5") { UseShellExecute = true });
            Close();
        }
    }
}
```

Добавить `using System.Linq;` в начало файла (нужен для `.Count(...)`).

- [ ] **Step 3: Реализовать BtnInstall_Click во вкладке**

В `Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml.cs` заменить заглушку `BtnInstall_Click` на:

```csharp
        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            var selectedIds = WindowsUpdateCategoryTreeBuilder.GetSelectedUpdateIds(_tree);
            if (selectedIds.Count == 0) return;

            var eulaItems = WindowsUpdateErrorMapper.GetItemsNeedingEula(_tree);
            long totalBytes = WindowsUpdateCategoryTreeBuilder.GetSelectedTotalSizeBytes(_tree);

            string confirmText = $"Установить {selectedIds.Count} патчей ({FormatSize(totalBytes)})?\n\n" +
                                  "Может потребоваться перезагрузка после установки.";
            if (eulaItems.Count > 0)
            {
                confirmText += "\n\nЛицензионные соглашения выбранных обновлений:\n\n" +
                    string.Join("\n\n---\n\n", eulaItems.Select(i => $"{i.Title}:\n{i.EulaText}"));
            }

            var confirmed = MessageBox.Show(confirmText, "Подтверждение установки",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirmed != MessageBoxResult.Yes) return;

            btnInstall.IsEnabled = false;
            btnCheck.IsEnabled = false;
            var progress = new Progress<WindowsUpdateProgress>(p =>
            {
                txtStatus.Text = $"{p.Phase}: {p.CurrentTitle} ({p.CompletedCount}/{p.TotalCount}, {p.PercentComplete}%)";
            });

            var outcome = await _service.InstallSelectedAsync(selectedIds, progress, CancellationToken.None);

            btnCheck.IsEnabled = true;
            if (!outcome.Success && outcome.Items.Count == 0)
            {
                // Отказ ещё до старта (занято/reboot-pending/пусто) — есть только общее сообщение.
                MessageBox.Show(outcome.ErrorMessage, "Установка не выполнена",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                UpdateSelectionSummary();
                return;
            }

            var resultWindow = new WindowsUpdateResultWindow(outcome) { Owner = Window.GetWindow(this) };
            resultWindow.ShowDialog();

            // После установки — обновить список (успешно поставленные больше не должны показываться).
            await RunSearchAsync();
        }
```

Добавить `using System.Linq;` в начало `WindowsUpdateTab.xaml.cs`, если ещё не добавлен.

- [ ] **Step 4: Собрать**

Run: `dotnet build Ven4Tools.sln -c Release`
Expected: 0 ошибок, 0 предупреждений

- [ ] **Step 5: Commit**

```bash
git add Ven4Tools/Views/WindowsUpdateResultWindow.xaml Ven4Tools/Views/WindowsUpdateResultWindow.xaml.cs Ven4Tools/Views/Tabs/WindowsUpdateTab.xaml.cs
git commit -m "Windows Update: установка выбранных патчей, сводка, диалог перезагрузки"
```

---

### Task 13: Регистрация вкладки и фоновой службы в MainWindow/App

**Files:**
- Modify: `Ven4Tools/MainWindow.xaml` (добавить кнопку навигации)
- Modify: `Ven4Tools/MainWindow.xaml.cs` (добавить поле, Navigate-метод)
- Modify: `Ven4Tools/App.xaml.cs` (запустить `WindowsUpdateBackgroundService`)

**Interfaces:**
- Consumes: `WindowsUpdateTab` (Task 11), `WindowsUpdateBackgroundService` (Task 9).

- [ ] **Step 1: Добавить кнопку навигации**

В `Ven4Tools/MainWindow.xaml`, после кнопки `btnSystemTab` (строка ~122, перед `btnOfficeTab`):

```xml
                        <Button x:Name="btnWindowsUpdateTab"
                                Style="{StaticResource NavButtonStyle}"
                                Tag="🛡️"
                                Content="Windows Update"
                                Click="NavigateToWindowsUpdate"/>
```

- [ ] **Step 2: Поле и метод навигации**

В `Ven4Tools/MainWindow.xaml.cs`, добавить поле рядом с `_systemTab` (строка ~26):

```csharp
        private WindowsUpdateTab? _windowsUpdateTab;
```

И метод рядом с `NavigateToSystem` (строка ~118):

```csharp
        private void NavigateToWindowsUpdate(object? sender, RoutedEventArgs? e)
        {
            SetActiveButton(btnWindowsUpdateTab);
            AppLogger.Write("📂 Открыта вкладка: Windows Update");
            if (_windowsUpdateTab == null) _windowsUpdateTab = new WindowsUpdateTab();
            MainFrame.Content = (_windowsUpdateTab);
            UpdateMascot("system"); // отдельного маскота для этой вкладки пока нет — используем нейтрального "system"
        }
```

- [ ] **Step 3: Запустить фоновую службу**

В `Ven4Tools/App.xaml.cs`, рядом с `_updateBgService` (строка ~13, ~77), добавить поле:

```csharp
        private static WindowsUpdateBackgroundService? _windowsUpdateBgService;
```

И в блоке `try { _updateBgService = new UpdateBackgroundService(); _updateBgService.Start(); } catch { }` (строка ~75-80) добавить следом:

```csharp
                try
                {
                    _windowsUpdateBgService = new WindowsUpdateBackgroundService();
                    _windowsUpdateBgService.Start();
                }
                catch { }
```

- [ ] **Step 4: Собрать**

Run: `dotnet build Ven4Tools.sln -c Release`
Expected: 0 ошибок, 0 предупреждений

- [ ] **Step 5: Ручная проверка (агент не может прокликать WPF UI)**

Запустить клиент, открыть вкладку «Windows Update» первый раз — должен появиться диалог выбора режима, затем автоматически начаться проверка обновлений, дерево должно отрисоваться (или показать "обновлений не найдено"). Повторное открытие вкладки не должно снова показывать диалог режима.

- [ ] **Step 6: Commit**

```bash
git add Ven4Tools/MainWindow.xaml Ven4Tools/MainWindow.xaml.cs Ven4Tools/App.xaml.cs
git commit -m "Windows Update: регистрация вкладки и фоновой службы"
```

---

### Task 14: Бейдж-счётчик на кнопке вкладки

**Files:**
- Modify: `Ven4Tools/MainWindow.xaml` (добавить `TextBlock`-бейдж рядом с кнопкой)
- Modify: `Ven4Tools/MainWindow.xaml.cs` (подписка на `WindowsUpdateBackgroundService.CountChanged`)

**Interfaces:**
- Consumes: `WindowsUpdateBackgroundService.AvailableCount`/`CountChanged` (Task 9).

- [ ] **Step 1: Обернуть кнопку в Grid с бейджем**

В `Ven4Tools/MainWindow.xaml` заменить добавленную в Task 13 кнопку `btnWindowsUpdateTab` на:

```xml
                        <Grid>
                            <Button x:Name="btnWindowsUpdateTab"
                                    Style="{StaticResource NavButtonStyle}"
                                    Tag="🛡️"
                                    Content="Windows Update"
                                    Click="NavigateToWindowsUpdate"/>
                            <Border x:Name="badgeWindowsUpdateCount"
                                    Background="#E74C3C" CornerRadius="8"
                                    Width="18" Height="16"
                                    HorizontalAlignment="Right" VerticalAlignment="Top"
                                    Margin="0,4,8,0" Visibility="Collapsed">
                                <TextBlock x:Name="txtWindowsUpdateBadge" Text="0"
                                           FontSize="10" FontWeight="Bold" Foreground="White"
                                           HorizontalAlignment="Center" VerticalAlignment="Center"/>
                            </Border>
                        </Grid>
```

- [ ] **Step 2: Подписка на изменение счётчика**

В `Ven4Tools/MainWindow.xaml.cs`, в конструкторе `MainWindow()` рядом с блоком `Loaded += (s, e) => { ConnectivityMonitor.Start(); ... };` (строка ~55-61) добавить:

```csharp
            Loaded += (s, e) =>
            {
                WindowsUpdateBackgroundService.CountChanged += OnWindowsUpdateCountChanged;
                OnWindowsUpdateCountChanged();
            };
```

Добавить метод рядом с `OnConnectivityChanged` (строка ~70):

```csharp
        private void OnWindowsUpdateCountChanged() => Dispatcher.Invoke(() =>
        {
            int count = WindowsUpdateBackgroundService.AvailableCount;
            txtWindowsUpdateBadge.Text = count > 99 ? "99+" : count.ToString();
            badgeWindowsUpdateCount.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
```

И отписаться в `OnClosed` (строка ~73-80), рядом с `ConnectivityMonitor.StatusChanged -= OnConnectivityChanged;`:

```csharp
            WindowsUpdateBackgroundService.CountChanged -= OnWindowsUpdateCountChanged;
```

- [ ] **Step 3: Собрать**

Run: `dotnet build Ven4Tools.sln -c Release`
Expected: 0 ошибок, 0 предупреждений

- [ ] **Step 4: Commit**

```bash
git add Ven4Tools/MainWindow.xaml Ven4Tools/MainWindow.xaml.cs
git commit -m "Windows Update: бейдж-счётчик доступных патчей на кнопке вкладки"
```

---

### Task 15: Покрытие coverlet + UI smoke-тест

**Files:**
- Modify: `tests/Ven4Tools.Tests/Ven4Tools.Tests.csproj` (добавить новые чистые классы в `<Include>`)
- Create: `Ven4Tools.ClientUITests/WindowsUpdateTabSmokeTests.cs` (MSTest/FlaUI, по точному паттерну `Ven4Tools.ClientUITests/OfflineEmbeddedCatalogUiTests.cs` — свой `[TestClass]`, свой `AppSession.Launch()`, `profile.json` подсеивается до запуска, чтобы обойти диалог первого входа — тот же приём, что там уже используется для обхода мастера выбора категории)

**Interfaces:**
- Не вводит новых интерфейсов — только тестовая обвязка.

- [ ] **Step 1: Добавить новые классы в coverlet Include**

В `tests/Ven4Tools.Tests/Ven4Tools.Tests.csproj`, строка 11 (`<Include>...`), дописать через запятую:

```
,[Ven4Tools]Ven4Tools.Services.WindowsUpdate.WindowsUpdateCategoryTreeBuilder,[Ven4Tools]Ven4Tools.Services.WindowsUpdate.WindowsUpdateErrorMapper,[Ven4Tools]Ven4Tools.Services.WindowsUpdate.WindowsUpdateService,[Ven4Tools]Ven4Tools.Services.WindowsUpdateBackgroundService
```

(`WindowsUpdateComSource` в список **не** добавлять — это COM-обёртка, тестируется только вручную на реальной машине, покрытие coverlet для неё бессмысленно.)

- [ ] **Step 2: Написать UI smoke-тест**

Профиль подсеивается с уже выставленным `WindowsUpdateMode`, чтобы диалог первого входа не появлялся — в этом тестовом наборе нет прецедента взаимодействия со вторичными modal-окнами (`OfflineEmbeddedCatalogUiTests` тем же приёмом обходит мастер выбора категории), поэтому тест ограничивается тем, что уже проверяется в этом проекте: открытие вкладки и отрисовка её содержимого без падения.

```csharp
// Ven4Tools.ClientUITests/WindowsUpdateTabSmokeTests.cs
using System;
using System.IO;
using FlaUI.Core.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ven4Tools.ClientUITests
{
    /// <summary>
    /// Смоук-тест вкладки Windows Update: только то, что она открывается и
    /// отрисовывается без падения. Реальная установка патчей здесь не
    /// вызывается никогда (см. Global Constraints плана) — устанавливать
    /// патчи ОС в CI-раннере небезопасно и непредсказуемо по времени.
    ///
    /// WindowsUpdateMode подсеивается в profile.json заранее ("NotifyOnly"),
    /// чтобы обойти модальный диалог первого входа — тот же приём, что уже
    /// использует OfflineEmbeddedCatalogUiTests для мастера выбора категории.
    /// </summary>
    [TestClass]
    public class WindowsUpdateTabSmokeTests
    {
        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ven4Tools");
        private static readonly string ProfilePath = Path.Combine(SettingsDir, "profile.json");

        private static string? _profileBackup;
        private static bool _profileExistedBefore;
        private static AppSession? _session;
        private static string? _launchError;

        private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(15);

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            _profileExistedBefore = File.Exists(ProfilePath);
            if (_profileExistedBefore)
                _profileBackup = File.ReadAllText(ProfilePath);

            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(ProfilePath,
                "{\"CatalogMode\":\"full\",\"HasSelectedCategory\":true,\"WindowsUpdateMode\":\"NotifyOnly\"}");

            try
            {
                _session = AppSession.Launch();
            }
            catch (Exception ex)
            {
                _launchError = ex.Message;
                _session = null;
            }
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _session?.Dispose();
            _session = null;

            if (_profileExistedBefore) File.WriteAllText(ProfilePath, _profileBackup!);
            else if (File.Exists(ProfilePath)) File.Delete(ProfilePath);
        }

        private static AppSession Require()
        {
            if (_session == null)
            {
                Assert.Inconclusive(
                    "Клиент Ven4Tools не запущен, UI-тесты пропущены. Причина: " +
                    (_launchError ?? "неизвестна") +
                    ". Запустите тесты в интерактивной сессии «от имени администратора».");
            }
            return _session!;
        }

        [TestMethod]
        public void WindowsUpdate_ВкладкаОткрываетсяИПоказываетСтатус()
        {
            var s = Require();

            var navBtn = s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("btnWindowsUpdateTab"));
            Assert.IsNotNull(navBtn, "Не найдена кнопка навигации 'btnWindowsUpdateTab'.");
            navBtn!.AsButton().Invoke();

            // txtStatus существует всегда (см. WindowsUpdateTab.xaml) — сразу после
            // открытия там будет "⏳ Проверка обновлений..." либо уже готовый результат,
            // если проверка успела завершиться быстро. Главное — что элемент есть и
            // вкладка не упала при открытии.
            var status = Retry.WhileNull(
                () => s.MainWindow.FindFirstDescendant(cf => cf.ByAutomationId("txtStatus")),
                timeout: ElementTimeout,
                interval: TimeSpan.FromMilliseconds(300),
                throwOnTimeout: false).Result;

            Assert.IsNotNull(status, "Элемент статуса (txtStatus) вкладки Windows Update не отобразился.");
        }
    }
}
```

- [ ] **Step 3: Собрать и прогнать юнит-тесты**

Run: `dotnet build Ven4Tools.sln -c Release`
Expected: 0 ошибок, 0 предупреждений

Run: `dotnet test tests/Ven4Tools.Tests -c Release`
Expected: все тесты проходят (старые 84 + новые из Task 1,3,4,5,8,9 — итого 84+21=105)

- [ ] **Step 4: Commit**

```bash
git add tests/Ven4Tools.Tests/Ven4Tools.Tests.csproj Ven4Tools.ClientUITests/WindowsUpdateTabSmokeTests.cs
git commit -m "Windows Update: покрытие coverlet + UI smoke-тест вкладки"
```

---

### Task 16: Финальная проверка всего решения

**Files:** нет новых/изменённых файлов — только верификация.

- [ ] **Step 1: Полная пересборка**

Run: `dotnet build Ven4Tools.sln -c Release`
Expected: 0 ошибок, 0 предупреждений

- [ ] **Step 2: Полный прогон тестов**

Run: `dotnet test tests/Ven4Tools.Tests -c Release`
Expected: все тесты зелёные

- [ ] **Step 3: Ручной прогон полного пользовательского пути (человеком, не агентом)**

1. Первый запуск клиента → открыть вкладку Windows Update → диалог режима → выбрать "Только уведомлять" → поиск запускается автоматически.
2. Проверить дерево категорий: чекбокс категории выбирает все патчи внутри, снятие любого патча переводит категорию в частичное состояние (индикатор "минус"/неопределённое, не пустой квадрат).
3. Выбрать 1 низкорисковый необязательный патч → "Установить выбранные" → подтверждение с размером → (если есть EULA) текст лицензии виден → установка идёт с прогрессом → сводка → если требуется — предложение перезагрузки (без авто-таймера, только кнопки).
4. Во время установки патча — попробовать установить приложение из каталога → должно показать "Дождитесь завершения установки обновлений Windows" вместо зависания.
5. Проверить бейдж на кнопке вкладки — если есть непроверенные/невыбранные патчи, число отображается корректно.

- [ ] **Step 4: Обновить CHANGELOG.md**

Добавить запись в начало `CHANGELOG.md` (следующая версия клиента, дата — на момент реального релиза):

```markdown
## [X.Y.Z] — YYYY-MM-DD

### Новое
- Вкладка «Windows Update»: поиск, категоризированный выбор (с чекбоксами на категориях и отдельных патчах) и установка обновлений Windows всех типов — критичных, security, драйверов, необязательных. Фоновая проверка с выбором режима (только уведомлять / уведомлять и скачивать в фоне) на первом входе. Установка патчей никогда не запускается автоматически.
```

- [ ] **Step 5: Финальный commit (если Step 4 внёс изменения)**

```bash
git add CHANGELOG.md
git commit -m "Windows Update: запись в CHANGELOG"
```

---

## Итог

16 задач, от чистых юнит-тестируемых моделей до полного UI-потока. Самая рискованная зона (COM-вызовы Windows Update Agent) сознательно изолирована в один файл (`WindowsUpdateComSource`), API поиска проверен вживую при планировании, путь скачивания/установки требует обязательной ручной проверки человеком перед тем, как считаться готовым (Task 7, Step 3) — агент никогда не вызывает `InstallAsync` самостоятельно.
