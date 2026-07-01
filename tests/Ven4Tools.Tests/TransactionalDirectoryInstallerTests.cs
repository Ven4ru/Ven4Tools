using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class TransactionalDirectoryInstallerTests
{
    [Fact]
    public void Install_ReplacesWholeDirectoryWithoutLeavingOldFiles()
    {
        using var area = new TemporaryDirectory();
        string target = Path.Combine(area.Path, "client");
        string staging = Path.Combine(area.Path, "staging");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(target, "old-only.txt"), "old");
        File.WriteAllText(Path.Combine(staging, "new-only.txt"), "new");

        new TransactionalDirectoryInstaller().Install(
            staging,
            target,
            CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(target, "old-only.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "new-only.txt")));
        Assert.Empty(Directory.GetDirectories(area.Path, "client.backup-*"));
    }

    [Fact]
    public void Install_RestoresPreviousVersionWhenCommitFails()
    {
        var directories = new SimulatedDirectoryOperations(
            existing: ["client", "staging"],
            failMoveNumber: 2);

        Assert.Throws<IOException>(
            () => new TransactionalDirectoryInstaller(directories).Install(
                @"C:\sandbox\staging",
                @"C:\sandbox\client",
                CancellationToken.None));

        Assert.Contains("client", directories.Existing);
        Assert.DoesNotContain(
            directories.Existing,
            path => path.Contains(".backup-", StringComparison.Ordinal));
    }

    [Fact]
    public void Install_RestoresPreviousVersionWhenCancelledAfterBackup()
    {
        using var cancellation = new CancellationTokenSource();
        var directories = new SimulatedDirectoryOperations(
            existing: ["client", "staging"],
            afterMove: moveNumber =>
            {
                if (moveNumber == 1)
                {
                    cancellation.Cancel();
                }
            });

        Assert.ThrowsAny<OperationCanceledException>(
            () => new TransactionalDirectoryInstaller(directories).Install(
                @"C:\sandbox\staging",
                @"C:\sandbox\client",
                cancellation.Token));

        Assert.Contains("client", directories.Existing);
    }

    private sealed class SimulatedDirectoryOperations : IDirectoryOperations
    {
        private readonly int? _failMoveNumber;
        private readonly Action<int>? _afterMove;
        private int _moveCount;

        public SimulatedDirectoryOperations(
            IEnumerable<string> existing,
            int? failMoveNumber = null,
            Action<int>? afterMove = null)
        {
            Existing = new HashSet<string>(
                existing.Select(path => Path.GetFileName(path)!),
                StringComparer.OrdinalIgnoreCase);
            _failMoveNumber = failMoveNumber;
            _afterMove = afterMove;
        }

        public HashSet<string> Existing { get; }

        public bool Exists(string path) => Existing.Contains(Path.GetFileName(path));

        public void Move(string source, string destination)
        {
            _moveCount++;
            if (_moveCount == _failMoveNumber)
            {
                throw new IOException("Simulated commit failure.");
            }

            Existing.Remove(Path.GetFileName(source));
            Existing.Add(Path.GetFileName(destination));
            _afterMove?.Invoke(_moveCount);
        }

        public void Delete(string path, bool recursive)
        {
            Existing.Remove(Path.GetFileName(path));
        }
    }
}
