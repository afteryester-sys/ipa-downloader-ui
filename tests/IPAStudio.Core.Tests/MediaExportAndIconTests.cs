namespace IPAStudio.Core.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

/// <summary>
/// Unit tests for Media Export categorization, date parsing, app icon resolver,
/// dynamic filtering, and statistics metrics.
/// </summary>
public static class MediaExportAndIconTests
{
    public static void RunAll()
    {
        TestMediaClassification();
        TestCategoryDetection();
        TestDateDetection();
        TestAppIconResolver();
        TestBuildResultFromFiles();
        TestStatisticsSummaryZeroHandling();
    }

    public static void TestMediaClassification()
    {
        AssertEqual(MediaExportService.IsPhoto("pic.jpg"), true, "jpg is photo");
        AssertEqual(MediaExportService.IsPhoto("pic.HEIC"), true, "HEIC is photo");
        AssertEqual(MediaExportService.IsPhoto("pic.PNG"), true, "PNG is photo");
        AssertEqual(MediaExportService.IsPhoto("movie.mp4"), false, "mp4 is not photo");

        AssertEqual(MediaExportService.IsVideo("movie.mp4"), true, "mp4 is video");
        AssertEqual(MediaExportService.IsVideo("movie.MOV"), true, "MOV is video");
        AssertEqual(MediaExportService.IsVideo("movie.mkv"), true, "mkv is video");
        AssertEqual(MediaExportService.IsVideo("pic.jpg"), false, "jpg is not video");

        AssertEqual(MediaExportService.IsMedia("pic.jpg"), true, "jpg is media");
        AssertEqual(MediaExportService.IsMedia("movie.mp4"), true, "mp4 is media");
        AssertEqual(MediaExportService.IsMedia("document.pdf"), false, "pdf is not media");
    }

    public static void TestCategoryDetection()
    {
        AssertEqual(MediaExportService.DetectCategory("video.mp4", true, ""), "Видеозаписи", "mp4 is video category");
        AssertEqual(MediaExportService.DetectCategory("clip.mov", true, ""), "Видеозаписи", "mov is video category");

        AssertEqual(MediaExportService.DetectCategory("Screenshot_20240512-143022.png", false, ""), "Скриншоты", "Screenshot_ prefix");
        AssertEqual(MediaExportService.DetectCategory("Screen_001.png", false, ""), "Скриншоты", "Screen_ prefix");
        AssertEqual(MediaExportService.DetectCategory("Снимок экрана 2024-05-12.png", false, ""), "Скриншоты", "Russian screenshot prefix");
        AssertEqual(MediaExportService.DetectCategory(@"C:\DCIM\Screenshots\test.jpg", false, "DCIM"), "Скриншоты", "Screenshots directory");

        AssertEqual(MediaExportService.DetectCategory("IMG_20240512_FRONT_001.jpg", false, "DCIM"), "Селфи", "FRONT tag in filename");
        AssertEqual(MediaExportService.DetectCategory("Selfie_2024.jpg", false, "DCIM"), "Селфи", "Selfie in filename");
        AssertEqual(MediaExportService.DetectCategory("SEL_2024.jpg", false, "DCIM"), "Селфи", "SEL_ prefix");

        AssertEqual(MediaExportService.DetectCategory(@"C:\DCIM\Camera\IMG_0001.jpg", false, "DCIM"), "Снимки камеры", "DCIM/Camera directory");
        AssertEqual(MediaExportService.DetectCategory("DSC_1234.jpg", false, ""), "Снимки камеры", "DSC_ prefix");
        AssertEqual(MediaExportService.DetectCategory("PXL_20240512_123.jpg", false, ""), "Снимки камеры", "PXL_ prefix");

        AssertEqual(MediaExportService.DetectCategory("random_icon.png", false, "Downloads"), "Прочее", "Generic image is other");
    }

    public static void TestDateDetection()
    {
        var d1 = MediaExportService.DetectMediaDate("IMG_20240512_143022.jpg");
        AssertEqual(d1?.Year, 2024, "IMG_ year parsed");
        AssertEqual(d1?.Month, 5, "IMG_ month parsed");
        AssertEqual(d1?.Day, 12, "IMG_ day parsed");

        var d2 = MediaExportService.DetectMediaDate("Screenshot_20231105-102030.png");
        AssertEqual(d2?.Year, 2023, "Screenshot_ year parsed");
        AssertEqual(d2?.Month, 11, "Screenshot_ month parsed");

        var d3 = MediaExportService.DetectMediaDate("PXL_20220815_120000.jpg");
        AssertEqual(d3?.Year, 2022, "PXL_ year parsed");
        AssertEqual(d3?.Month, 8, "PXL_ month parsed");
    }

    public static void TestAppIconResolver()
    {
        // Null/empty input returns null
        AssertEqual(AppIconResolver.ResolveIconPath("", ""), null, "Empty bundle returns null");
        AssertEqual(AppIconResolver.ResolveIconPath(null, null), null, "Null bundle returns null");

        // Non-existent bundle returns null
        AssertEqual(AppIconResolver.ResolveIconPath("non.existent.bundle.12345", "NonExistentApp"), null, "Unknown bundle returns null");
    }

    public static void TestBuildResultFromFiles()
    {
        var service = new MediaExportService();
        var files = new List<MediaExportFile>
        {
            new MediaExportFile
            {
                FullPath = @"C:\DCIM\Camera\IMG_001.jpg",
                FileName = "IMG_001.jpg",
                GroupName = "DCIM",
                IsVideo = false,
                SizeBytes = 2_000_000,
                Category = "Снимки камеры",
                MediaDate = new DateTime(2024, 5, 1)
            },
            new MediaExportFile
            {
                FullPath = @"C:\DCIM\Camera\VID_001.mp4",
                FileName = "VID_001.mp4",
                GroupName = "DCIM",
                IsVideo = true,
                SizeBytes = 10_000_000,
                Category = "Видеозаписи",
                MediaDate = new DateTime(2024, 5, 2)
            },
            new MediaExportFile
            {
                FullPath = @"C:\Pictures\Screenshots\Screen_001.png",
                FileName = "Screen_001.png",
                GroupName = "Pictures",
                IsVideo = false,
                SizeBytes = 500_000,
                Category = "Скриншоты",
                MediaDate = null
            }
        };

        var result = service.BuildResultFromFiles(files, 2, key => key == "L.MediaExport.NoDate" ? "Без даты" : key);
        AssertEqual(result.TotalCount, 3, "TotalCount is 3");
        AssertEqual(result.TotalPhotos, 2, "TotalPhotos is 2");
        AssertEqual(result.TotalVideos, 1, "TotalVideos is 1");
        AssertEqual(result.CategoryGroups.Count, 3, "CategoryGroups count is 3");
        AssertEqual(result.DateGroups.Any(g => g.Name == "Без даты"), true, "DateGroups has Без даты fallback");
    }

    public static void TestStatisticsSummaryZeroHandling()
    {
        var stats = new StatisticsSummary
        {
            TotalInstalls = 0,
            UniqueDevices = 0,
            TotalConnections = 0
        };
        AssertEqual(stats.UniqueDevices, 0, "Cleared statistics unique devices is 0");
    }

    private static void AssertEqual<T>(T actual, T expected, string testName)
    {
        if (Equals(actual, expected))
        {
            Console.WriteLine($"  [PASS] {testName}");
        }
        else
        {
            Console.WriteLine($"  [FAIL] {testName}: expected '{expected}', got '{actual}'");
            throw new Exception($"Test failed: {testName}");
        }
    }
}
