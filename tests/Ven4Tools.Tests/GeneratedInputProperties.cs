using FsCheck.Xunit;
using Ven4Tools.Launcher.Services;
using Ven4Tools.Services;
using LauncherDownloadValidator = Ven4Tools.Launcher.Services.DownloadValidator;

namespace Ven4Tools.Tests;

public sealed class GeneratedInputProperties
{
    [Property(MaxTest = 500)]
    public bool VersionComparison_IsReflexive(string? version)
    {
        return version is null || VersionComparer.Compare(version, version) == 0;
    }

    [Property(MaxTest = 500)]
    public bool VersionComparison_IsAntisymmetric(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return true;
        }

        int forward = Math.Sign(VersionComparer.Compare(left, right));
        int backward = Math.Sign(VersionComparer.Compare(right, left));
        return forward == -backward;
    }

    [Property(MaxTest = 500)]
    public bool UntrustedHost_RemainsRejectedForAnyPath(string? path)
    {
        string encodedPath = Uri.EscapeDataString(path ?? string.Empty);
        return !LauncherDownloadValidator.IsAllowedDownloadHost(
            $"https://attacker.example/{encodedPath}");
    }

    [Property(MaxTest = 500)]
    public bool Sha256Format_RequiresExactlyThirtyTwoBytes(byte[]? bytes)
    {
        if (bytes is null)
        {
            return !HashHelper.HasExpectedHash(null);
        }

        string hexadecimal = Convert.ToHexString(bytes);
        return HashHelper.HasExpectedHash(hexadecimal) == (bytes.Length == 32);
    }

    [Property(MaxTest = 500)]
    public bool AcceptedZipEntry_NeverEscapesDestination(string? entryName)
    {
        const string root = @"C:\safe-root\";
        try
        {
            string destination = SafeZipExtractor.GetSafeDestinationPath(
                root,
                entryName ?? string.Empty);
            return destination.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidDataException)
        {
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (NotSupportedException)
        {
            return true;
        }
    }
}
