using IPAStudio.Core.Services;
using Xunit;

namespace IPAStudio.Core.Tests;

public sealed class InstallResultTests
{
    [Fact]
    public void MissingDeviceConfirmation_IsNeverSuccess()
    {
        var result = InstallResult.Missing("bundle absent");

        Assert.False(result.Success);
        Assert.True(result.NotOnDevice);
    }

    [Fact]
    public void ValidatedCompletion_IsSuccess()
    {
        var result = InstallResult.Ok();

        Assert.True(result.Success);
        Assert.False(result.NotOnDevice);
        Assert.False(result.LicenseMissing);
        Assert.False(result.AccountMismatch);
    }
}
