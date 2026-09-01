using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using IPAStudio.Core.Models;

namespace IPAStudio.Core.Services;

public sealed class FirmwareDownloadService
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public FirmwareDownloadService(HttpClient http) => _http = http;

    public static string BuildFileName(string deviceName, string version)
    {
        var os = deviceName.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iPadOS" : "iOS";
        var raw = $"Apple {deviceName} {os} {version}.ipsw";
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(raw.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
    }

    public async Task<string> DownloadAsync(
        FirmwareDevice device,
        FirmwareRelease firmware,
        string folder,
        int segmentCount,
        IProgress<FirmwareDownloadProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        segmentCount = Math.Clamp(segmentCount, 1, 8);
        var destination = Path.Combine(folder, BuildFileName(device.Name, firmware.Version));
        var manifestPath = destination + ".download.json";

        using var head = new HttpRequestMessage(HttpMethod.Head, firmware.Url);
        using var headResponse = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        headResponse.EnsureSuccessStatusCode();
        var length = headResponse.Content.Headers.ContentLength ?? firmware.FileSize;
        if (length <= 0) throw new InvalidDataException("The server did not report firmware size.");
        var acceptsRanges = headResponse.Headers.AcceptRanges.Contains("bytes");
        if (!acceptsRanges) segmentCount = 1;

        var manifest = await LoadCompatibleManifestAsync(manifestPath, firmware.Url, destination, length, headResponse, ct)
            .ConfigureAwait(false) ?? CreateManifest(firmware, destination, length, segmentCount, headResponse);
        if (!acceptsRanges)
        {
            // A server that cannot honor Range cannot safely resume an appended file.
            // Keep the final destination untouched, but restart its temporary segment.
            foreach (var stale in manifest.Segments)
                if (File.Exists(stale.PartPath)) File.Delete(stale.PartPath);
            manifest = CreateManifest(firmware, destination, length, 1, headResponse);
        }
        await SaveManifestAsync(manifestPath, manifest, ct).ConfigureAwait(false);

        var started = Stopwatch.StartNew();
        long initialBytes = manifest.Segments.Sum(s => Math.Min(s.Downloaded, s.End - s.Start + 1));
        long sessionBytes = 0;

        await Parallel.ForEachAsync(manifest.Segments, new ParallelOptions
        {
            MaxDegreeOfParallelism = segmentCount,
            CancellationToken = ct,
        }, async (segment, token) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(segment.PartPath)!);
            var expected = segment.End - segment.Start + 1;
            var existing = File.Exists(segment.PartPath) ? new FileInfo(segment.PartPath).Length : 0;
            segment.Downloaded = Math.Clamp(existing, 0, expected);
            if (segment.Downloaded >= expected) return;

            using var request = new HttpRequestMessage(HttpMethod.Get, firmware.Url);
            request.Headers.Range = new RangeHeaderValue(segment.Start + segment.Downloaded, segment.End);
            if (!string.IsNullOrWhiteSpace(manifest.ETag))
                request.Headers.TryAddWithoutValidation("If-Range", manifest.ETag);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (segmentCount > 1 && response.StatusCode != HttpStatusCode.PartialContent)
                throw new InvalidDataException("Apple CDN did not honor the Range request; the partial file was kept.");
            response.EnsureSuccessStatusCode();

            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(segment.PartPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[1024 * 1024];
            var lastSave = Stopwatch.StartNew();
            while (segment.Downloaded < expected)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected - segment.Downloaded)), token)
                    .ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                segment.Downloaded += read;
                Interlocked.Add(ref sessionBytes, read);
                var downloaded = initialBytes + Interlocked.Read(ref sessionBytes);
                var speed = started.Elapsed.TotalSeconds > 0 ? sessionBytes / started.Elapsed.TotalSeconds : 0;
                progress?.Report(new FirmwareDownloadProgress(downloaded, length, speed));
                if (lastSave.ElapsedMilliseconds >= 1000)
                {
                    await SaveManifestAsync(manifestPath, manifest, token).ConfigureAwait(false);
                    lastSave.Restart();
                }
            }
            if (segment.Downloaded != expected) throw new EndOfStreamException("Firmware download ended before the segment was complete.");
        }).ConfigureAwait(false);

        await SaveManifestAsync(manifestPath, manifest, ct).ConfigureAwait(false);
        var temp = destination + ".assembling";
        await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        {
            foreach (var segment in manifest.Segments.OrderBy(s => s.Start))
            {
                await using var input = new FileStream(segment.PartPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
        }
        if (new FileInfo(temp).Length != length) throw new InvalidDataException("Downloaded firmware size does not match the API response.");
        if (!string.IsNullOrWhiteSpace(firmware.Sha1))
        {
            await using var input = File.OpenRead(temp);
            var actual = Convert.ToHexString(await SHA1.HashDataAsync(input, ct).ConfigureAwait(false));
            if (!actual.Equals(firmware.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Firmware SHA-1 verification failed. Partial files were kept for diagnosis.");
        }
        File.Move(temp, destination, true);
        foreach (var segment in manifest.Segments) File.Delete(segment.PartPath);
        File.Delete(manifestPath);
        progress?.Report(new FirmwareDownloadProgress(length, length, 0));
        return destination;
    }

    private static FirmwareDownloadManifest CreateManifest(FirmwareRelease fw, string path, long length, int count, HttpResponseMessage head)
    {
        var manifest = new FirmwareDownloadManifest
        {
            Url = fw.Url, DestinationPath = path, ExpectedLength = length, Sha1 = fw.Sha1,
            ETag = head.Headers.ETag?.Tag, LastModified = head.Content.Headers.LastModified,
        };
        var partSize = (long)Math.Ceiling(length / (double)count);
        for (var i = 0; i < count; i++)
        {
            var start = i * partSize;
            var end = Math.Min(length - 1, start + partSize - 1);
            if (start <= end) manifest.Segments.Add(new FirmwareSegment { Start = start, End = end, PartPath = path + $".part{i}" });
        }
        return manifest;
    }

    private static async Task<FirmwareDownloadManifest?> LoadCompatibleManifestAsync(string path, string url, string destination, long length, HttpResponseMessage head, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var value = await JsonSerializer.DeserializeAsync<FirmwareDownloadManifest>(stream, JsonOptions, ct).ConfigureAwait(false);
            if (value is null || value.Url != url || value.DestinationPath != destination || value.ExpectedLength != length) return null;
            var currentEtag = head.Headers.ETag?.Tag;
            if (!string.IsNullOrEmpty(value.ETag) && !string.Equals(value.ETag, currentEtag, StringComparison.Ordinal)) return null;
            return value;
        }
        catch { return null; }
    }

    private async Task SaveManifestAsync(string path, FirmwareDownloadManifest manifest, CancellationToken ct)
    {
        await _manifestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var temp = path + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
                await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, ct).ConfigureAwait(false);
            File.Move(temp, path, true);
        }
        finally
        {
            _manifestGate.Release();
        }
    }
}
