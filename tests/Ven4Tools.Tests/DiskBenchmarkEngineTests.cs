using Ven4Tools.Models;
using Ven4Tools.Services.DiskBenchmark;

namespace Ven4Tools.Tests;

public class PciLinkInfoTests
{
    [Fact]
    public void ЛинияPcie4x4_ДаётПотолокОколо7900МБс()
    {
        var link = new PciLinkInfo { Generation = 4, Width = 4 };

        Assert.True(link.IsKnown);
        Assert.Equal(7876, link.CeilingMegabytesPerSecond, 0);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(4, 0)]
    [InlineData(9, 4)]
    [InlineData(-1, 4)]
    public void НеполныеИлиНевозможныеДанные_ПотолокНеСчитается(int generation, int width)
    {
        var link = new PciLinkInfo { Generation = generation, Width = width };

        Assert.False(link.IsKnown);
        Assert.Equal(0, link.CeilingMegabytesPerSecond);
    }

    [Fact]
    public void ЛинияПоУмолчанию_Неизвестна()
    {
        Assert.False(PciLinkInfo.Unknown.IsKnown);
        Assert.Equal(0, PciLinkInfo.Unknown.CeilingMegabytesPerSecond);
    }
}

public class DiskBenchmarkEngineTests
{
    /// <summary>FILE_FLAG_NO_BUFFERING.</summary>
    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private const int Alignment = 4096;

    /// <summary>
    /// Доказывает, что связка «флаг обхода кэша + выровненный закреплённый буфер» рабочая
    /// на этой машине, а не только в теории: именно на ней держится весь замер.
    /// </summary>
    [Fact]
    public async Task НебуферизованныйДескриптор_ПишетИЧитаетВыровненныйБлок()
    {
        string path = Path.Combine(Path.GetTempPath(), "Ven4Tools_test_" + Guid.NewGuid().ToString("N") + ".tmp");

        byte[] raw = new byte[Alignment * 3];
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(
            raw, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            long address = pin.AddrOfPinnedObject().ToInt64();
            int offset = (int)((Alignment - address % Alignment) % Alignment);
            Assert.Equal(0, (address + offset) % Alignment);

            var payload = raw.AsMemory(offset, Alignment);
            new Random(7).NextBytes(payload.Span);
            byte[] expected = payload.ToArray();

            using (var handle = File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None,
                       NoBuffering | FileOptions.WriteThrough | FileOptions.Asynchronous, Alignment))
            {
                await RandomAccess.WriteAsync(handle, payload, 0);
            }

            payload.Span.Clear();

            using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                       NoBuffering | FileOptions.Asynchronous))
            {
                int read = await RandomAccess.ReadAsync(handle, payload, 0);
                Assert.Equal(Alignment, read);
            }

            Assert.Equal(expected, payload.ToArray());
        }
        finally
        {
            pin.Free();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Паттерны_СоответствуютНаборуCrystalDiskMark()
    {
        var names = DiskBenchmarkEngine.Patterns.Select(p => p.Name).ToArray();

        Assert.Equal(new[] { "SEQ1M Q8T1", "SEQ1M Q1T1", "RND4K Q32T16", "RND4K Q1T1" }, names);
    }

    [Fact]
    public void ГлубинаОчереди_ЭтоПроизведениеОчередиНаПотоки()
    {
        var random = DiskBenchmarkEngine.Patterns.Single(p => p.Name == "RND4K Q32T16");

        Assert.Equal(512, random.Streams);
        Assert.Equal(4096, random.BlockSize);
        Assert.False(random.Sequential);
        // T16 означает шестнадцать потоков операционной системы, а Q32 — глубину очереди
        // внутри каждого из них. Именно так подписи трактует CrystalDiskMark.
        Assert.Equal(16, random.ThreadCount);
        Assert.Equal(32, random.QueueDepth);
    }

    /// <summary>
    /// На глубине очереди 1 скорость равна единице, делённой на задержку, поэтому накладные
    /// расходы асинхронного пути .NET попадают в результат целиком: замеры показали 41 мкс
    /// на асинхронной записи против 21 мкс на синхронной. Такие паттерны обязаны измеряться
    /// синхронным вводом-выводом, иначе вместо диска измеряется само приложение.
    /// </summary>
    [Theory]
    [InlineData("SEQ1M Q1T1")]
    [InlineData("RND4K Q1T1")]
    public void ПаттерныСОчередьюОдин_ИзмеряютсяСинхронно(string name)
    {
        var pattern = DiskBenchmarkEngine.Patterns.Single(p => p.Name == name);

        Assert.Equal(1, pattern.QueueDepth);
        Assert.True(DiskBenchmarkEngine.UsesSynchronousIo(pattern));
    }

    /// <summary>
    /// А там, где очередь действительно набирается, синхронный путь недопустим: он физически
    /// не даст держать несколько операций в полёте.
    /// </summary>
    [Theory]
    [InlineData("SEQ1M Q8T1")]
    [InlineData("RND4K Q32T16")]
    public void ПаттерныСГлубокойОчередью_ИзмеряютсяАсинхронно(string name)
    {
        var pattern = DiskBenchmarkEngine.Patterns.Single(p => p.Name == name);

        Assert.True(pattern.QueueDepth > 1);
        Assert.False(DiskBenchmarkEngine.UsesSynchronousIo(pattern));
    }

    [Theory]
    [InlineData(BenchmarkProfile.Fast, 1)]
    [InlineData(BenchmarkProfile.Normal, 3)]
    [InlineData(BenchmarkProfile.Precise, 5)]
    public void ПрофильЗадаётЧислоПроходов(BenchmarkProfile profile, int expected)
    {
        Assert.Equal(expected, DiskBenchmarkEngine.PassesForProfile(profile));
    }

    [Fact]
    public void РазмерыТестовогоФайла_КратныБлокуВОдинМегабайт()
    {
        foreach (long size in DiskBenchmarkEngine.FileSizes)
        {
            Assert.Equal(0, size % (1024 * 1024));
            // Блоков должно хватать на все потоки запросов самого широкого паттерна.
            Assert.True(size / (1024 * 1024) >= 8);
        }
    }
}
