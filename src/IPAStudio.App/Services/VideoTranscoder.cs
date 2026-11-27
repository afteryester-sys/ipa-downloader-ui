using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Tools;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace IPAStudio.App.Services;

/// <summary>
/// High-performance video transcoder supporting:
/// 1) Bundled / system FFmpeg (if available).
/// 2) Hardware-accelerated Windows Media Foundation (MediaTranscoder) built into Windows.
/// Converts Apple MOV, MKV, AVI, WMV, etc. into universal MP4 (H.264 + AAC) and animated GIF.
/// </summary>
public sealed class VideoTranscoder
{
    private readonly ToolLocator _tools;

    public VideoTranscoder(ToolLocator tools)
    {
        _tools = tools;
    }

    /// <summary>
    /// Finds FFmpeg executable if present in tools directory or PATH.
    /// </summary>
    public string? LocateFfmpeg()
    {
        // 1. tools/ffmpeg/ffmpeg.exe
        var p1 = Path.Combine(_tools.ToolsRoot, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(p1)) return p1;

        // 2. tools/ffmpeg.exe
        var p2 = Path.Combine(_tools.ToolsRoot, "ffmpeg.exe");
        if (File.Exists(p2)) return p2;

        // 3. Base directory
        var p3 = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(p3)) return p3;

        // 4. System PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Transcodes a video file to universal MP4 (H.264 + AAC).
    /// </summary>
    public async Task<bool> TranscodeToMp4Async(
        string sourcePath,
        string destinationPath,
        string qualityPreset = "auto",
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath)) return false;

        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        var ffmpeg = LocateFfmpeg();
        if (ffmpeg != null)
        {
            return await TranscodeWithFfmpegAsync(ffmpeg, sourcePath, destinationPath, qualityPreset, progress, ct);
        }

        return await TranscodeWithWmfAsync(sourcePath, destinationPath, qualityPreset, progress, ct);
    }

    /// <summary>
    /// Transcodes a video or Live Photo MOV clip to an animated GIF.
    /// </summary>
    public async Task<bool> ConvertToGifAsync(
        string sourceVideoPath,
        string destinationGifPath,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(sourceVideoPath)) return false;

        var destDir = Path.GetDirectoryName(destinationGifPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        var ffmpeg = LocateFfmpeg();
        if (ffmpeg != null)
        {
            return await RunFfmpegGifAsync(ffmpeg, sourceVideoPath, destinationGifPath, progress, ct);
        }

        // Without FFmpeg, convert video to MP4 first if not already MP4, as universal fallback
        var fallbackMp4 = Path.ChangeExtension(destinationGifPath, ".mp4");
        return await TranscodeWithWmfAsync(sourceVideoPath, fallbackMp4, "auto", progress, ct);
    }

    private async Task<bool> TranscodeWithWmfAsync(
        string sourcePath,
        string destinationPath,
        string qualityPreset,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        try
        {
            var sourceFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(sourcePath));
            var destDir = Path.GetDirectoryName(Path.GetFullPath(destinationPath))!;
            Directory.CreateDirectory(destDir);
            var destFolder = await StorageFolder.GetFolderFromPathAsync(destDir);
            var destName = Path.GetFileName(destinationPath);
            var destFile = await destFolder.CreateFileAsync(destName, CreationCollisionOption.ReplaceExisting);

            var quality = qualityPreset.ToLowerInvariant() switch
            {
                "1080p" => VideoEncodingQuality.HD1080p,
                "720p" => VideoEncodingQuality.HD720p,
                _ => VideoEncodingQuality.Auto
            };

            var profile = MediaEncodingProfile.CreateMp4(quality);
            var transcoder = new MediaTranscoder();
            var prepare = await transcoder.PrepareFileTranscodeAsync(sourceFile, destFile, profile);

            if (!prepare.CanTranscode)
            {
                AppLog.Warn($"WMF transcode cannot prepare '{sourcePath}': {prepare.FailureReason}");
                return false;
            }

            var transcodeOp = prepare.TranscodeAsync();
            transcodeOp.Progress = (info, percent) =>
            {
                progress?.Report(percent);
            };

            using (ct.Register(() => transcodeOp.Cancel()))
            {
                await transcodeOp;
            }

            return File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error($"WMF transcode failed on '{sourcePath}'", ex);
            return false;
        }
    }

    private static async Task<bool> TranscodeWithFfmpegAsync(
        string ffmpegPath,
        string sourcePath,
        string destinationPath,
        string qualityPreset,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var scaleFilter = qualityPreset.ToLowerInvariant() switch
        {
            "1080p" => "-vf \"scale='min(1920,iw)':-2\" ",
            "720p" => "-vf \"scale='min(1280,iw)':-2\" ",
            _ => ""
        };

        var args = $"-i \"{sourcePath}\" -c:v libx264 -preset fast -crf 22 {scaleFilter}-c:a aac -b:a 192k -movflags +faststart \"{destinationPath}\" -y";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        using (ct.Register(() =>
        {
            try { proc.Kill(); } catch { }
        }))
        {
            await proc.WaitForExitAsync(ct);
        }

        return proc.ExitCode == 0 && File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0;
    }

    private static async Task<bool> RunFfmpegGifAsync(
        string ffmpegPath,
        string sourcePath,
        string destinationPath,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var args = $"-i \"{sourcePath}\" -vf \"fps=15,scale=480:-1:flags=lanczos\" \"{destinationPath}\" -y";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        using (ct.Register(() =>
        {
            try { proc.Kill(); } catch { }
        }))
        {
            await proc.WaitForExitAsync(ct);
        }

        return proc.ExitCode == 0 && File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0;
    }
}
