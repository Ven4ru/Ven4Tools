using Ven4Tools.Helpers;

namespace Ven4Tools.Tests;

/// <summary>
/// Подмена junction'ом не ближайшего каталога файла, а его предка внутри
/// %LocalAppData% (например, самого <c>Ven4Tools</c> при записи в
/// <c>Ven4Tools\snapshots\x.json</c>). Раньше проверялся только ближайший каталог:
/// его атрибуты читались уже в цели подмены, и запись уходила туда.
/// </summary>
public sealed class VwFileHelperAncestorReparseTests
{
    [Fact]
    public void ПодменённыйПредок_ЗаписьОтклоняетсяИКаталогиВЦелиНеСоздаются()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData)) return;

        string baseDir = Path.Combine(localAppData, $"Ven4Tools.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);
        try
        {
            string target = Path.Combine(baseDir, "цель");
            string link = Path.Combine(baseDir, "ссылка");
            Directory.CreateDirectory(target);

            if (!FileHelperReparsePointTests.TryCreateDirectoryLinkForTests(link, target)) return;

            string file = Path.Combine(link, "вложенный", "снапшот.json");

            Assert.Throws<IOException>(() => FileHelper.WriteAllTextAtomic(file, "секрет"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ВложенныйОбычныйКаталог_ЗаписьВыполняется()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData)) return;

        string baseDir = Path.Combine(localAppData, $"Ven4Tools.Tests-{Guid.NewGuid():N}");
        try
        {
            string file = Path.Combine(baseDir, "вложенный", "снапшот.json");

            FileHelper.WriteAllTextAtomic(file, "{}");

            Assert.Equal("{}", File.ReadAllText(file));
        }
        finally
        {
            try { Directory.Delete(baseDir, recursive: true); } catch { }
        }
    }
}
