using System.Text;
using Ven4Tools.Services;

namespace Ven4Tools.Tests;

public sealed class HashHelperTests
{
    private const string AbcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    [Theory]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", true)]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", true)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("za7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", false)]
    public void HasExpectedHash_ValidatesSha256Format(string hash, bool expected)
    {
        Assert.Equal(expected, HashHelper.HasExpectedHash(hash));
    }

    [Fact]
    public async Task VerifyHashAsync_AcceptsMatchingHashAndRejectsMismatch()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "abc", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            Assert.Equal(AbcSha256, await HashHelper.ComputeSha256Async(path));
            Assert.True(await HashHelper.VerifyHashAsync(path, AbcSha256.ToUpperInvariant()));
            Assert.False(await HashHelper.VerifyHashAsync(path, new string('0', 64)));
            Assert.False(await HashHelper.VerifyHashAsync(path, ""));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
