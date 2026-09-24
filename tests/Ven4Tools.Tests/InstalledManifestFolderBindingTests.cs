using System.Security.Cryptography;
using System.Text;
using Ven4Tools.Launcher.Models;
using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// Кэш состава установки один на систему: дельта-обновление обязано пользоваться
/// им только когда он описывает клиента в ТЕКУЩЕЙ папке. Иначе (кэш от 5.2.0, а в
/// папке 5.0.0) план выходил пустым, и лаунчер рапортовал об обновлении, не тронув
/// ни одного файла.
/// </summary>
public sealed class InstalledManifestFolderBindingTests
{
    private static string Sha(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static ClientFileManifest ManifestFor(string exeContent, string dllContent) => new()
    {
        Version = "5.2.0",
        Files =
        [
            new ClientManifestFileEntry { Path = "Ven4Tools.exe", Sha256 = Sha(exeContent), Size = exeContent.Length },
            new ClientManifestFileEntry { Path = "Ven4Tools.dll", Sha256 = Sha(dllContent), Size = dllContent.Length },
        ],
    };

    private static void WriteClient(string dir, string exeContent, string dllContent)
    {
        File.WriteAllText(Path.Combine(dir, "Ven4Tools.exe"), exeContent, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "Ven4Tools.dll"), dllContent, new UTF8Encoding(false));
    }

    [Fact]
    public void КэшТойЖеУстановки_Подходит()
    {
        using var area = new TemporaryDirectory();
        WriteClient(area.Path, "exe-5.2.0", "dll-5.2.0");

        Assert.True(InstalledManifestStore.MatchesClientFolder(ManifestFor("exe-5.2.0", "dll-5.2.0"), area.Path));
    }

    [Fact]
    public void КэшДругойВерсии_Отклоняется()
    {
        using var area = new TemporaryDirectory();
        WriteClient(area.Path, "exe-5.0.0", "dll-5.0.0");

        Assert.False(InstalledManifestStore.MatchesClientFolder(ManifestFor("exe-5.2.0", "dll-5.2.0"), area.Path));
    }

    [Fact]
    public void СовпалТолькоExe_Отклоняется()
    {
        // Апхост может совпасть между сборками — решает и сборка клиента.
        using var area = new TemporaryDirectory();
        WriteClient(area.Path, "exe-5.2.0", "dll-5.0.0");

        Assert.False(InstalledManifestStore.MatchesClientFolder(ManifestFor("exe-5.2.0", "dll-5.2.0"), area.Path));
    }

    [Fact]
    public void ВПапкеНетКлиента_Отклоняется()
    {
        using var area = new TemporaryDirectory();

        Assert.False(InstalledManifestStore.MatchesClientFolder(ManifestFor("exe-5.2.0", "dll-5.2.0"), area.Path));
    }

    [Fact]
    public void ВКэшеНетЗаписиExe_Отклоняется()
    {
        using var area = new TemporaryDirectory();
        WriteClient(area.Path, "exe-5.2.0", "dll-5.2.0");
        var manifest = new ClientFileManifest
        {
            Version = "5.2.0",
            Files = [new ClientManifestFileEntry { Path = "Ven4Tools.dll", Sha256 = Sha("dll-5.2.0"), Size = 9 }],
        };

        Assert.False(InstalledManifestStore.MatchesClientFolder(manifest, area.Path));
    }
}
