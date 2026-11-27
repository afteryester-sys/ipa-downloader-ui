using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ImageMagick;
using IPAStudio.Core.Diagnostics;
using IPAStudio.Core.Models;

namespace IPAStudio.Core.Services;

/// <summary>
/// Service providing high-performance media conversion:
/// - HEIC / HEIF to JPEG / PNG with EXIF preservation, auto-orientation, and timestamps.
/// - Batch directory conversion preserving relative folder structure and original names.
/// - Live Photo pair detection and universal conversion (HEIC + MOV -> JPEG + MP4).
/// - Post-factum photo export conversion allowing device disconnection prior to conversion.
/// </summary>
public sealed class MediaConverterService
{
    private static readonly HashSet<string> HeicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif"
    };

    private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".tif", ".webp", ".heic", ".heif"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mov", ".mp4", ".m4v", ".avi", ".mkv", ".wmv", ".webm", ".3gp", ".flv", ".ts"
    };

    /// <summary>
    /// Pluggable video transcoder delegate: (sourcePath, destPath, progress, ct) => Task&lt;bool&gt;
    /// Allows the UI / platform layer to supply hardware-accelerated Windows Media Foundation or FFmpeg transcoding.
    /// </summary>
    public Func<string, string, IProgress<double>?, CancellationToken, Task<bool>>? VideoTranscoder { get; set; }

    /// <summary>
    /// Pluggable animated GIF transcoder delegate: (sourceVideoPath, destGifPath, progress, ct) => Task&lt;bool&gt;
    /// </summary>
    public Func<string, string, IProgress<double>?, CancellationToken, Task<bool>>? GifTranscoder { get; set; }

    /// <summary>
    /// Checks whether the specified file extension is an Apple HEIC/HEIF container.
    /// </summary>
    public static bool IsHeic(string pathOrExt)
    {
        if (string.IsNullOrWhiteSpace(pathOrExt)) return false;
        var ext = Path.GetExtension(pathOrExt);
        if (string.IsNullOrEmpty(ext)) ext = pathOrExt;
        return HeicExtensions.Contains(ext);
    }

    /// <summary>
    /// Checks whether the file is a recognized video format.
    /// </summary>
    public static bool IsVideo(string pathOrExt)
    {
        if (string.IsNullOrWhiteSpace(pathOrExt)) return false;
        var ext = Path.GetExtension(pathOrExt);
        if (string.IsNullOrEmpty(ext)) ext = pathOrExt;
        return VideoExtensions.Contains(ext);
    }

    /// <summary>
    /// Checks whether the file is a recognized photo format.
    /// </summary>
    public static bool IsPhoto(string pathOrExt)
    {
        if (string.IsNullOrWhiteSpace(pathOrExt)) return false;
        var ext = Path.GetExtension(pathOrExt);
        if (string.IsNullOrEmpty(ext)) ext = pathOrExt;
        return PhotoExtensions.Contains(ext);
    }

    /// <summary>
    /// Scans a folder or list of files and classifies items into HEIC photos, Videos, Live Photo pairs, and other photos.
    /// </summary>
    public List<MediaConverterItem> ScanItems(IEnumerable<string> paths, bool recursive = true)
    {
        var fileList = new List<string>();

        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;

            if (File.Exists(p))
            {
                fileList.Add(p);
            }
            else if (Directory.Exists(p))
            {
                try
                {
                    var searchOpt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    var found = Directory.EnumerateFiles(p, "*.*", searchOpt);
                    fileList.AddRange(found);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"media converter: scan folder failed for '{p}': {ex.Message}");
                }
            }
        }

        return ClassifyFiles(fileList);
    }

    /// <summary>
    /// Classifies a list of file paths into converter items, identifying Live Photo pairs.
    /// </summary>
    public static List<MediaConverterItem> ClassifyFiles(IReadOnlyList<string> filePaths)
    {
        var result = new List<MediaConverterItem>();
        if (filePaths.Count == 0) return result;

        // Group by directory + basename to detect Live Photo pairs (IMG_0001.HEIC + IMG_0001.MOV)
        var groups = filePaths
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .GroupBy(f =>
            {
                var dir = Path.GetDirectoryName(f) ?? "";
                var baseName = Path.GetFileNameWithoutExtension(f);
                return Path.Combine(dir, baseName);
            }, StringComparer.OrdinalIgnoreCase);

        var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var files = group.ToList();
            var photo = files.FirstOrDefault(f => IsPhoto(f));
            var video = files.FirstOrDefault(f => Path.GetExtension(f).Equals(".mov", StringComparison.OrdinalIgnoreCase) ||
                                                  Path.GetExtension(f).Equals(".mp4", StringComparison.OrdinalIgnoreCase));

            // Detected Live Photo pair: photo + paired mov
            if (photo != null && video != null && !processedFiles.Contains(photo) && !processedFiles.Contains(video))
            {
                var fi = new FileInfo(photo);
                var vi = new FileInfo(video);
                result.Add(new MediaConverterItem
                {
                    SourcePath = photo,
                    FileName = Path.GetFileName(photo),
                    RelativePath = photo,
                    FileSizeBytes = fi.Exists ? fi.Length + (vi.Exists ? vi.Length : 0) : 0,
                    Extension = Path.GetExtension(photo).ToLowerInvariant(),
                    Kind = MediaItemKind.LivePhotoPair,
                    PairedPhotoPath = photo,
                    PairedVideoPath = video,
                });
                processedFiles.Add(photo);
                processedFiles.Add(video);
                continue;
            }

            // Normal files
            foreach (var file in files)
            {
                if (processedFiles.Contains(file)) continue;

                var ext = Path.GetExtension(file).ToLowerInvariant();
                var fi = new FileInfo(file);
                var kind = IsHeic(ext) ? MediaItemKind.HeicPhoto :
                           IsVideo(ext) ? MediaItemKind.Video :
                           IsPhoto(ext) ? MediaItemKind.OtherPhoto : MediaItemKind.Unknown;

                if (kind != MediaItemKind.Unknown)
                {
                    result.Add(new MediaConverterItem
                    {
                        SourcePath = file,
                        FileName = Path.GetFileName(file),
                        RelativePath = file,
                        FileSizeBytes = fi.Exists ? fi.Length : 0,
                        Extension = ext,
                        Kind = kind
                    });
                    processedFiles.Add(file);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Converts a HEIC/HEIF file or standard image to JPEG or PNG with preserved EXIF and timestamps.
    /// </summary>
    public static bool ConvertImageToJpeg(
        string sourcePath,
        string destinationPath,
        int quality = 92,
        bool preserveTimestamps = true)
    {
        if (!File.Exists(sourcePath)) return false;

        var destDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

        DateTime creationUtc = DateTime.UtcNow;
        DateTime lastWriteUtc = DateTime.UtcNow;
        if (preserveTimestamps)
        {
            try
            {
                creationUtc = File.GetCreationTimeUtc(sourcePath);
                lastWriteUtc = File.GetLastWriteTimeUtc(sourcePath);
            }
            catch { }
        }

        try
        {
            using (var image = new MagickImage(sourcePath))
            {
                image.AutoOrient();
                var ext = Path.GetExtension(destinationPath);
                if (string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase))
                {
                    image.Format = MagickFormat.Png;
                }
                else
                {
                    image.Format = MagickFormat.Jpeg;
                    image.Quality = (uint)Math.Clamp(quality, 1, 100);
                }
                image.Write(destinationPath);
            }

            if (preserveTimestamps)
            {
                try
                {
                    File.SetCreationTimeUtc(destinationPath, creationUtc);
                    File.SetLastWriteTimeUtc(destinationPath, lastWriteUtc);
                }
                catch { }
            }

            return File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"media converter: image conversion failed for '{sourcePath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Converts in-memory HEIC image bytes directly to JPEG format.
    /// Used for on-the-fly conversion during photo transfers.
    /// </summary>
    public static byte[] ConvertHeicBytesToJpeg(byte[] heicBytes, int quality = 92)
    {
        if (heicBytes == null || heicBytes.Length == 0) return Array.Empty<byte>();

        try
        {
            using var image = new MagickImage(heicBytes);
            image.AutoOrient();
            image.Format = MagickFormat.Jpeg;
            image.Quality = (uint)Math.Clamp(quality, 1, 100);

            using var ms = new MemoryStream();
            image.Write(ms);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"media converter: in-memory HEIC conversion failed: {ex.Message}");
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Post-factum conversion: converts all HEIC/HEIF files inside a directory to JPEG,
    /// enabling the user to disconnect the iPhone immediately after transfer completes.
    /// </summary>
    public async Task<int> ConvertPostExportFolderAsync(
        string targetFolder,
        bool deleteOriginals = true,
        int quality = 92,
        IProgress<MediaConverterProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(targetFolder)) return 0;

        var heicFiles = Directory.EnumerateFiles(targetFolder, "*.*", SearchOption.AllDirectories)
            .Where(f => IsHeic(f))
            .ToList();

        if (heicFiles.Count == 0) return 0;

        var total = heicFiles.Count;
        var completed = 0;
        var succeeded = 0;
        var failed = 0;

        var maxConcurrency = Math.Clamp(Environment.ProcessorCount, 1, 8);
        using var semaphore = new SemaphoreSlim(maxConcurrency);

        var tasks = heicFiles.Select(async file =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                var fileName = Path.GetFileName(file);
                var dir = Path.GetDirectoryName(file) ?? targetFolder;
                var baseName = Path.GetFileNameWithoutExtension(file);
                var destJpeg = Path.Combine(dir, $"{baseName}.jpg");

                if (destJpeg.Equals(file, StringComparison.OrdinalIgnoreCase))
                {
                    destJpeg = Path.Combine(dir, $"{baseName}_converted.jpg");
                }

                progress?.Report(new MediaConverterProgress(
                    completed, total, fileName, (double)completed / total * 100.0, succeeded, failed,
                    $"Конвертация HEIC: {fileName}"));

                bool ok = await Task.Run(() => ConvertImageToJpeg(file, destJpeg, quality, preserveTimestamps: true), ct)
                    .ConfigureAwait(false);

                if (ok)
                {
                    Interlocked.Increment(ref succeeded);
                    if (deleteOriginals && File.Exists(destJpeg) && new FileInfo(destJpeg).Length > 0)
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                AppLog.Warn($"media converter: post-export conversion failed for '{file}': {ex.Message}");
            }
            finally
            {
                var done = Interlocked.Increment(ref completed);
                semaphore.Release();
                progress?.Report(new MediaConverterProgress(
                    done, total, Path.GetFileName(file), (double)done / total * 100.0, succeeded, failed));
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return succeeded;
    }

    private static string? GetCommonDirectory(IEnumerable<string> paths)
    {
        var dirs = paths.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        if (dirs.Count == 0) return null;
        if (dirs.Count == 1) return dirs[0];

        var common = dirs[0];
        foreach (var dir in dirs.Skip(1))
        {
            while (!string.IsNullOrEmpty(common) && !dir.StartsWith(common, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(common);
                if (parent == common || string.IsNullOrEmpty(parent))
                {
                    common = "";
                    break;
                }
                common = parent;
            }
            if (string.IsNullOrEmpty(common)) break;
        }

        return string.IsNullOrEmpty(common) ? null : common;
    }

    /// <summary>
    /// Executes batch media conversion for a list of items according to specified options.
    /// </summary>
    public async Task<int> ConvertBatchAsync(
        IReadOnlyList<MediaConverterItem> items,
        MediaConverterOptions options,
        string? rootBaseFolder = null,
        IProgress<MediaConverterProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (items.Count == 0) return 0;

        if (string.IsNullOrWhiteSpace(rootBaseFolder))
        {
            rootBaseFolder = GetCommonDirectory(items.Select(i => Path.GetDirectoryName(i.SourcePath) ?? ""));
        }

        var total = items.Count;
        var completed = 0;
        var succeeded = 0;
        var failed = 0;

        var maxConcurrency = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        using var semaphore = new SemaphoreSlim(maxConcurrency);

        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();
                item.Status = MediaConversionStatus.Converting;

                progress?.Report(new MediaConverterProgress(
                    completed, total, item.FileName, (double)completed / total * 100.0, succeeded, failed,
                    $"Конвертация: {item.FileName}"));

                // Compute destination output path
                string destDir;
                if (options.SaveInSourceFolder || string.IsNullOrWhiteSpace(options.OutputFolder))
                {
                    destDir = Path.GetDirectoryName(item.SourcePath) ?? "";
                }
                else
                {
                    if (options.PreserveSubdirectories && !string.IsNullOrEmpty(rootBaseFolder) &&
                        item.SourcePath.StartsWith(rootBaseFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        var relDir = Path.GetDirectoryName(item.SourcePath[rootBaseFolder.Length..].TrimStart('\\', '/')) ?? "";
                        destDir = Path.Combine(options.OutputFolder, relDir);
                    }
                    else
                    {
                        destDir = options.OutputFolder;
                    }
                }

                Directory.CreateDirectory(destDir);
                var baseName = Path.GetFileNameWithoutExtension(item.SourcePath);
                bool ok = false;

                switch (item.Kind)
                {
                    case MediaItemKind.HeicPhoto:
                    case MediaItemKind.OtherPhoto:
                    {
                        var targetExt = options.PhotoFormat.Equals("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
                        var destPath = Path.Combine(destDir, $"{baseName}{targetExt}");
                        if (destPath.Equals(item.SourcePath, StringComparison.OrdinalIgnoreCase))
                        {
                            destPath = Path.Combine(destDir, $"{baseName}_converted{targetExt}");
                        }

                        item.OutputPath = destPath;

                        if (Path.GetExtension(item.SourcePath).Equals(targetExt, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!destPath.Equals(item.SourcePath, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Copy(item.SourcePath, destPath, overwrite: true);
                                ok = true;
                            }
                            else
                            {
                                ok = true;
                            }
                        }
                        else
                        {
                            ok = await Task.Run(() => ConvertImageToJpeg(
                                item.SourcePath, destPath, options.JpegQuality, options.PreserveTimestamps), ct).ConfigureAwait(false);
                        }

                        if (ok && options.DeleteOriginals && File.Exists(destPath) && new FileInfo(destPath).Length > 0 &&
                            !destPath.Equals(item.SourcePath, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(item.SourcePath); } catch { }
                        }
                        break;
                    }

                    case MediaItemKind.Video:
                    {
                        var destPath = Path.Combine(destDir, $"{baseName}.mp4");
                        if (destPath.Equals(item.SourcePath, StringComparison.OrdinalIgnoreCase))
                        {
                            destPath = Path.Combine(destDir, $"{baseName}_converted.mp4");
                        }

                        item.OutputPath = destPath;
                        if (VideoTranscoder != null)
                        {
                            ok = await VideoTranscoder(item.SourcePath, destPath, null, ct).ConfigureAwait(false);
                        }
                        else
                        {
                            if (!destPath.Equals(item.SourcePath, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Copy(item.SourcePath, destPath, overwrite: true);
                                ok = true;
                            }
                            else
                            {
                                throw new NotSupportedException("Video transcoding engine is not initialized.");
                            }
                        }

                        if (ok && options.DeleteOriginals && File.Exists(destPath) && new FileInfo(destPath).Length > 0 &&
                            !destPath.Equals(item.SourcePath, StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(item.SourcePath); } catch { }
                        }
                        break;
                    }

                    case MediaItemKind.LivePhotoPair:
                    {
                        var photoPath = item.PairedPhotoPath ?? item.SourcePath;
                        var videoPath = item.PairedVideoPath;
                        var targetExt = options.PhotoFormat.Equals("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";

                        bool photoOk = true;
                        bool videoOk = true;

                        switch (options.LivePhotoMode)
                        {
                            case LivePhotoConversionMode.PhotoOnlyJpeg:
                            {
                                var destPhoto = Path.Combine(destDir, $"{baseName}{targetExt}");
                                if (destPhoto.Equals(photoPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    destPhoto = Path.Combine(destDir, $"{baseName}_converted{targetExt}");
                                }
                                item.OutputPath = destPhoto;
                                photoOk = await Task.Run(() => ConvertImageToJpeg(
                                    photoPath, destPhoto, options.JpegQuality, options.PreserveTimestamps), ct).ConfigureAwait(false);
                                ok = photoOk;

                                if (ok && options.DeleteOriginals && File.Exists(destPhoto) && photoPath != destPhoto)
                                {
                                    try { File.Delete(photoPath); } catch { }
                                }
                                break;
                            }

                            case LivePhotoConversionMode.VideoOnlyMp4:
                            {
                                if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                                {
                                    var destMp4 = Path.Combine(destDir, $"{baseName}.mp4");
                                    if (destMp4.Equals(videoPath, StringComparison.OrdinalIgnoreCase))
                                    {
                                        destMp4 = Path.Combine(destDir, $"{baseName}_converted.mp4");
                                    }
                                    item.OutputPath = destMp4;
                                    if (VideoTranscoder != null)
                                    {
                                        videoOk = await VideoTranscoder(videoPath, destMp4, null, ct).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        if (!destMp4.Equals(videoPath, StringComparison.OrdinalIgnoreCase))
                                            File.Copy(videoPath, destMp4, overwrite: true);
                                        videoOk = true;
                                    }
                                    ok = videoOk;

                                    if (ok && options.DeleteOriginals && File.Exists(destMp4) && videoPath != destMp4)
                                    {
                                        try { File.Delete(videoPath); } catch { }
                                    }
                                }
                                else
                                {
                                    ok = false;
                                }
                                break;
                            }

                            case LivePhotoConversionMode.AnimatedGif:
                            {
                                var destPhoto = Path.Combine(destDir, $"{baseName}{targetExt}");
                                if (destPhoto.Equals(photoPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    destPhoto = Path.Combine(destDir, $"{baseName}_converted{targetExt}");
                                }
                                photoOk = await Task.Run(() => ConvertImageToJpeg(
                                    photoPath, destPhoto, options.JpegQuality, options.PreserveTimestamps), ct).ConfigureAwait(false);

                                var destGif = Path.Combine(destDir, $"{baseName}.gif");
                                item.OutputPath = destGif;

                                if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                                {
                                    if (GifTranscoder != null)
                                    {
                                        videoOk = await GifTranscoder(videoPath, destGif, null, ct).ConfigureAwait(false);
                                    }
                                    else if (VideoTranscoder != null)
                                    {
                                        var destMp4 = Path.Combine(destDir, $"{baseName}.mp4");
                                        videoOk = await VideoTranscoder(videoPath, destMp4, null, ct).ConfigureAwait(false);
                                        item.OutputPath = destMp4;
                                    }
                                }

                                ok = photoOk && videoOk;
                                if (ok && options.DeleteOriginals)
                                {
                                    try { if (File.Exists(destPhoto) && photoPath != destPhoto) File.Delete(photoPath); } catch { }
                                    try { if (!string.IsNullOrEmpty(videoPath) && (File.Exists(destGif) || File.Exists(item.OutputPath))) File.Delete(videoPath); } catch { }
                                }
                                break;
                            }

                            case LivePhotoConversionMode.UniversalJpegAndMp4:
                            default:
                            {
                                var destPhoto = Path.Combine(destDir, $"{baseName}{targetExt}");
                                if (destPhoto.Equals(photoPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    destPhoto = Path.Combine(destDir, $"{baseName}_converted{targetExt}");
                                }
                                photoOk = await Task.Run(() => ConvertImageToJpeg(
                                    photoPath, destPhoto, options.JpegQuality, options.PreserveTimestamps), ct).ConfigureAwait(false);

                                if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                                {
                                    var destMp4 = Path.Combine(destDir, $"{baseName}.mp4");
                                    if (destMp4.Equals(videoPath, StringComparison.OrdinalIgnoreCase))
                                    {
                                        destMp4 = Path.Combine(destDir, $"{baseName}_converted.mp4");
                                    }
                                    if (VideoTranscoder != null)
                                    {
                                        videoOk = await VideoTranscoder(videoPath, destMp4, null, ct).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        if (!destMp4.Equals(videoPath, StringComparison.OrdinalIgnoreCase))
                                            File.Copy(videoPath, destMp4, overwrite: true);
                                        videoOk = true;
                                    }
                                }

                                ok = photoOk && videoOk;
                                item.OutputPath = destPhoto;

                                if (ok && options.DeleteOriginals)
                                {
                                    try { if (File.Exists(destPhoto) && photoPath != destPhoto) File.Delete(photoPath); } catch { }
                                    try { if (!string.IsNullOrEmpty(videoPath)) File.Delete(videoPath); } catch { }
                                }
                                break;
                            }
                        }
                        break;
                    }

                    default:
                        ok = false;
                        break;
                }

                if (ok)
                {
                    item.Status = MediaConversionStatus.Completed;
                    Interlocked.Increment(ref succeeded);
                }
                else
                {
                    item.Status = MediaConversionStatus.Failed;
                    Interlocked.Increment(ref failed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                item.Status = MediaConversionStatus.Failed;
                item.ErrorMessage = ex.Message;
                Interlocked.Increment(ref failed);
                AppLog.Error($"media converter: conversion error on '{item.FileName}'", ex);
            }
            finally
            {
                var done = Interlocked.Increment(ref completed);
                semaphore.Release();
                progress?.Report(new MediaConverterProgress(
                    done, total, item.FileName, (double)done / total * 100.0, succeeded, failed));
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return succeeded;
    }
}
