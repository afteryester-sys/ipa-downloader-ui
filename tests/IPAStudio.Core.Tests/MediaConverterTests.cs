namespace IPAStudio.Core.Tests;

using System;
using System.IO;
using System.Threading.Tasks;
using ImageMagick;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

public static class MediaConverterTests
{
    private static void AssertEqual<T>(T actual, T expected, string testName)
    {
        if (!object.Equals(actual, expected))
        {
            throw new Exception($"FAIL [{testName}]: expected '{expected}', but got '{actual}'");
        }
        Console.WriteLine($"  [PASS] {testName}");
    }

    private static void AssertTrue(bool condition, string testName)
    {
        if (!condition)
        {
            throw new Exception($"FAIL [{testName}]: expected true, got false");
        }
        Console.WriteLine($"  [PASS] {testName}");
    }

    public static void RunAll()
    {
        TestExtensions();
        TestLivePhotoPairing();
        TestImageConversion();
        TestPostExportFolderConversion().GetAwaiter().GetResult();
        TestBatchConversionWithSubdirectories().GetAwaiter().GetResult();
        TestLivePhotoModesAndGifTranscoder().GetAwaiter().GetResult();
        TestCorruptionResilience();
        TestSettingsAndOptions();
    }

    private static void TestExtensions()
    {
        AssertEqual(MediaConverterService.IsHeic("photo.heic"), true, "heic is recognized as HEIC");
        AssertEqual(MediaConverterService.IsHeic("PHOTO.HEIF"), true, "HEIF is recognized as HEIC");
        AssertEqual(MediaConverterService.IsHeic(".heic"), true, "extension only .heic is recognized");
        AssertEqual(MediaConverterService.IsHeic("image.jpg"), false, "jpg is not HEIC");
        AssertEqual(MediaConverterService.IsHeic("video.mov"), false, "mov is not HEIC");
        AssertEqual(MediaConverterService.IsHeic(""), false, "empty string is not HEIC");

        AssertEqual(MediaConverterService.IsVideo("clip.MOV"), true, "MOV is video");
        AssertEqual(MediaConverterService.IsVideo("movie.mp4"), true, "mp4 is video");
        AssertEqual(MediaConverterService.IsVideo("video.mkv"), true, "mkv is video");
        AssertEqual(MediaConverterService.IsVideo("photo.png"), false, "png is not video");

        AssertEqual(MediaConverterService.IsPhoto("photo.jpg"), true, "jpg is photo");
        AssertEqual(MediaConverterService.IsPhoto("photo.heic"), true, "heic is photo");
        AssertEqual(MediaConverterService.IsPhoto("clip.mp4"), false, "mp4 is not photo");
    }

    private static void TestLivePhotoPairing()
    {
        var files = new[]
        {
            @"C:\Photos\IMG_0001.HEIC",
            @"C:\Photos\IMG_0001.MOV",
            @"C:\Photos\IMG_0002.HEIC",
            @"C:\Photos\video.mp4"
        };

        var classified = MediaConverterService.ClassifyFiles(files);
        AssertEqual(classified.Count, 3, "Classified items count");

        var livePair = classified.Find(c => c.Kind == MediaItemKind.LivePhotoPair);
        AssertTrue(livePair != null, "Live Photo pair detected");
        AssertEqual(livePair!.PairedPhotoPath, @"C:\Photos\IMG_0001.HEIC", "Live Photo photo path matches");
        AssertEqual(livePair.PairedVideoPath, @"C:\Photos\IMG_0001.MOV", "Live Photo video path matches");

        var singleHeic = classified.Find(c => c.Kind == MediaItemKind.HeicPhoto);
        AssertTrue(singleHeic != null, "Single HEIC photo detected");
        AssertEqual(singleHeic!.FileName, "IMG_0002.HEIC", "Single HEIC filename matches");

        var singleVideo = classified.Find(c => c.Kind == MediaItemKind.Video);
        AssertTrue(singleVideo != null, "Single video detected");
        AssertEqual(singleVideo!.FileName, "video.mp4", "Single video filename matches");
    }

