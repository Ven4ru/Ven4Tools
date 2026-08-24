using System.Linq;
using Ven4Tools.Models;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Логика вкладки «О программе», перенесённая из code-behind в ViewModel
/// (2026-08-25). Покрыты швы, не требующие ни реального каталога, ни реального
/// каталога логов: FormatLastLines (обрезка хвоста лога), GetLastLogLines с
/// переданным каталогом (временная папка), BuildEntries (проекция и сортировка
/// истории изменений) и сам ChangelogEntryViewModel — форматирование строк
/// списка, ставшее после перехода на ItemsControl+DataTemplate главным
/// изменением поведения. Реальные кнопки-ссылки (Process.Start открывает
/// браузер) здесь не проверяются — это открытие внешнего процесса, не логика.
/// </summary>
public class AboutViewModelTests
{
    [Fact]
    public void FormatLastLines_МеньшеЛимитаСтрок_НичегоНеОбрезает()
    {
        var lines = new[] { "строка1", "строка2", "строка3" };

        var result = AboutViewModel.FormatLastLines(lines, 15);

        Assert.Equal("строка1\nстрока2\nстрока3", result);
        Assert.DoesNotContain("обрезан", result);
    }

    [Fact]
    public void FormatLastLines_БольшеЛимитаСтрок_ОставляетТолькоПоследние()
    {
        var lines = Enumerable.Range(1, 20).Select(i => $"строка{i}").ToArray();

        var result = AboutViewModel.FormatLastLines(lines, 5);

        Assert.Contains("строка16\nстрока17\nстрока18\nстрока19\nстрока20", result);
        Assert.DoesNotContain("строка15", result);
        Assert.StartsWith("… (лог обрезан, показаны только последние строки) …\n", result);
    }

    [Fact]
    public void FormatLastLines_ПустойМассив_ВозвращаетПустоеТелоБезПометкиОбрезки()
    {
        var result = AboutViewModel.FormatLastLines(System.Array.Empty<string>(), 15);

        Assert.Equal("", result);
    }

    [Fact]
    public void FormatLastLines_ПревышенЛимитСимволов_ОбрезаетИПомечает()
    {
        // Одна строка длиннее 3000 символов — превышает maxChars даже при
        // единственной строке (лимит строк 15 не сработал бы сам по себе).
        string longLine = new string('x', 5000);
        var lines = new[] { longLine };

        var result = AboutViewModel.FormatLastLines(lines, 15);

        Assert.StartsWith("… (лог обрезан, показаны только последние строки) …\n", result);
        // Тело после пометки — ровно последние 3000 символов исходной строки.
        string body = result.Substring(result.IndexOf('\n') + 1);
        Assert.Equal(3000, body.Length);
        Assert.Equal(longLine.Substring(longLine.Length - 3000), body);
    }

    [Fact]
    public void FormatLastLines_РовноНаГраницеЛимитаСтрок_НеСчитаетсяОбрезкой()
    {
        var lines = Enumerable.Range(1, 15).Select(i => $"строка{i}").ToArray();

        var result = AboutViewModel.FormatLastLines(lines, 15);

        Assert.DoesNotContain("обрезан", result);
        Assert.StartsWith("строка1\n", result);
        Assert.EndsWith("строка15", result);
    }

    [Fact]
    public void GetLastLogLines_КаталогНеСуществует_ВозвращаетСообщениеОбОтсутствии()
    {
        // Путь каталога передаётся параметром — случай «каталога нет» проверяется
        // детерминированно, а не в зависимости от состояния реальной машины.
        var vm = new AboutViewModel();
        string missingDir = Path.Combine(Path.GetTempPath(), "ven4tools-test-missing-" + Guid.NewGuid());

        string result = vm.GetLastLogLines(logDir: missingDir);

        Assert.Equal("Лог не найден", result);
    }

