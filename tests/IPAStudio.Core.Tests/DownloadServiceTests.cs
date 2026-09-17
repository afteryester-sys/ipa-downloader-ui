using IPAStudio.Core.Services;
using Xunit;

namespace IPAStudio.Core.Tests;

public sealed class DownloadServiceTests
{
    [Theory]
    [InlineData("unexpected response: empty songList")]
    [InlineData("download failed with FailureType 5002")]
    public void DescribeStoreFailure_ClassifiesBrokenLegacyBackend(string output)
    {
        var expected = DownloadService.DescribeStoreFailure("unexpected response: empty songList");

        Assert.Equal(expected, DownloadService.DescribeStoreFailure(output));
    }
}