    private static void TestImageConversion()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "IPAStudio_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var srcPath = Path.Combine(tempDir, "sample.png");
            var dstPath = Path.Combine(tempDir, "sample.jpg");

            // Create a small test image using Magick.NET
            using (var img = new MagickImage(MagickColors.DodgerBlue, 100, 100))
            {
                img.Format = MagickFormat.Png;
                img.Write(srcPath);
            }

            AssertTrue(File.Exists(srcPath), "Source test image created");

            var ok = MediaConverterService.ConvertImageToJpeg(srcPath, dstPath, quality: 90, preserveTimestamps: true);
            AssertTrue(ok, "ConvertImageToJpeg succeeded");
            AssertTrue(File.Exists(dstPath), "Destination JPEG exists");
            AssertTrue(new FileInfo(dstPath).Length > 0, "Destination JPEG is not empty");

            // Byte conversion test
            var srcBytes = File.ReadAllBytes(srcPath);
            var jpegBytes = MediaConverterService.ConvertHeicBytesToJpeg(srcBytes, quality: 85);
            AssertTrue(jpegBytes.Length > 0, "ConvertHeicBytesToJpeg produced valid bytes");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task TestPostExportFolderConversion()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "IPAStudio_PostExport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var photo1 = Path.Combine(tempDir, "IMG_0001.heic");
            var photo2 = Path.Combine(tempDir, "IMG_0002.jpg");

            using (var img = new MagickImage(MagickColors.Crimson, 50, 50))
            {
                img.Format = MagickFormat.Png; // test image content
                img.Write(photo1);
                img.Write(photo2);
            }

            var service = new MediaConverterService();
            var converted = await service.ConvertPostExportFolderAsync(
                tempDir,
                deleteOriginals: true,
                quality: 90);

            AssertEqual(converted, 1, "Exactly 1 HEIC file converted in folder");
            AssertTrue(File.Exists(Path.Combine(tempDir, "IMG_0001.jpg")), "Converted IMG_0001.jpg exists");
            AssertTrue(!File.Exists(photo1), "Original IMG_0001.heic deleted after conversion");
            AssertTrue(File.Exists(photo2), "Existing non-HEIC photo IMG_0002.jpg preserved untouched");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task TestBatchConversionWithSubdirectories()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "IPAStudio_BatchSubdirs_" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(tempDir, "Source");
        var destDir = Path.Combine(tempDir, "Output");

        var sub1 = Path.Combine(srcDir, "Vacation");
        var sub2 = Path.Combine(srcDir, "Family");
        Directory.CreateDirectory(sub1);
        Directory.CreateDirectory(sub2);
        Directory.CreateDirectory(destDir);

