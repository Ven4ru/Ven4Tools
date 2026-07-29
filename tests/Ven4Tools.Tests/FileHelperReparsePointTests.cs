using Ven4Tools.Helpers;

namespace Ven4Tools.Tests;

/// <summary>
/// Регрессионные тесты на защиту атомарной записи от подмены пути junction'ом.
///
/// Предыстория: проверку на reparse point получили только два места, которые пишут
/// журналы через File.AppendAllText — AppLogger и журнал установки. При этом почти
/// все остальные данные клиента (настройки, профиль, избранное, история установок,
/// журнал сбоев, снапшоты конфигурации, кэш каталога) сохраняются через общий
/// FileHelper, и он такой проверки не имел, хотя пишет в то же самое дерево
/// %LocalAppData%\Ven4Tools из elevated-процесса. Проверка перенесена в общий
/// хелпер; эти тесты фиксируют её, чтобы расхождение не вернулось.
/// </summary>
public sealed class FileHelperReparsePointTests
{
    [Fact]
    public void ОбычныйКаталог_ЗаписьВыполняется()
    {
        using var temp = new TemporaryDirectory();
        string file = Path.Combine(temp.Path, "настройки.json");

        FileHelper.WriteAllTextAtomic(file, "{\"значение\":1}");

        Assert.Equal("{\"значение\":1}", File.ReadAllText(file));
    }

    [Fact]
    public void НесуществующийКаталог_СоздаётсяИЗаписьВыполняется()
    {
        using var temp = new TemporaryDirectory();
        // Первый запуск на чистой машине: каталога ещё нет — защита не должна
        // мешать его созданию.
        string file = Path.Combine(temp.Path, "вложенный", "профиль.json");

        FileHelper.WriteAllTextAtomic(file, "содержимое");

        Assert.Equal("содержимое", File.ReadAllText(file));
    }

    [Fact]
    public async Task ОбычныйКаталог_АсинхроннаяЗаписьВыполняется()
    {
        using var temp = new TemporaryDirectory();
        string file = Path.Combine(temp.Path, "история.json");

        await FileHelper.WriteAllTextAtomicAsync(file, "[]");

        Assert.Equal("[]", File.ReadAllText(file));
    }

    [Fact]
    public void ПодменённыйКаталог_ЗаписьОтклоняется()
    {
        using var temp = new TemporaryDirectory();
        string target = Path.Combine(temp.Path, "цель");
        string link = Path.Combine(temp.Path, "ссылка");
        Directory.CreateDirectory(target);

        if (!TryCreateDirectoryLink(link, target)) return;

        string file = Path.Combine(link, "настройки.json");

        Assert.Throws<IOException>(() => FileHelper.WriteAllTextAtomic(file, "секрет"));
        // Главное — что в цель подмены ничего не попало.
        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public async Task ПодменённыйКаталог_АсинхроннаяЗаписьОтклоняется()
    {
        using var temp = new TemporaryDirectory();
        string target = Path.Combine(temp.Path, "цель");
        string link = Path.Combine(temp.Path, "ссылка");
        Directory.CreateDirectory(target);

        if (!TryCreateDirectoryLink(link, target)) return;

        string file = Path.Combine(link, "история.json");

        await Assert.ThrowsAsync<IOException>(
            () => FileHelper.WriteAllTextAtomicAsync(file, "секрет"));
        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public void ПодменённыйФайл_ЗаписьОтклоняется()
    {
        using var temp = new TemporaryDirectory();
        string target = Path.Combine(temp.Path, "цель.json");
        string link = Path.Combine(temp.Path, "настройки.json");
        File.WriteAllText(target, "исходное");

        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception)
        {
            // Создание символических ссылок требует прав администратора либо
            // включённого режима разработчика. Где это недоступно — сам сценарий
            // подмены тоже недостижим, проверять нечего.
            return;
        }

        Assert.Throws<IOException>(() => FileHelper.WriteAllTextAtomic(link, "подменённое"));
        Assert.Equal("исходное", File.ReadAllText(target));
    }

    /// <summary>
    /// Подмена каталога ссылкой. Сначала junction (mklink /J) и только потом
    /// символическая ссылка — порядок принципиален: junction создаётся БЕЗ прав
    /// администратора и без режима разработчика, то есть именно он и есть реальный
    /// вектор атаки (обычный пользовательский процесс подменяет каталог, elevated
    /// клиент пишет по подменённому пути). Тест на символических ссылках молча
    /// пропускался бы на обычной машине разработчика и не проверял бы ничего.
    /// </summary>
    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(link);
            psi.ArgumentList.Add(target);

            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(15000);
            if (Directory.Exists(link)) return true;
        }
        catch (Exception)
        {
            // junction не получился — пробуем символическую ссылку ниже
        }

        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception)
        {
            // Символические ссылки требуют прав администратора либо режима
            // разработчика. Если недоступны оба способа — сценарий подмены
            // на этой машине недостижим, проверять нечего.
            return false;
        }
    }
}
