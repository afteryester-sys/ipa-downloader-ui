using System.IO.Compression;
using System.Text;
using IPAStudio.Core.Tools;
using Xunit;

namespace IPAStudio.Core.Tests;

public sealed class IpaLicenseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ipastudio-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Inspect_AcceptsCompleteLicensedArchive()
    {
        var path = CreateArchive(
            ("iTunesMetadata.plist", "<plist><dict><key>appleId</key><string>owner@example.com</string></dict></plist>"),
            ("Payload/Test.app/SC_Info/Manifest.plist", "SC_Info/Test.sinf"),
            ("Payload/Test.app/SC_Info/Test.sinf", "license"));

        var report = IpaLicense.Inspect(path);

        Assert.True(report.IsInstallable);
        Assert.Equal(1, report.MainAppCount);
        Assert.Equal("owner@example.com", report.AppleId);
    }

    [Fact]
    public void Inspect_RejectsArchiveWithoutFairPlayPayload()
    {
        var path = CreateArchive(
            ("iTunesMetadata.plist", "<plist />"),
            ("Payload/Test.app/SC_Info/Manifest.plist", "SC_Info/Test.sinf"));

        var report = IpaLicense.Inspect(path);

        Assert.False(report.IsInstallable);
        Assert.True(report.IsDefinitelyUnlicensed);
    }

    [Fact]
    public void Inspect_RejectsTruncatedZip()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "truncated.ipa");
        File.WriteAllBytes(path, "PK incomplete central directory"u8.ToArray());

        var report = IpaLicense.Inspect(path);

        Assert.False(report.IsInstallable);
        Assert.NotNull(report.ReadError);
    }

    [Fact]
    public void Inspect_RejectsMultiplePrimaryApps()
    {
        var path = CreateArchive(
            ("iTunesMetadata.plist", "<plist />"),
            ("Payload/One.app/SC_Info/Manifest.plist", "SC_Info/One.sinf"),
            ("Payload/One.app/SC_Info/One.sinf", "license"),
            ("Payload/Two.app/Info.plist", "plist"));

        var report = IpaLicense.Inspect(path);

        Assert.False(report.IsInstallable);
        Assert.Equal(2, report.MainAppCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string CreateArchive(params (string Path, string Content)[] entries)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.ipa");

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = zip.CreateEntry(item.Path);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(item.Content);
        }

        return path;
    }
}
