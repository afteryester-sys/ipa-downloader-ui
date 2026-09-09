using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using IPAStudio.Core.Localization;
using IPAStudio.Core.Models;

namespace IPAStudio.Core.Services;

public sealed class FirmwareDownloadService
{
    private const int CurrentManifestVersion = 2;
    private const string ManifestSuffix = ".download.json";
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _manifestGate = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _destinationGates =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public FirmwareDownloadService(HttpClient http) => _http = http;

    public static string BuildFileName(string deviceName, string version)
    {
        var os = deviceName.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iPadOS" : "iOS";
        var raw = $"Apple {deviceName} {os} {version}.ipsw";
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(raw.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
    }

    public void DeleteTemporaryFiles(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination)) return;
        var fullDestination = Path.GetFullPath(destination);
        var folder = Path.GetDirectoryName(fullDestination);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        foreach (var path in Directory.EnumerateFiles(folder, Path.GetFileName(fullDestination) + ".*"))
        {
            if (!path.EndsWith(ManifestSuffix, StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(ManifestSuffix + ".tmp", StringComparison.OrdinalIgnoreCase)
                && !path.Contains(".part", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith(".assembling", StringComparison.OrdinalIgnoreCase))
                continue;

            TryDelete(path);
        }
    }

    public void CleanupInvalidTemporaryFiles(string folder, TimeSpan? maxAge = null)
    {
        if (!Directory.Exists(folder)) return;
        var cutoff = DateTime.UtcNow - (maxAge ?? TimeSpan.FromDays(14));

        foreach (var manifestPath in Directory.EnumerateFiles(folder, "*" + ManifestSuffix))
        {
            try
            {
                var destination = manifestPath[..^ManifestSuffix.Length];
                var manifest = JsonSerializer.Deserialize<FirmwareDownloadManifest>(
                    File.ReadAllText(manifestPath), JsonOptions);
                if (manifest is null || !HasValidSegmentLayout(manifest, destination))
                {
                    if (File.GetLastWriteTimeUtc(manifestPath) < cutoff)
                        DeleteTemporaryFiles(destination);
                    continue;
                }

                if (File.GetLastWriteTimeUtc(manifestPath) >= cutoff) continue;
                foreach (var segment in manifest.Segments)
                {
                    var expected = segment.End - segment.Start + 1;
                    if (File.Exists(segment.PartPath) && new FileInfo(segment.PartPath).Length > expected)
                        TryDelete(segment.PartPath);
                }
            }
            catch
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(manifestPath) < cutoff)
                        DeleteTemporaryFiles(manifestPath[..^ManifestSuffix.Length]);
                }
                catch { }
            }
        }

        foreach (var path in Directory.EnumerateFiles(folder, "*.assembling"))
            try { if (File.GetLastWriteTimeUtc(path) < cutoff) TryDelete(path); } catch { }

        foreach (var path in Directory.EnumerateFiles(folder, "*.part*"))
        {
            try
            {
                var marker = path.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
                if (marker > 0
                    && !File.Exists(path[..marker] + ManifestSuffix)
                    && File.GetLastWriteTimeUtc(path) < cutoff)
                    TryDelete(path);
            }
            catch { }
        }
    }

    public IReadOnlyList<FirmwarePendingDownload> FindPendingDownloads(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return Array.Empty<FirmwarePendingDownload>();

        CleanupInvalidTemporaryFiles(folder);
        var pending = new List<FirmwarePendingDownload>();
        foreach (var manifestPath in Directory.EnumerateFiles(folder, "*" + ManifestSuffix))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<FirmwareDownloadManifest>(
                    File.ReadAllText(manifestPath), JsonOptions);
                var destination = manifestPath[..^ManifestSuffix.Length];
                if (manifest is null
                    || manifest.FormatVersion > CurrentManifestVersion
                    || !HasValidSegmentLayout(manifest, destination))
                    continue;

                var downloaded = manifest.Segments.Sum(segment =>
                {
                    var expected = segment.End - segment.Start + 1;
                    var onDisk = File.Exists(segment.PartPath) ? new FileInfo(segment.PartPath).Length : 0;
                    return Math.Clamp(onDisk, 0, expected);
                });

                pending.Add(new FirmwarePendingDownload(
                    manifestPath,
                    destination,
                    Path.GetFileName(destination),
                    manifest.Url,
                    manifest.Sha1,
                    manifest.Md5,
                    manifest.ExpectedLength,
                    downloaded,
                    manifest.DeviceIdentifier,
                    manifest.DeviceName,
                    manifest.FirmwareVersion,
                    manifest.BuildId));
            }
            catch { }
        }

        return pending.OrderBy(item => item.FileName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task<string> ResumeAsync(
        FirmwarePendingDownload pending,
        int segmentCount,
        IProgress<FirmwareDownloadProgress>? progress,
        CancellationToken ct)
    {
        var device = new FirmwareDevice
        {
            Identifier = pending.DeviceIdentifier ?? "",
            Name = string.IsNullOrWhiteSpace(pending.DeviceName) ? pending.FileName : pending.DeviceName,
        };
        var firmware = new FirmwareRelease
        {
            Identifier = device.Identifier,
            Version = pending.FirmwareVersion ?? "",
            BuildId = pending.BuildId ?? "",
            Url = pending.Url,
            Sha1 = pending.Sha1,
            Md5 = pending.Md5,
            FileSize = pending.Total,
        };
        return DownloadToPathAsync(device, firmware, pending.DestinationPath, segmentCount, progress, ct);
    }

    public Task<string> DownloadAsync(
        FirmwareDevice device,
        FirmwareRelease firmware,
        string folder,
        int segmentCount,
        IProgress<FirmwareDownloadProgress>? progress,
        CancellationToken ct) =>
        DownloadToPathAsync(
            device,
            firmware,
            Path.Combine(folder, BuildFileName(device.Name, firmware.Version)),
            segmentCount,
            progress,
            ct);

    public async Task<string> DownloadToPathAsync(
        FirmwareDevice device,
        FirmwareRelease firmware,
        string destination,
        int segmentCount,
        IProgress<FirmwareDownloadProgress>? progress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(firmware.Url))
            throw new ArgumentException("Firmware URL is required.", nameof(firmware));
        if (string.IsNullOrWhiteSpace(destination))
            throw new ArgumentException("Destination path is required.", nameof(destination));

        var fullDestination = Path.GetFullPath(destination);
        var gate = _destinationGates.GetOrAdd(fullDestination, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await DownloadCoreAsync(device, firmware, fullDestination, segmentCount, progress, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> DownloadCoreAsync(
        FirmwareDevice device,
        FirmwareRelease firmware,
        string destination,
        int segmentCount,
        IProgress<FirmwareDownloadProgress>? progress,
        CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("The firmware destination has no parent folder.");
        Directory.CreateDirectory(folder);
        segmentCount = Math.Clamp(segmentCount, 1, 8);
        var manifestPath = destination + ManifestSuffix;

        CleanupInvalidTemporaryFiles(folder);
        var hasCatalogHash = !string.IsNullOrWhiteSpace(firmware.Sha1)
            || !string.IsNullOrWhiteSpace(firmware.Md5);
        if (firmware.FileSize > 0
            && hasCatalogHash
            && File.Exists(destination)
            && await IsFileValidAsync(
                    destination, firmware.FileSize, firmware.Sha1, firmware.Md5, ct)
                .ConfigureAwait(false))
        {
            DeleteTemporaryFiles(destination);
            progress?.Report(new FirmwareDownloadProgress(firmware.FileSize, firmware.FileSize, 0));
            return destination;
        }

        using var probe = await ProbeAsync(firmware.Url, ct).ConfigureAwait(false);
        probe.EnsureSuccessStatusCode();
        var length = probe.Content.Headers.ContentRange?.Length
            ?? probe.Content.Headers.ContentLength
            ?? firmware.FileSize;
        if (length <= 0) throw new InvalidDataException(Loc.Get("L.Firmware.Error.NoSize"));

        if (File.Exists(destination)
            && await IsFileValidAsync(destination, length, firmware.Sha1, firmware.Md5, ct).ConfigureAwait(false))
        {
            DeleteTemporaryFiles(destination);
            progress?.Report(new FirmwareDownloadProgress(length, length, 0));
            return destination;
        }

        var acceptsRanges = probe.StatusCode == HttpStatusCode.PartialContent
            || probe.Headers.AcceptRanges.Any(value => value.Equals("bytes", StringComparison.OrdinalIgnoreCase));
        if (!acceptsRanges) segmentCount = 1;

        var manifest = await LoadCompatibleManifestAsync(
                manifestPath, device, firmware, destination, length, probe, ct)
            .ConfigureAwait(false);
        if (manifest is null)
        {
            // A part file without a compatible manifest has no trustworthy URL, range layout,
            // validator or hash context. Never splice those bytes into a fresh download.
            DeleteTemporaryFiles(destination);
            manifest = CreateManifest(device, firmware, destination, length, segmentCount, probe);
        }

        if (!acceptsRanges && (manifest.Segments.Count != 1 || manifest.Segments[0].Downloaded > 0))
        {
            ResetTemporarySegments(destination, manifest);
            manifest = CreateManifest(device, firmware, destination, length, 1, probe);
        }

        await SaveManifestAsync(manifestPath, manifest, ct).ConfigureAwait(false);

        var initialBytes = manifest.Segments.Sum(segment =>
            Math.Min(segment.Downloaded, segment.End - segment.Start + 1));
        progress?.Report(new FirmwareDownloadProgress(initialBytes, length, 0));

        var started = Stopwatch.StartNew();
        long sessionBytes = 0;

        try
        {
            await Parallel.ForEachAsync(manifest.Segments, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(segmentCount, manifest.Segments.Count),
                CancellationToken = ct,
            }, async (segment, token) =>
            {
                var expected = segment.End - segment.Start + 1;
                var existing = File.Exists(segment.PartPath) ? new FileInfo(segment.PartPath).Length : 0;
                if (existing > expected)
                {
                    TryDelete(segment.PartPath);
                    existing = 0;
                }

                segment.Downloaded = existing;
                segment.IsComplete = existing == expected;
                if (segment.IsComplete) return;

                var requestedStart = segment.Start + existing;
                using var response = await SendWithReconnectAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, firmware.Url);
                    if (acceptsRanges)
                    {
                        request.Headers.Range = new RangeHeaderValue(requestedStart, segment.End);
                        if (!string.IsNullOrWhiteSpace(manifest.ETag))
                            request.Headers.TryAddWithoutValidation("If-Range", manifest.ETag);
                        else if (manifest.LastModified.HasValue)
                            request.Headers.TryAddWithoutValidation("If-Range", manifest.LastModified.Value.ToString("R"));
                    }
                    return request;
                }, token).ConfigureAwait(false);

                if (acceptsRanges)
                {
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                    {
                        if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
                            throw new RemoteFirmwareChangedException("The firmware validator changed while resuming.");
                        response.EnsureSuccessStatusCode();
                        throw new InvalidDataException(Loc.Get("L.Firmware.Error.RangeUnsupported"));
                    }

                    ValidateContentRange(response, requestedStart, segment.End, length);
                    ValidateResponseValidator(response, manifest);
                }

                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var output = new FileStream(
                    segment.PartPath,
                    existing == 0 ? FileMode.Create : FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var buffer = new byte[1024 * 1024];
                var lastSave = Stopwatch.StartNew();
                while (segment.Downloaded < expected)
                {
                    var read = await input.ReadAsync(
                            buffer.AsMemory(0, (int)Math.Min(buffer.Length, expected - segment.Downloaded)), token)
                        .ConfigureAwait(false);
                    if (read == 0) break;

                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    segment.Downloaded += read;
                    Interlocked.Add(ref sessionBytes, read);

                    var downloaded = initialBytes + Interlocked.Read(ref sessionBytes);
                    var speed = started.Elapsed.TotalSeconds > 0
                        ? Interlocked.Read(ref sessionBytes) / started.Elapsed.TotalSeconds
                        : 0;
                    progress?.Report(new FirmwareDownloadProgress(downloaded, length, speed));

                    if (lastSave.ElapsedMilliseconds >= 1000)
                    {
                        await SaveManifestAsync(manifestPath, manifest, token).ConfigureAwait(false);
                        lastSave.Restart();
                    }
                }

                await output.FlushAsync(token).ConfigureAwait(false);
                if (segment.Downloaded != expected)
                    throw new EndOfStreamException(Loc.Get("L.Firmware.Error.IncompleteSegment"));

                segment.IsComplete = true;
                await SaveManifestAsync(manifestPath, manifest, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (RemoteFirmwareChangedException ex)
        {
            ResetTemporarySegments(destination, manifest);
            await SaveManifestAsync(manifestPath, manifest, CancellationToken.None).ConfigureAwait(false);
            throw new IOException(Loc.Get("L.Firmware.Error.RemoteChanged"), ex);
        }

        await SaveManifestAsync(manifestPath, manifest, ct).ConfigureAwait(false);
        var temp = destination + ".assembling";
        await using (var output = new FileStream(
                         temp,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            foreach (var segment in manifest.Segments.OrderBy(item => item.Start))
            {
                var expected = segment.End - segment.Start + 1;
                if (!File.Exists(segment.PartPath) || new FileInfo(segment.PartPath).Length != expected)
                    throw new InvalidDataException(Loc.Get("L.Firmware.Error.InvalidSegment"));

                await using var input = new FileStream(
                    segment.PartPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
        }

        if (!await IsFileValidAsync(temp, length, firmware.Sha1, firmware.Md5, ct).ConfigureAwait(false))
        {
            TryDelete(temp);
            ResetTemporarySegments(destination, manifest);
            await SaveManifestAsync(manifestPath, manifest, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidDataException(GetHashFailureMessage(firmware));
        }

        File.Move(temp, destination, true);
        foreach (var segment in manifest.Segments) TryDelete(segment.PartPath);
        TryDelete(manifestPath);
        TryDelete(manifestPath + ".tmp");
        progress?.Report(new FirmwareDownloadProgress(length, length, 0));
        return destination;
    }

    private async Task<HttpResponseMessage> ProbeAsync(string url, CancellationToken ct)
    {
        var response = await SendWithReconnectAsync(() => new HttpRequestMessage(HttpMethod.Head, url), ct)
            .ConfigureAwait(false);
        if (response.StatusCode is not HttpStatusCode.MethodNotAllowed and not HttpStatusCode.NotImplemented)
            return response;

        response.Dispose();
        return await SendWithReconnectAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            return request;
        }, ct).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendWithReconnectAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan? serverDelay = null;
            try
            {
                using var request = requestFactory();
                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                var retryableStatus = response.StatusCode is HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests
                    || (int)response.StatusCode >= 500;
                if (!retryableStatus) return response;

                serverDelay = response.Headers.RetryAfter?.Delta;
                response.Dispose();
            }
            catch (Exception ex) when ((ex is HttpRequestException or IOException or TaskCanceledException)
                                       && !ct.IsCancellationRequested)
            {
                last = ex;
            }

            if (attempt == 7) break;
            var delay = serverDelay is { } requested && requested > TimeSpan.Zero
                ? TimeSpan.FromSeconds(Math.Min(30, requested.TotalSeconds))
                : TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }

        throw new HttpRequestException(Loc.Get("L.Firmware.Error.ServerUnavailable"), last);
    }

    private static FirmwareDownloadManifest CreateManifest(
        FirmwareDevice device,
        FirmwareRelease firmware,
        string destination,
        long length,
        int count,
        HttpResponseMessage probe)
    {
        var manifest = new FirmwareDownloadManifest
        {
            FormatVersion = CurrentManifestVersion,
            Url = firmware.Url,
            DestinationPath = destination,
            ExpectedLength = length,
            Sha1 = firmware.Sha1,
            Md5 = firmware.Md5,
            ETag = probe.Headers.ETag?.Tag,
            LastModified = probe.Content.Headers.LastModified,
            DeviceIdentifier = device.Identifier,
            DeviceName = device.Name,
            FirmwareVersion = firmware.Version,
            BuildId = firmware.BuildId,
        };

        var partSize = (long)Math.Ceiling(length / (double)count);
        for (var i = 0; i < count; i++)
        {
            var start = i * partSize;
            var end = Math.Min(length - 1, start + partSize - 1);
            if (start <= end)
            {
                manifest.Segments.Add(new FirmwareSegment
                {
                    Start = start,
                    End = end,
                    PartPath = destination + $".part{i}",
                });
            }
        }

        return manifest;
    }

    private async Task<FirmwareDownloadManifest?> LoadCompatibleManifestAsync(
        string manifestPath,
        FirmwareDevice device,
        FirmwareRelease firmware,
        string destination,
        long length,
        HttpResponseMessage probe,
        CancellationToken ct)
    {
        if (!File.Exists(manifestPath)) return null;

        FirmwareDownloadManifest? manifest;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync<FirmwareDownloadManifest>(stream, JsonOptions, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            DeleteTemporaryFiles(destination);
            return null;
        }

        var compatible = manifest is not null
            && manifest.FormatVersion <= CurrentManifestVersion
            && string.Equals(manifest.Url, firmware.Url, StringComparison.Ordinal)
            && PathsEqual(manifest.DestinationPath, destination)
            && manifest.ExpectedLength == length
            && HashesCompatible(manifest.Sha1, firmware.Sha1)
            && HashesCompatible(manifest.Md5, firmware.Md5)
            && HasValidSegmentLayout(manifest, destination)
            && ValidatorsCompatible(manifest, probe);

        if (!compatible)
        {
            DeleteTemporaryFiles(destination);
            return null;
        }

        manifest!.FormatVersion = CurrentManifestVersion;
        manifest.DestinationPath = destination;
        manifest.Sha1 = firmware.Sha1 ?? manifest.Sha1;
        manifest.Md5 = firmware.Md5 ?? manifest.Md5;
        manifest.ETag = probe.Headers.ETag?.Tag ?? manifest.ETag;
        manifest.LastModified = probe.Content.Headers.LastModified ?? manifest.LastModified;
        manifest.DeviceIdentifier = string.IsNullOrWhiteSpace(device.Identifier)
            ? manifest.DeviceIdentifier
            : device.Identifier;
        manifest.DeviceName = string.IsNullOrWhiteSpace(device.Name) ? manifest.DeviceName : device.Name;
        manifest.FirmwareVersion = string.IsNullOrWhiteSpace(firmware.Version)
            ? manifest.FirmwareVersion
            : firmware.Version;
        manifest.BuildId = string.IsNullOrWhiteSpace(firmware.BuildId) ? manifest.BuildId : firmware.BuildId;

        foreach (var segment in manifest.Segments)
        {
            var expected = segment.End - segment.Start + 1;
            var onDisk = File.Exists(segment.PartPath) ? new FileInfo(segment.PartPath).Length : 0;
            if (onDisk > expected)
            {
                TryDelete(segment.PartPath);
                onDisk = 0;
            }
            segment.Downloaded = onDisk;
            segment.IsComplete = onDisk == expected;
        }

        return manifest;
    }

    private async Task SaveManifestAsync(
        string path,
        FirmwareDownloadManifest manifest,
        CancellationToken ct)
    {
        await _manifestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = CloneManifest(manifest);
            var temp = path + ".tmp";
            await using (var stream = new FileStream(
                             temp,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temp, path, true);
        }
        finally
        {
            _manifestGate.Release();
        }
    }

    private static FirmwareDownloadManifest CloneManifest(FirmwareDownloadManifest source) => new()
    {
        FormatVersion = source.FormatVersion,
        Url = source.Url,
        DestinationPath = source.DestinationPath,
        ExpectedLength = source.ExpectedLength,
        ETag = source.ETag,
        LastModified = source.LastModified,
        Sha1 = source.Sha1,
        Md5 = source.Md5,
        DeviceIdentifier = source.DeviceIdentifier,
        DeviceName = source.DeviceName,
        FirmwareVersion = source.FirmwareVersion,
        BuildId = source.BuildId,
        Segments = source.Segments.Select(segment => new FirmwareSegment
        {
            Start = segment.Start,
            End = segment.End,
            Downloaded = segment.Downloaded,
            IsComplete = segment.IsComplete,
            PartPath = segment.PartPath,
        }).ToList(),
    };

    private static bool HasValidSegmentLayout(FirmwareDownloadManifest manifest, string destination)
    {
        if (manifest.ExpectedLength <= 0 || manifest.Segments.Count is < 1 or > 8) return false;
        if (!PathsEqual(manifest.DestinationPath, destination)) return false;

        var ordered = manifest.Segments.OrderBy(segment => segment.Start).ToList();
        long next = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var segment = ordered[i];
            if (segment.Start != next || segment.End < segment.Start || segment.End >= manifest.ExpectedLength)
                return false;
            if (!PathsEqual(segment.PartPath, destination + $".part{i}")) return false;
            next = segment.End + 1;
        }

        return next == manifest.ExpectedLength;
    }

    private static bool ValidatorsCompatible(
        FirmwareDownloadManifest manifest,
        HttpResponseMessage probe)
    {
        var currentEtag = probe.Headers.ETag?.Tag;
        if (!string.IsNullOrWhiteSpace(manifest.ETag)
            && !string.IsNullOrWhiteSpace(currentEtag)
            && !string.Equals(manifest.ETag, currentEtag, StringComparison.Ordinal))
            return false;

        var currentModified = probe.Content.Headers.LastModified;
        if (string.IsNullOrWhiteSpace(manifest.ETag)
            && string.IsNullOrWhiteSpace(currentEtag)
            && manifest.LastModified.HasValue
            && currentModified.HasValue
            && manifest.LastModified.Value != currentModified.Value)
            return false;

        return true;
    }

    private static void ValidateContentRange(
        HttpResponseMessage response,
        long requestedStart,
        long requestedEnd,
        long expectedLength)
    {
        var range = response.Content.Headers.ContentRange;
        if (range?.From != requestedStart
            || !range.To.HasValue
            || range.To.Value > requestedEnd
            || range.To.Value < requestedStart
            || (range.Length.HasValue && range.Length.Value != expectedLength))
            throw new InvalidDataException(Loc.Get("L.Firmware.Error.ContentRange"));
    }

    private static void ValidateResponseValidator(
        HttpResponseMessage response,
        FirmwareDownloadManifest manifest)
    {
        var responseEtag = response.Headers.ETag?.Tag;
        if (!string.IsNullOrWhiteSpace(manifest.ETag)
            && !string.IsNullOrWhiteSpace(responseEtag)
            && !string.Equals(manifest.ETag, responseEtag, StringComparison.Ordinal))
            throw new RemoteFirmwareChangedException("The firmware ETag changed during download.");

        var responseModified = response.Content.Headers.LastModified;
        if (string.IsNullOrWhiteSpace(manifest.ETag)
            && manifest.LastModified.HasValue
            && responseModified.HasValue
            && manifest.LastModified.Value != responseModified.Value)
            throw new RemoteFirmwareChangedException("The firmware modification date changed during download.");
    }

    private static async Task<bool> IsFileValidAsync(
        string path,
        long expectedLength,
        string? sha1,
        string? md5,
        CancellationToken ct)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expectedLength) return false;

        if (!string.IsNullOrWhiteSpace(sha1))
        {
            await using var input = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA1.HashDataAsync(input, ct).ConfigureAwait(false));
            return actual.Equals(NormalizeHash(sha1), StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(md5))
        {
            await using var input = File.OpenRead(path);
            var actual = Convert.ToHexString(await MD5.HashDataAsync(input, ct).ConfigureAwait(false));
            return actual.Equals(NormalizeHash(md5), StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static string GetHashFailureMessage(FirmwareRelease firmware) =>
        !string.IsNullOrWhiteSpace(firmware.Sha1)
            ? Loc.Get("L.Firmware.Error.Sha1Mismatch")
            : !string.IsNullOrWhiteSpace(firmware.Md5)
                ? Loc.Get("L.Firmware.Error.Md5Mismatch")
                : Loc.Get("L.Firmware.Error.SizeMismatch");

    private static bool HashesCompatible(string? saved, string? current) =>
        string.IsNullOrWhiteSpace(saved)
        || string.IsNullOrWhiteSpace(current)
        || NormalizeHash(saved).Equals(NormalizeHash(current), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHash(string value) =>
        value.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void ResetTemporarySegments(
        string destination,
        FirmwareDownloadManifest manifest)
    {
        var folder = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            foreach (var path in Directory.EnumerateFiles(folder, Path.GetFileName(destination) + ".part*"))
                TryDelete(path);
        }

        TryDelete(destination + ".assembling");
        foreach (var segment in manifest.Segments)
        {
            segment.Downloaded = 0;
            segment.IsComplete = false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class RemoteFirmwareChangedException(string message) : IOException(message);
}