    [Fact]
    public void GetLastLogLines_КаталогБезЛогФайлов_ВозвращаетСообщениеОбОтсутствии()
    {
        var vm = new AboutViewModel();
        string dir = Path.Combine(Path.GetTempPath(), "ven4tools-test-empty-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            string result = vm.GetLastLogLines(logDir: dir);

            Assert.Equal("Лог не найден", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void GetLastLogLines_ЕстьЛогФайл_ВозвращаетЕгоСодержимое()
    {
        var vm = new AboutViewModel();
        string dir = Path.Combine(Path.GetTempPath(), "ven4tools-test-log-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "install_test.log"), new[] { "строка1", "строка2" });

            string result = vm.GetLastLogLines(logDir: dir);

            Assert.Equal("строка1\nстрока2", result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ChangelogEntryViewModel_ФорматируетЗаголовокСВерсиейИДатой()
    {
        var entry = new CatalogChangelogEntry { Version = 5, Date = "01.01.2026", Message = "", AddedApps = new() };

        var vm = new ChangelogEntryViewModel(entry);

        Assert.Equal("v5  ·  01.01.2026", vm.HeaderText);
    }

    [Fact]
    public void ChangelogEntryViewModel_БезСообщения_HasMessageFalse()
    {
        var entry = new CatalogChangelogEntry { Version = 1, Date = "-", Message = "", AddedApps = new() };

        var vm = new ChangelogEntryViewModel(entry);

        Assert.False(vm.HasMessage);
        Assert.Equal("", vm.Message);
    }

    [Fact]
    public void ChangelogEntryViewModel_ССообщением_HasMessageTrue()
    {
        var entry = new CatalogChangelogEntry { Version = 1, Date = "-", Message = "Исправлен баг", AddedApps = new() };

        var vm = new ChangelogEntryViewModel(entry);

        Assert.True(vm.HasMessage);
        Assert.Equal("Исправлен баг", vm.Message);
    }

    [Fact]
    public void ChangelogEntryViewModel_БезДобавленныхПриложений_HasAddedAppsFalse()
    {
        var entry = new CatalogChangelogEntry { Version = 1, Date = "-", Message = "", AddedApps = new() };

        var vm = new ChangelogEntryViewModel(entry);

        Assert.False(vm.HasAddedApps);
        Assert.Equal("", vm.AddedAppsText);
    }

    [Fact]
    public void ChangelogEntryViewModel_СДобавленнымиПриложениями_ФорматируетСписок()
    {
        var entry = new CatalogChangelogEntry { Version = 1, Date = "-", Message = "", AddedApps = new() { "firefox", "vscode" } };

        var vm = new ChangelogEntryViewModel(entry);

        Assert.True(vm.HasAddedApps);
        Assert.Equal("+ firefox, vscode", vm.AddedAppsText);
    }

    [Fact]
    public void BuildEntries_СортируетПоУбываниюВерсии()
    {
        var entries = new List<CatalogChangelogEntry>
        {
            new() { Version = 1, Date = "-", Message = "", AddedApps = new() },
            new() { Version = 3, Date = "-", Message = "", AddedApps = new() },
            new() { Version = 2, Date = "-", Message = "", AddedApps = new() }
        };

        var result = AboutViewModel.BuildEntries(entries);

        Assert.Equal(3, result.Count);
        Assert.Equal("v3  ·  -", result[0].HeaderText);
        Assert.Equal("v2  ·  -", result[1].HeaderText);
        Assert.Equal("v1  ·  -", result[2].HeaderText);
    }

    [Fact]
    public void BuildEntries_ПустойСписок_ВозвращаетПустойСписок()
    {
        var result = AboutViewModel.BuildEntries(new List<CatalogChangelogEntry>());

        Assert.Empty(result);
    }

    [Fact]
    public void BuildEntries_Null_ВозвращаетПустойСписок()
    {
        var result = AboutViewModel.BuildEntries(null);

        Assert.Empty(result);
    }

    [Fact]
    public void BuildEntries_ВсегдаВозвращаетНовыйЭкземплярСписка()
    {
        // SetField в AboutViewModel сравнивает по ссылке — переиспользование
        // одного и того же списка молча погасило бы PropertyChanged.
        var entries = new List<CatalogChangelogEntry>
        {
            new() { Version = 1, Date = "-", Message = "", AddedApps = new() }
        };

        var first = AboutViewModel.BuildEntries(entries);
        var second = AboutViewModel.BuildEntries(entries);

        Assert.NotSame(first, second);
        Assert.NotSame(AboutViewModel.BuildEntries(null), AboutViewModel.BuildEntries(null));
    }

    [Fact]
    public void ChangelogEntries_БезЗагруженногоКаталога_ПустойСписокИHasChangelogFalse()
    {
        var vm = new AboutViewModel();

        Assert.Empty(vm.ChangelogEntries);
        Assert.False(vm.HasChangelog);
        Assert.True(vm.NoChangelog);
    }

    [Fact]
    public void RefreshChangelog_ПоднимаетPropertyChanged_ДляСпискаИВычисляемыхФлагов()
    {
        // Каталог часто догружается уже после открытия вкладки: без уведомления
        // повторный RefreshChangelog молча не обновил бы привязку ItemsSource.
        var vm = new AboutViewModel();
        var raised = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        vm.RefreshChangelog();

        Assert.Contains(nameof(vm.ChangelogEntries), raised);
        Assert.Contains(nameof(vm.HasChangelog), raised);
        Assert.Contains(nameof(vm.NoChangelog), raised);
    }

    [Fact]
    public void VersionText_НачинаетсяСоСлова_Версия()
    {
        var vm = new AboutViewModel();

        Assert.StartsWith("Версия ", vm.VersionText);
    }
}
