using Ven4Tools.Launcher.Services;

namespace Ven4Tools.Tests;

public sealed class SetupComponentRequestServiceTests
{
    [Fact]
    public void Consume_ReturnsSelectedComponentsAndRemovesMarkers()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "Ven4Tools.SetupRequests.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string winget = Path.Combine(directory, "install-winget.pending");
            string chocolatey = Path.Combine(directory, "install-chocolatey.pending");
            File.WriteAllText(winget, "1");
            File.WriteAllText(chocolatey, "1");

            var result = SetupComponentRequestService.Consume(directory);

            Assert.Equal(
                new[] { SetupComponent.Winget, SetupComponent.Chocolatey },
                result);
            Assert.False(File.Exists(winget));
            Assert.False(File.Exists(chocolatey));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Consume_EmptyDirectory_ReturnsNoRequests()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "Ven4Tools.SetupRequests.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            Assert.Empty(SetupComponentRequestService.Consume(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
