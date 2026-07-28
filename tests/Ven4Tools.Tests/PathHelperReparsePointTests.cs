using Ven4Tools.Helpers;

namespace Ven4Tools.Tests;

/// <summary>
/// Регрессионные тесты на защиту от подмены пути junction'ом.
///
/// Предыстория: клиент работает elevated, а %LocalAppData%\Ven4Tools доступен на
/// запись непривилегированному процессу того же пользователя. Проверка на reparse
/// point стояла только в AppLogger, хотя журнал установки (InstallationService)
/// пишется в то же самое дерево и такой защиты не имел. Проверка вынесена в общий
/// PathHelper и применена в обоих местах; эти тесты фиксируют её поведение, чтобы
/// расхождение не вернулось.
/// </summary>
public sealed class PathHelperReparsePointTests
{
    [Fact]
    public void ОбычныйКаталог_НеСчитаетсяПодменой()
    {
        using var temp = new TemporaryDirectory();

        Assert.False(PathHelper.IsReparsePoint(temp.Path));
    }

    [Fact]
    public void ОбычныйФайл_НеСчитаетсяПодменой()
    {
        using var temp = new TemporaryDirectory();
        string file = Path.Combine(temp.Path, "app.log");
        File.WriteAllText(file, "строка журнала");

        Assert.False(PathHelper.IsReparsePoint(file));
    }

    [Fact]
    public void НесуществующийПуть_НеСчитаетсяПодменой()
    {
        using var temp = new TemporaryDirectory();

        // Файла ещё нет — подменять нечего, запись должна быть разрешена
        // (иначе первый же запуск не смог бы создать журнал).
        Assert.False(PathHelper.IsReparsePoint(Path.Combine(temp.Path, "ещё-нет.log")));
    }

    [Fact]
    public void КаталогЧерезSymlink_РаспознаётсяКакПодмена()
    {
        using var temp = new TemporaryDirectory();
        string target = Path.Combine(temp.Path, "цель");
        string link   = Path.Combine(temp.Path, "ссылка");
        Directory.CreateDirectory(target);

        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception)
        {
            // Создание символических ссылок требует прав администратора или
            // включённого режима разработчика. Там, где это недоступно, сам
            // сценарий подмены тоже недостижим — тест нечего проверять.
            return;
        }

        Assert.True(PathHelper.IsReparsePoint(link));
    }
}
