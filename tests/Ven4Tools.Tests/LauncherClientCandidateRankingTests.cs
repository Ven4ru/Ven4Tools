namespace Ven4Tools.Tests;

/// <summary>
/// Порядок кандидатов для «Найти клиент на диске».
///
/// Живой прогон 2026-09-10: поиск нашёл 16 копий Ven4Tools.exe и БЕЗ подтверждения
/// применил первую в порядке обхода каталогов — ею оказался старый бэкап
/// (Ven4Tools_Client_backup_pre_5.1.0, клиент 5.0.0), и папка установки молча
/// увела пользователя с рабочего клиента 5.1.1 на него. Подтверждение теперь
/// спрашивается всегда, а первым предлагается осмысленный кандидат — за это
/// отвечает ранжирование ниже.
/// </summary>
public sealed class LauncherClientCandidateRankingTests
{
    private static string MakeExe(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Ven4Tools.exe");
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A }); // версии нет — у всех одинаково
        return path;
    }

    [Fact]
    public void CurrentInstallFolderWinsEvenWhenListedLast()
    {
        using var root = new TemporaryDirectory();
        string current = Path.Combine(root.Path, "a", "b", "c", "Ven4Tools_Client");
        string other = Path.Combine(root.Path, "short");

        string otherExe = MakeExe(other);
        string currentExe = MakeExe(current);

        var ranked = Ven4Tools.Launcher.MainWindow.RankClientCandidates(new[] { otherExe, currentExe }, current);

        // Более короткий путь обычно выигрывает — но привязанная папка важнее.
        Assert.Equal(currentExe, ranked[0]);
    }

    [Fact]
    public void BackupFolderLosesToRegularFolder()
    {
        using var root = new TemporaryDirectory();
        string backup = Path.Combine(root.Path, "Ven4Tools_Client_backup_pre_5.1.0_20260903");
        string regular = Path.Combine(root.Path, "Ven4Tools_Client_regular_here");

        string backupExe = MakeExe(backup);
        string regularExe = MakeExe(regular);

        // Бэкап идёт первым во входном порядке — ровно как его отдал обход каталогов.
        var ranked = Ven4Tools.Launcher.MainWindow.RankClientCandidates(new[] { backupExe, regularExe }, currentClientPath: null);

        Assert.Equal(regularExe, ranked[0]);
    }

    [Fact]
    public void ShorterPathWinsWhenNothingElseDistinguishes()
    {
        using var root = new TemporaryDirectory();
        string deep = Path.Combine(root.Path, "one", "two", "three", "four");
        string shallow = Path.Combine(root.Path, "one");

        string deepExe = MakeExe(deep);
        string shallowExe = MakeExe(shallow);

        var ranked = Ven4Tools.Launcher.MainWindow.RankClientCandidates(new[] { deepExe, shallowExe }, currentClientPath: null);

        Assert.Equal(shallowExe, ranked[0]);
    }

    [Fact]
    public void KeepsEveryCandidate()
    {
        using var root = new TemporaryDirectory();
        string[] exes =
        [
            MakeExe(Path.Combine(root.Path, "one")),
            MakeExe(Path.Combine(root.Path, "two_backup")),
            MakeExe(Path.Combine(root.Path, "three", "deeper")),
        ];

        var ranked = Ven4Tools.Launcher.MainWindow.RankClientCandidates(exes, currentClientPath: null);

        // Ранжирование только переставляет: потерять найденную копию нельзя —
        // пользователь должен увидеть весь список.
        Assert.Equal(exes.Length, ranked.Count);
        Assert.All(exes, exe => Assert.True(ranked.Contains(exe), $"Кандидат потерян: {exe}"));
    }
}
