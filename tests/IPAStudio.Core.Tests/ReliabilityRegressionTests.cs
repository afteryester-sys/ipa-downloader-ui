using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

namespace IPAStudio.Core.Tests;

public sealed class DownloadServiceFileNameTests
{
    [Theory]
    [InlineData("My App", "My App.ipa")]
    [InlineData("My App.ipa", "My App.ipa")]
    [InlineData("My App.IPA.ipa", "My App.ipa")]
    [InlineData("My App.ipa. ", "My App.ipa")]
    [InlineData("Приложение 2026", "Приложение 2026.ipa")]
    [InlineData("bad:name?", "bad_name_.ipa")]
    public void NormalizesSafeIpaFileNames(string input, string expected)
    {
        var valid = DownloadService.TryNormalizeIpaFileName(input, out var normalized);

        Assert.True(valid);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("folder/app")]
    [InlineData("folder\\app")]
    [InlineData("C:\\app")]
    [InlineData("CON")]
    [InlineData("LPT9.txt")]
    public void RejectsPathsEmptyAndReservedNames(string? input)
    {
        var valid = DownloadService.TryNormalizeIpaFileName(input, out var normalized);

        Assert.False(valid);
        Assert.Empty(normalized);
    }

    [Fact]
    public void CapsLongFileNamesBeforeAddingExtension()
    {
        var valid = DownloadService.TryNormalizeIpaFileName(new string('a', 260), out var normalized);

        Assert.True(valid);
        Assert.Equal(224, normalized.Length);
        Assert.EndsWith(".ipa", normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyFirmwareSubscriptionsRemainEnabledAfterDeserialization()
    {
        var subscription = JsonSerializer.Deserialize<FirmwareSubscription>(
            "{\"Identifier\":\"iPhone17,1\",\"DeviceName\":\"iPhone 16 Pro\"}");

        Assert.NotNull(subscription);
        Assert.True(subscription.AutoUpdateEnabled);
    }

    [Fact]
    public async Task ItunesCopyPublishesCustomNameAndKeepsBothFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "IPAStudio.Core.Tests", Guid.NewGuid().ToString("N"));
        var destinationFolder = Path.Combine(root, "destination");
        var source = Path.Combine(root, "source.ipa");
        var payload = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(source, payload);

        try
        {
            var service = new ItunesLegacyService();
            var first = await service.CopyOutAsync(source, destinationFolder, "Моё приложение");
            var second = await service.CopyOutAsync(source, destinationFolder, "Моё приложение.ipa");

            Assert.Equal("Моё приложение.ipa", Path.GetFileName(first));
            Assert.Equal("Моё приложение (2).ipa", Path.GetFileName(second));
            Assert.Equal(payload, await File.ReadAllBytesAsync(first));
            Assert.Equal(payload, await File.ReadAllBytesAsync(second));
            Assert.Empty(Directory.EnumerateFiles(destinationFolder, "*.copying"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledItunesCopyLeavesNoPublishedOrTemporaryFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "IPAStudio.Core.Tests", Guid.NewGuid().ToString("N"));
        var destinationFolder = Path.Combine(root, "destination");
        var source = Path.Combine(root, "source.ipa");
        Directory.CreateDirectory(root);
        await File.WriteAllBytesAsync(source, new byte[1024 * 1024]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            var service = new ItunesLegacyService();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.CopyOutAsync(source, destinationFolder, "Cancelled", cts.Token));

            Assert.Empty(Directory.EnumerateFiles(destinationFolder));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class FirmwareDownloadServiceTests : IDisposable
{
    private static readonly FirmwareDevice Device = new()
    {
        Identifier = "iPhone17,1",
        Name = "iPhone 16 Pro",
    };

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "IPAStudio.Core.Tests",
        Guid.NewGuid().ToString("N"));

    public FirmwareDownloadServiceTests() => Directory.CreateDirectory(_folder);

    [Fact]
    public async Task ResumesIncompleteSegmentFromItsActualLength()
    {
        var payload = CreatePayload(257);
        using var server = new FirmwareServerHandler(payload);
        using var http = new HttpClient(server);
        var service = new FirmwareDownloadService(http);
        var destination = Destination();
        var release = Release(server.Url, payload);
        var partial = payload[..73];
        await WriteManifestAsync(destination, release, server.ETag, partial);

        var result = await service.DownloadToPathAsync(
            Device, release, destination, 1, null, CancellationToken.None);

        Assert.Equal(destination, result);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(new long[] { partial.Length }, server.RangeStarts);
        Assert.Contains(server.ETag, server.IfRangeValues);
        Assert.False(File.Exists(destination + ".part0"));
        Assert.False(File.Exists(destination + ".download.json"));
    }

    [Fact]
    public async Task ResumesLegacyManifestWithoutNewMetadataFields()
    {
        var payload = CreatePayload(239);
        using var server = new FirmwareServerHandler(payload);
        using var http = new HttpClient(server);
        var service = new FirmwareDownloadService(http);
        var destination = Destination();
        var release = Release(server.Url, payload);
        var partPath = destination + ".part0";
        var partial = payload[..61];
        await File.WriteAllBytesAsync(partPath, partial);
        var legacyManifest = new
        {
            Url = release.Url,
            DestinationPath = destination,
            ExpectedLength = release.FileSize,
            ETag = server.ETag,
            Sha1 = release.Sha1,
            Segments = new[]
            {
                new
                {
                    Start = 0L,
                    End = release.FileSize - 1,
                    Downloaded = partial.LongLength,
                    PartPath = partPath,
                },
            },
        };
        await File.WriteAllTextAsync(
            destination + ".download.json",
            JsonSerializer.Serialize(legacyManifest));

        var pending = Assert.Single(service.FindPendingDownloads(_folder));
        Assert.Null(pending.DeviceIdentifier);
        Assert.Null(pending.Md5);
        await service.ResumeAsync(pending, 1, null, CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(new long[] { partial.Length }, server.RangeStarts);
    }

    [Fact]
    public async Task RepairsOnlyAnOversizedSegmentBeforeDownloadingItAgain()
    {
        var payload = CreatePayload(193);
        using var server = new FirmwareServerHandler(payload);
        using var http = new HttpClient(server);
        var service = new FirmwareDownloadService(http);
        var destination = Destination();
        var release = Release(server.Url, payload);
        await WriteManifestAsync(destination, release, server.ETag, new byte[payload.Length + 1]);

        await service.DownloadToPathAsync(
            Device, release, destination, 1, null, CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(new long[] { 0 }, server.RangeStarts);
    }

    [Fact]
    public async Task DiscardsOrphanPartWithoutATrustedManifest()
    {
        var payload = CreatePayload(199);
        using var server = new FirmwareServerHandler(payload);
        using var http = new HttpClient(server);
        var service = new FirmwareDownloadService(http);
        var destination = Destination();
        var release = Release(server.Url, payload);
        await File.WriteAllBytesAsync(destination + ".part0", payload[..47]);

        await service.DownloadToPathAsync(
            Device, release, destination, 1, null, CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(new long[] { 0 }, server.RangeStarts);
    }

    [Fact]
    public async Task RestartsSavedSegmentsWhenTheRemoteEtagChanges()
    {
        var payload = CreatePayload(211);
        using var server = new FirmwareServerHandler(payload) { ETag = "\"new-etag\"" };
        using var http = new HttpClient(server);
        var service = new FirmwareDownloadService(http);
        var destination = Destination();
        var release = Release(server.Url, payload);
        await WriteManifestAsync(destination, release, "\"old-etag\"", payload[..41]);

        await service.DownloadToPathAsync(
            Device, release, destination, 1, null, CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(new long[] { 0 }, server.RangeStarts);
    }

    [Fact]
    public async Task HashFailureResetsACompleteButCorruptSegmentForTheNextResume()
    {
        var payload = CreatePayload(181);
        using var server = new FirmwareServerHandler(payload);
        using var http = new HttpClient(server);
        var service = new FirmwareDownloadService(http);
        var destination = Destination();
        var release = Release(server.Url, payload);
        var corrupt = Enumerable.Repeat((byte)0xFF, payload.Length).ToArray();
        await WriteManifestAsync(destination, release, server.ETag, corrupt, isComplete: true);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadToPathAsync(
                Device, release, destination, 1, null, CancellationToken.None));

        Assert.NotEmpty(error.Message);
        Assert.Empty(server.RangeStarts);
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(destination + ".part0"));

        var pending = Assert.Single(service.FindPendingDownloads(_folder));
        Assert.Equal(0, pending.Downloaded);

        await service.ResumeAsync(pending, 1, null, CancellationToken.None);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(new long[] { 0 }, server.RangeStarts);
    }

    [Fact]
    public async Task ReusesAnExistingFileWhenItsMd5Matches()
    {
        var payload = CreatePayload(167);
        using var server = new FirmwareServerHandler(payload);
        using var http = new HttpClient(server);
        var service = new FirmwareDownloadService(http);
        var destination = Destination();
        await File.WriteAllBytesAsync(destination, payload);
        await File.WriteAllBytesAsync(destination + ".part0", payload[..20]);
        var release = Release(server.Url, payload);
        release.Sha1 = null;
        release.Md5 = Convert.ToHexString(MD5.HashData(payload));
        await WriteManifestAsync(destination, release, server.ETag, payload[..20]);

        var result = await service.DownloadToPathAsync(
            Device, release, destination, 1, null, CancellationToken.None);

        Assert.Equal(destination, result);
        Assert.Equal(0, server.HeadRequestCount);
        Assert.Equal(0, server.GetRequestCount);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(destination + ".part0"));
        Assert.False(File.Exists(destination + ".download.json"));
    }

    [Fact]
    public async Task RetriesCdnRateLimitBeforeDownloadingTheRange()
    {
        var payload = CreatePayload(197);
        using var server = new FirmwareServerHandler(payload) { RateLimitedGetResponses = 1 };
        using var http = new HttpClient(server);
        var service = new FirmwareDownloadService(http);
        var destination = Destination();
        var release = Release(server.Url, payload);

        await service.DownloadToPathAsync(
            Device, release, destination, 1, null, CancellationToken.None);

        Assert.Equal(2, server.GetRequestCount);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task SerializesConcurrentDownloadsToTheSameDestination()
    {
        var payload = CreatePayload(223);
        using var server = new FirmwareServerHandler(payload) { GetDelay = TimeSpan.FromMilliseconds(50) };
        using var http = new HttpClient(server);
        var service = new FirmwareDownloadService(http);
        var destination = Destination();
        var release = Release(server.Url, payload);

        var first = service.DownloadToPathAsync(
            Device, release, destination, 1, null, CancellationToken.None);
        var second = service.DownloadToPathAsync(
            Device, release, destination, 1, null, CancellationToken.None);

        await Task.WhenAll(first, second);

        Assert.Equal(1, server.GetRequestCount);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Destination() => Path.Combine(_folder, "firmware.ipsw");

    private static FirmwareRelease Release(
        Uri url,
        byte[] payload,
        string? sha1 = null,
        string? md5 = null) => new()
    {
        Identifier = Device.Identifier,
        Version = "26.0",
        BuildId = "23A000",
        Url = url.AbsoluteUri,
        FileSize = payload.LongLength,
        Sha1 = sha1 ?? Convert.ToHexString(SHA1.HashData(payload)),
        Md5 = md5,
    };

    private static async Task WriteManifestAsync(
        string destination,
        FirmwareRelease release,
        string etag,
        byte[] part,
        bool isComplete = false)
    {
        var partPath = destination + ".part0";
        await File.WriteAllBytesAsync(partPath, part);
        var manifest = new FirmwareDownloadManifest
        {
            FormatVersion = 2,
            Url = release.Url,
            DestinationPath = destination,
            ExpectedLength = release.FileSize,
            ETag = etag,
            Sha1 = release.Sha1,
            Md5 = release.Md5,
            DeviceIdentifier = Device.Identifier,
            DeviceName = Device.Name,
            FirmwareVersion = release.Version,
            BuildId = release.BuildId,
            Segments =
            [
                new FirmwareSegment
                {
                    Start = 0,
                    End = release.FileSize - 1,
                    Downloaded = part.LongLength,
                    IsComplete = isComplete,
                    PartPath = partPath,
                },
            ],
        };

        await File.WriteAllTextAsync(
            destination + ".download.json",
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static byte[] CreatePayload(int length) =>
        Enumerable.Range(0, length).Select(index => (byte)(index * 31 % 251)).ToArray();

    private sealed class FirmwareServerHandler(byte[] payload) : HttpMessageHandler
    {
        private int _headRequestCount;
        private int _getRequestCount;

        public Uri Url { get; } = new("https://firmware.test/device.ipsw");
        public string ETag { get; set; } = "\"firmware-v1\"";
        public TimeSpan GetDelay { get; set; }
        public int RateLimitedGetResponses { get; set; }
        public int HeadRequestCount => Volatile.Read(ref _headRequestCount);
        public int GetRequestCount => Volatile.Read(ref _getRequestCount);
        public ConcurrentQueue<long> RangeStarts { get; } = new();
        public ConcurrentQueue<string> IfRangeValues { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                Interlocked.Increment(ref _headRequestCount);
                return CreateResponse(HttpStatusCode.OK, Array.Empty<byte>(), contentLength: payload.LongLength);
            }

            var requestNumber = Interlocked.Increment(ref _getRequestCount);
            if (GetDelay > TimeSpan.Zero)
                await Task.Delay(GetDelay, cancellationToken);
            if (requestNumber <= RateLimitedGetResponses)
            {
                var limited = CreateResponse(
                    HttpStatusCode.TooManyRequests,
                    Array.Empty<byte>(),
                    contentLength: 0);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
                return limited;
            }

            if (request.Headers.TryGetValues("If-Range", out var values))
            {
                foreach (var value in values) IfRangeValues.Enqueue(value);
            }

            var range = request.Headers.Range?.Ranges.SingleOrDefault();
            if (range is null)
                return CreateResponse(HttpStatusCode.OK, payload, contentLength: payload.LongLength);

            var start = range.From ?? 0;
            var end = range.To ?? payload.LongLength - 1;
            RangeStarts.Enqueue(start);
            if (start < 0 || end < start || end >= payload.LongLength)
                return CreateResponse(HttpStatusCode.RequestedRangeNotSatisfiable, Array.Empty<byte>(), payload.LongLength);

            var body = payload[(int)start..((int)end + 1)];
            var response = CreateResponse(HttpStatusCode.PartialContent, body, body.LongLength);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, payload.LongLength);
            return response;
        }

        private HttpResponseMessage CreateResponse(
            HttpStatusCode status,
            byte[] body,
            long contentLength)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(body),
            };
            response.Headers.AcceptRanges.Add("bytes");
            response.Headers.ETag = new EntityTagHeaderValue(ETag);
            response.Content.Headers.ContentLength = contentLength;
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
            return response;
        }
    }
}
