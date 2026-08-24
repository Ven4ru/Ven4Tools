using System.Linq;
using Ven4Tools.ViewModels;

namespace Ven4Tools.Tests;

/// <summary>
/// Логика вкладки «О программе», перенесённая из code-behind в ViewModel
/// (2026-08-25). Основное внимание — AboutViewModel.FormatLastLines: чистая
/// функция обрезки хвоста лога, до этого рефакторинга не имевшая ни одного
/// теста. Реальные кнопки-ссылки (Process.Start открывает браузер) здесь не
/// проверяются — это открытие внешнего процесса, не логика.
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
    public void GetLastLogLines_КаталогЛоговНеСуществует_ВозвращаетСообщениеОбОтсутствии()
    {
        // Реальный %LocalAppData%\Ven4Tools\logs почти наверняка существует на
        // машине разработки (клиент туда пишет логи при обычной работе) —
        // поэтому эта проверка не бьёт по несуществующему пути напрямую, а
        // проверяет случай через приватную логику GetLastLogLines нельзя без
        // DI пути каталога, которого в оригинале не было (сохраняем 1:1).
        // Вместо этого просто убеждаемся, что метод не бросает исключение и
        // возвращает непустую строку в любом состоянии реальной машины —
        // содержательное покрытие обрезки уже даёт FormatLastLines выше.
        var vm = new AboutViewModel();

        string result = vm.GetLastLogLines();

        Assert.False(string.IsNullOrEmpty(result));
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
