using Ven4Tools.Services;

namespace Ven4Tools.Tests;

/// <summary>
/// «Освободить место на C»: разбор вывода DISM, разбор размеров, защита от
/// чистки «не той» папки временных файлов и состав категорий.
/// </summary>
public sealed class DiskCleanupServiceTests
{
    // Настоящий вывод DISM /English /Online /Cleanup-Image /AnalyzeComponentStore
    // (Windows 11, строки прогресса сокращены; в выводе они разделены \r).
    private const string RealDismOutput =
        "\r\nDeployment Image Servicing and Management tool\r\nVersion: 10.0.26100.9549\r\n\r\n" +
        "Image Version: 10.0.26300.9711\r\n\r\n" +
        "[==                         4.2%                           ] \r\n" +
        "[==========================100.0%==========================] \r\n" +
        "Component Store (WinSxS) information:\r\n\r\n" +
        "Windows Explorer Reported Size of Component Store : 18.99 GB\r\n\r\n" +
        "Actual Size of Component Store : 17.83 GB\r\n\r\n" +
        "    Shared with Windows : 8.03 GB\r\n" +
        "    Backups and Disabled Features : 9.79 GB\r\n" +
        "    Cache and Temporary Data :  0 bytes\r\n\r\n" +
        "Date of Last Cleanup : 2026-10-19 15:20:10\r\n\r\n" +
        "Number of Reclaimable Packages : 9\r\n" +
        "Component Store Cleanup Recommended : Yes\r\n\r\n" +
        "The operation completed successfully.\r\n";

    [Fact]
    public void ParsesRealDismOutput()
    {
        ComponentStoreAnalysis? a = DiskCleanupService.ParseComponentStoreAnalysis(RealDismOutput);
        Assert.NotNull(a);
        Assert.Equal((long)Math.Round(17.83 * 1024 * 1024 * 1024), a.ActualSizeBytes);
        Assert.Equal((long)Math.Round(9.79 * 1024 * 1024 * 1024), a.BackupsBytes);
        Assert.Equal(0, a.CacheBytes);
        Assert.Equal(9, a.ReclaimablePackages);
        Assert.True(a.CleanupRecommended);
        Assert.Equal(a.BackupsBytes, a.ReclaimableUpperBound);
    }

    [Fact]
    public void NotRecommendedIsFalse()
    {
        ComponentStoreAnalysis? a = DiskCleanupService.ParseComponentStoreAnalysis(
            "Number of Reclaimable Packages : 0\r\nComponent Store Cleanup Recommended : No\r\n");
        Assert.NotNull(a);
        Assert.Equal(0, a.ReclaimablePackages);
        Assert.False(a.CleanupRecommended);
        Assert.Null(a.ReclaimableUpperBound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error: 87\r\n\r\nThe parameter is incorrect.")]
    public void OutputWithoutFieldsGivesNull(string output)
    {
        Assert.Null(DiskCleanupService.ParseComponentStoreAnalysis(output));
    }

    [Theory]
    [InlineData("0 bytes", 0L)]
    [InlineData("512 bytes", 512L)]
    [InlineData("1.5 KB", 1536L)]
    [InlineData("299.34 MB", 313880740L)]
    [InlineData("299,34 MB", 313880740L)]
    [InlineData("2 GB", 2147483648L)]
    [InlineData("1 TB", 1099511627776L)]
    public void ParsesSizes(string text, long expected)
    {
        Assert.Equal(expected, DiskCleanupService.ParseSize(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("много")]
    [InlineData("12 PB")]
    public void UnknownSizeIsNull(string text)
    {
        Assert.Null(DiskCleanupService.ParseSize(text));
    }

    [Theory]
    [InlineData(@"C:\Users\user\AppData\Local\Temp", true)]
    [InlineData(@"C:\Users\user\AppData\Local\Temp\", true)]
    [InlineData(@"C:\Windows\Temp", true)]
    [InlineData(@"D:\tmp", true)]
    [InlineData(@"C:\", false)]
    [InlineData(@"C:", false)]
    [InlineData(@"C:\Users\user\Documents", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"C:\Temp2", false)]
    [InlineData("", false)]
    public void OnlyRealTempFoldersAreCleaned(string path, bool expected)
    {
        Assert.Equal(expected, DiskCleanupService.IsSafeTempRoot(path));
    }

    [Fact]
    public void CategoriesHaveUniqueIdsAndWarningsWhereIrreversibleForWindows()
    {
        var categories = DiskCleanupService.Categories;
        Assert.Equal(categories.Count, categories.Select(c => c.Id).Distinct().Count());
        Assert.False(string.IsNullOrEmpty(categories.Single(c => c.Id == "windows-old").Warning));
        Assert.False(string.IsNullOrEmpty(categories.Single(c => c.Id == "winsxs").Warning));
        Assert.All(categories.Where(c => c.Method == CleanupMethod.CleanMgr),
            c => Assert.NotEmpty(c.CleanMgrHandlers));
    }

    [Fact]
    public void ResultTextUsesRealDifference()
    {
        const long gb = 1024L * 1024 * 1024;
        string text = ViewModels.DiskSpaceViewModel.BuildResultText(10 * gb, 13 * gb);
        // Сравниваем с тем же форматированием: язык и разделитель дробной части зависят от машины.
        Assert.Contains(DiskCleanupService.FormatSize(3 * gb), text);
        Assert.Contains(DiskCleanupService.FormatSize(10 * gb), text);
        Assert.Contains(DiskCleanupService.FormatSize(13 * gb), text);
    }

    [Fact]
    public void ResultTextWithoutDataHasNoNumbers()
    {
        string text = ViewModels.DiskSpaceViewModel.BuildResultText(null, 5 * 1024L * 1024 * 1024);
        Assert.DoesNotContain(DiskCleanupService.FormatSize(5 * 1024L * 1024 * 1024), text);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}
