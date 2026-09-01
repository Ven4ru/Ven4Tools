using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Частичная (пофайловая) установка — основа блочного обновления клиента.
/// Проверяется главное свойство транзакции: либо применён весь набор, либо
/// каталог остаётся ровно таким, каким был. Половина новой и половина старой
/// версии в папке клиента — это неработающая программа.
/// </summary>
public sealed class TransactionalDirectoryInstallerPartialTests
{
    [Fact]
    public void InstallPartial_ReplacesListedFilesAndLeavesOthersUntouched()
    {
        using var area = new TemporaryDirectory();
        string target = Path.Combine(area.Path, "client");
        string incoming = Path.Combine(area.Path, "incoming");
        Directory.CreateDirectory(Path.Combine(target, "Resources"));
        Directory.CreateDirectory(incoming);

        File.WriteAllText(Path.Combine(target, "Ven4Tools.dll"), "старая dll");
        File.WriteAllText(Path.Combine(target, "не-трогать.dll"), "нетронутая");
        File.WriteAllText(Path.Combine(target, "Resources", "устаревший.dat"), "лишний");

        string source = Path.Combine(incoming, "0.bin");
        File.WriteAllText(source, "новая dll");
        string newSource = Path.Combine(incoming, "1.bin");
        File.WriteAllText(newSource, "совсем новый файл");

        new TransactionalDirectoryInstaller().InstallPartial(
            target,
            [
                new PartialFileUpdate("Ven4Tools.dll", source),
                new PartialFileUpdate("Resources/Новый/добавленный.dat", newSource),
            ],
            ["Resources/устаревший.dat"],
            CancellationToken.None);

        Assert.Equal("новая dll", File.ReadAllText(Path.Combine(target, "Ven4Tools.dll")));
        Assert.Equal("нетронутая", File.ReadAllText(Path.Combine(target, "не-трогать.dll")));
        Assert.Equal(
            "совсем новый файл",
            File.ReadAllText(Path.Combine(target, "Resources", "Новый", "добавленный.dat")));
        Assert.False(File.Exists(Path.Combine(target, "Resources", "устаревший.dat")));

        // Ни одной служебной заготовки/резервной копии после успеха остаться не должно.
        Assert.DoesNotContain(
            Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories),
            f => TransactionalDirectoryInstaller.IsTransientArtifactName(Path.GetFileName(f)));

        // Исходники во временном каталоге копируются, а не переносятся: вызывающий
        // код держит на них защитные хендлы до конца установки.
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void InstallPartial_RestoresOriginalsWhenCommitFailsMidway()
    {
        var files = new SimulatedFileOperations(
            existing: [@"C:\client\a.dll", @"C:\client\b.dll", @"C:\client\c.dll"]);
        // Переносы: 1 — резерв a.dll, 2 — установка a.dll, 3 — резерв b.dll,
        // 4 — установка b.dll (роняем ровно посередине набора).
        files.FailMoveNumber = 4;

        var installer = new TransactionalDirectoryInstaller(
            new AlwaysPresentDirectoryOperations(), files);

        Assert.Throws<IOException>(() => installer.InstallPartial(
            @"C:\client",
            [
                new PartialFileUpdate("a.dll", @"C:\tmp\0.bin"),
                new PartialFileUpdate("b.dll", @"C:\tmp\1.bin"),
            ],
            ["c.dll"],
            CancellationToken.None));

        // Каталог вернулся к исходному состоянию: оба файла на месте, удаляемый цел.
        Assert.Contains(@"C:\client\a.dll", files.Existing);
        Assert.Contains(@"C:\client\b.dll", files.Existing);
        Assert.Contains(@"C:\client\c.dll", files.Existing);
        Assert.DoesNotContain(files.Existing, p => TransactionalDirectoryInstaller.IsTransientArtifactName(Path.GetFileName(p)));
    }

    [Fact]
    public void InstallPartial_KeepsDeletedFilesUntilWholeSetCommitted()
    {
        // Удаление — последняя фаза и тоже через отложенный откат: файл, пропавший
        // из новой версии, не должен исчезнуть, если набор в целом не зафиксирован.
        var files = new SimulatedFileOperations(existing: [@"C:\client\a.dll", @"C:\client\legacy.dll"]);
        files.FailMoveNumber = 2; // падаем на установке a.dll, до фазы удалений

        var installer = new TransactionalDirectoryInstaller(
            new AlwaysPresentDirectoryOperations(), files);

        Assert.Throws<IOException>(() => installer.InstallPartial(
            @"C:\client",
            [new PartialFileUpdate("a.dll", @"C:\tmp\0.bin")],
            ["legacy.dll"],
            CancellationToken.None));

        Assert.Contains(@"C:\client\legacy.dll", files.Existing);
        Assert.Contains(@"C:\client\a.dll", files.Existing);
    }

