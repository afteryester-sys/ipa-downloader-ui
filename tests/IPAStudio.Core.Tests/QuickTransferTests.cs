namespace IPAStudio.Core.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using IPAStudio.Core.Models;
using IPAStudio.Core.Services;

/// <summary>
/// Unit tests for Quick Transfer file classification and app recommendation engine.
/// </summary>
public static class QuickTransferTests
{
    public static void RunAll()
    {
        TestClassification();
        TestRecommendations();
    }

    public static void TestClassification()
    {
        AssertEqual(FileClassifier.Classify("movie.mp4"), FileCategory.Video, "mp4 is Video");
        AssertEqual(FileClassifier.Classify("movie.MKV"), FileCategory.Video, "MKV is Video");
        AssertEqual(FileClassifier.Classify("song.mp3"), FileCategory.Audio, "mp3 is Audio");
        AssertEqual(FileClassifier.Classify("song.FLAC"), FileCategory.Audio, "FLAC is Audio");
        AssertEqual(FileClassifier.Classify("book.epub"), FileCategory.Book, "epub is Book");
        AssertEqual(FileClassifier.Classify("doc.pdf"), FileCategory.Book, "pdf is Book");
        AssertEqual(FileClassifier.Classify("report.docx"), FileCategory.Document, "docx is Document");
        AssertEqual(FileClassifier.Classify("photo.heic"), FileCategory.Photo, "heic is Photo");
        AssertEqual(FileClassifier.Classify("archive.7z"), FileCategory.Archive, "7z is Archive");
        AssertEqual(FileClassifier.Classify("app.ipa"), FileCategory.App, "ipa is App");
        AssertEqual(FileClassifier.Classify("other.xyz"), FileCategory.Other, "xyz is Other");
        AssertEqual(FileClassifier.Classify(""), FileCategory.Other, "Empty is Other");
    }

    public static void TestRecommendations()
    {
        var vlc = new FileSharingApp("org.videolan.vlc-ios", "VLC");
        var books = new FileSharingApp("com.apple.iBooks", "Books");
        var readdle = new FileSharingApp("com.readdle.ReaddleDocsIPad", "Documents");
        var word = new FileSharingApp("com.microsoft.Office.Word", "Word");

        var apps = new[] { readdle, word, books, vlc };

        // Test Video recommendation
        var videoPayloads = new[] { FileClassifier.Describe("film.mkv") };
        var bestVideo = AppRecommendationEngine.GetBestRecommendation(videoPayloads, apps);
        AssertEqual(bestVideo?.BundleId, "org.videolan.vlc-ios", "VLC recommended for MKV");

        // Test Book recommendation
        var bookPayloads = new[] { FileClassifier.Describe("novel.epub") };
        var bestBook = AppRecommendationEngine.GetBestRecommendation(bookPayloads, apps);
        AssertEqual(bestBook?.BundleId, "com.apple.iBooks", "Books recommended for EPUB");

        // Test Word recommendation
        var docPayloads = new[] { FileClassifier.Describe("report.docx") };
        var bestDoc = AppRecommendationEngine.GetBestRecommendation(docPayloads, apps);
        AssertEqual(bestDoc?.BundleId, "com.microsoft.Office.Word", "Word recommended for DOCX");
    }

    private static void AssertEqual<T>(T actual, T expected, string testName)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            throw new InvalidOperationException($"Test failed: {testName}. Expected: {expected}, Actual: {actual}");
        }
    }
}