        try
        {
            var p1 = Path.Combine(sub1, "IMG_0001.heic");
            var p2 = Path.Combine(sub2, "IMG_0002.heic");
            var v2 = Path.Combine(sub2, "IMG_0002.mov");

            using (var img = new MagickImage(MagickColors.LimeGreen, 40, 40))
            {
                img.Format = MagickFormat.Png;
                img.Write(p1);
                img.Write(p2);
            }
            File.WriteAllText(v2, "dummy_video_stream");

            var service = new MediaConverterService
            {
                VideoTranscoder = (src, dst, prog, ct) =>
                {
                    File.WriteAllText(dst, "dummy_transcoded_mp4");
                    return Task.FromResult(true);
                }
            };

            var items = service.ScanItems(new[] { srcDir }, recursive: true);
            AssertEqual(items.Count, 2, "Scanned 2 items across subdirectories (1 HEIC + 1 Live Photo pair)");

            var options = new MediaConverterOptions
            {
                OutputFolder = destDir,
                SaveInSourceFolder = false,
                PreserveSubdirectories = true,
                LivePhotoMode = LivePhotoConversionMode.UniversalJpegAndMp4
            };

            var converted = await service.ConvertBatchAsync(items, options, rootBaseFolder: srcDir);
            AssertEqual(converted, 2, "Batch converted 2 items with hierarchy preserved");

            var out1 = Path.Combine(destDir, "Vacation", "IMG_0001.jpg");
            var out2Photo = Path.Combine(destDir, "Family", "IMG_0002.jpg");
            var out2Video = Path.Combine(destDir, "Family", "IMG_0002.mp4");

            AssertTrue(File.Exists(out1), "IMG_0001.jpg exists in Vacation subdirectory");
            AssertTrue(File.Exists(out2Photo), "IMG_0002.jpg exists in Family subdirectory");
            AssertTrue(File.Exists(out2Video), "IMG_0002.mp4 exists in Family subdirectory");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task TestLivePhotoModesAndGifTranscoder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "IPAStudio_LiveModes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var photo = Path.Combine(tempDir, "IMG_5555.heic");
            var video = Path.Combine(tempDir, "IMG_5555.mov");

            using (var img = new MagickImage(MagickColors.Orange, 40, 40))
            {
                img.Format = MagickFormat.Png;
                img.Write(photo);
            }
            File.WriteAllText(video, "live_mov_content");

            bool gifTranscoderCalled = false;
            var service = new MediaConverterService
            {
                GifTranscoder = (src, dst, prog, ct) =>
                {
                    gifTranscoderCalled = true;
                    File.WriteAllText(dst, "dummy_gif_content");
                    return Task.FromResult(true);
                }
            };

            var items = service.ScanItems(new[] { tempDir });
            AssertEqual(items.Count, 1, "Detected 1 Live Photo pair");

            // Test AnimatedGif mode
            var options = new MediaConverterOptions
            {
                SaveInSourceFolder = true,
                LivePhotoMode = LivePhotoConversionMode.AnimatedGif
            };

            var converted = await service.ConvertBatchAsync(items, options);
            AssertEqual(converted, 1, "Live Photo converted in AnimatedGif mode");
            AssertTrue(gifTranscoderCalled, "GifTranscoder delegate was invoked");
            AssertTrue(File.Exists(Path.Combine(tempDir, "IMG_5555.gif")), "Animated GIF file was created");
            AssertTrue(File.Exists(Path.Combine(tempDir, "IMG_5555.jpg")), "Photo JPEG companion was created");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void TestCorruptionResilience()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "IPAStudio_Corrupt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var corruptFile = Path.Combine(tempDir, "corrupted.heic");
            var targetJpeg = Path.Combine(tempDir, "corrupted.jpg");
            File.WriteAllText(corruptFile, "THIS IS NOT A VALID HEIC IMAGE CONTENT");

            // Should return false and not throw unhandled exception
            var ok = MediaConverterService.ConvertImageToJpeg(corruptFile, targetJpeg);
            AssertEqual(ok, false, "Corrupted file gracefully returns false");
            AssertEqual(File.Exists(targetJpeg), false, "No corrupted output created");

            // Byte conversion on corrupted bytes
            var corruptBytes = new byte[] { 0x00, 0x01, 0x02, 0xFF };
            var resultBytes = MediaConverterService.ConvertHeicBytesToJpeg(corruptBytes);
            AssertEqual(resultBytes.Length, 0, "Corrupted bytes gracefully return empty array");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void TestSettingsAndOptions()
    {
        var options = new MediaConverterOptions
        {
            JpegQuality = 95,
            PhotoFormat = "jpeg",
            DeleteOriginals = true,
            LivePhotoMode = LivePhotoConversionMode.UniversalJpegAndMp4
        };

        AssertEqual(options.JpegQuality, 95, "Options JpegQuality is 95");
        AssertEqual(options.DeleteOriginals, true, "Options DeleteOriginals is true");
        AssertEqual(options.LivePhotoMode, LivePhotoConversionMode.UniversalJpegAndMp4, "Options LivePhotoMode is UniversalJpegAndMp4");

        AssertEqual((int)HeicExportConversionMode.Prompt, 0, "Prompt mode is 0");
        AssertEqual((int)HeicExportConversionMode.PostTransfer, 1, "PostTransfer mode is 1");
        AssertEqual((int)HeicExportConversionMode.DuringTransfer, 2, "DuringTransfer mode is 2");
        AssertEqual((int)HeicExportConversionMode.Disabled, 3, "Disabled mode is 3");
    }
}