    [Fact]
    public void InstallPartial_RemovesStagedFileWhenNewFileFailsToCommit()
    {
        // Файл, которого раньше не было: откат обязан убрать его целиком, а не
        // оставить наполовину применённым.
        var files = new SimulatedFileOperations(existing: [@"C:\client\a.dll"]);
        files.FailMoveNumber = 3; // 1 — резерв a.dll, 2 — установка a.dll, 3 — установка нового

        var installer = new TransactionalDirectoryInstaller(
            new AlwaysPresentDirectoryOperations(), files);

        Assert.Throws<IOException>(() => installer.InstallPartial(
            @"C:\client",
            [
                new PartialFileUpdate("a.dll", @"C:\tmp\0.bin"),
                new PartialFileUpdate("новый.dll", @"C:\tmp\1.bin"),
            ],
            [],
            CancellationToken.None));

        Assert.Contains(@"C:\client\a.dll", files.Existing);
        Assert.DoesNotContain(@"C:\client\новый.dll", files.Existing);
        Assert.DoesNotContain(files.Existing, p => TransactionalDirectoryInstaller.IsTransientArtifactName(Path.GetFileName(p)));
    }

    [Fact]
    public void InstallPartial_RejectsPathsOutsideClientFolder()
    {
        var files = new SimulatedFileOperations(existing: []);
        var installer = new TransactionalDirectoryInstaller(
            new AlwaysPresentDirectoryOperations(), files);

        Assert.Throws<InvalidOperationException>(() => installer.InstallPartial(
            @"C:\client",
            [new PartialFileUpdate("../evil.dll", @"C:\tmp\0.bin")],
            [],
            CancellationToken.None));

        Assert.Throws<InvalidOperationException>(() => installer.InstallPartial(
            @"C:\client",
            [],
            ["../../Windows/System32/kernel32.dll"],
            CancellationToken.None));
    }

    [Fact]
    public void InstallPartial_ThrowsWhenClientFolderMissing()
    {
        var installer = new TransactionalDirectoryInstaller(
            new MissingDirectoryOperations(), new SimulatedFileOperations(existing: []));

        Assert.Throws<DirectoryNotFoundException>(() => installer.InstallPartial(
            @"C:\client", [], [], CancellationToken.None));
    }

    [Theory]
    [InlineData("Ven4Tools.dll.new-0123456789abcdef0123456789abcdef", true)]
    [InlineData("Ven4Tools.dll.old-0123456789ABCDEF0123456789ABCDEF", true)]
    [InlineData("Ven4Tools.dll", false)]
    [InlineData("Ven4Tools.dll.new-короткий", false)]
    [InlineData("Ven4Tools.dll.old-zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", false)]
    [InlineData(".new-0123456789abcdef0123456789abcdef", false)]
    [InlineData("", false)]
    public void IsTransientArtifactName_MatchesOnlyStrictPattern(string name, bool expected)
    {
        Assert.Equal(expected, TransactionalDirectoryInstaller.IsTransientArtifactName(name));
    }

    [Fact]
    public void TryParseTransientArtifactName_SeparatesBackupFromStagedFile()
    {
        Assert.True(TransactionalDirectoryInstaller.TryParseTransientArtifactName(
            "Ven4Tools.dll.old-0123456789abcdef0123456789abcdef", out string original, out bool isBackup));
        Assert.Equal("Ven4Tools.dll", original);
        Assert.True(isBackup);

        Assert.True(TransactionalDirectoryInstaller.TryParseTransientArtifactName(
            "Ven4Tools.dll.new-0123456789abcdef0123456789abcdef", out original, out isBackup));
        Assert.Equal("Ven4Tools.dll", original);
        Assert.False(isBackup);
    }

    private sealed class AlwaysPresentDirectoryOperations : IDirectoryOperations
    {
        public bool Exists(string path) => true;
        public void Move(string source, string destination) => throw new NotSupportedException();
        public void Delete(string path, bool recursive) => throw new NotSupportedException();
    }

    private sealed class MissingDirectoryOperations : IDirectoryOperations
    {
        public bool Exists(string path) => false;
        public void Move(string source, string destination) => throw new NotSupportedException();
        public void Delete(string path, bool recursive) => throw new NotSupportedException();
    }

    /// <summary>
    /// Файловые операции в памяти: позволяют уронить установку на заданном по счёту
    /// переименовании — то есть ровно посреди набора файлов, что на реальном диске
    /// воспроизводится только случайно.
    /// </summary>
    private sealed class SimulatedFileOperations : IFileOperations
    {
        private int _moveCount;

        public SimulatedFileOperations(IEnumerable<string> existing)
        {
            Existing = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        }

        public HashSet<string> Existing { get; }

        public int? FailMoveNumber { get; set; }

        public bool FileExists(string path) => Existing.Contains(path);

        public void CreateDirectory(string path)
        {
        }

        public void CopyFile(string source, string destination, bool overwrite) => Existing.Add(destination);

        public void MoveFile(string source, string destination, bool overwrite)
        {
            _moveCount++;
            if (_moveCount == FailMoveNumber)
            {
                throw new IOException("Смоделированный сбой переименования.");
            }

            Existing.Remove(source);
            Existing.Add(destination);
        }

        public void DeleteFile(string path) => Existing.Remove(path);
    }
}
