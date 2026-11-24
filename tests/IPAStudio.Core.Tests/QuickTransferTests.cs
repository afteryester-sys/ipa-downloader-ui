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
        TestVCardContactCounting();
        TestQuickTransferPayloadClassification();
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
        AssertEqual(FileClassifier.Classify("contact.vcf"), FileCategory.Contact, "vcf is Contact");
        AssertEqual(FileClassifier.Classify("cards.vcard"), FileCategory.Contact, "vcard is Contact");
        AssertEqual(FileClassifier.Classify("other.xyz"), FileCategory.Other, "xyz is Other");
        AssertEqual(FileClassifier.Classify(""), FileCategory.Other, "Empty is Other");

        AssertEqual(FileClassifier.IsCameraRollMedia("photo.jpg"), true, "jpg is Camera Roll");
        AssertEqual(FileClassifier.IsCameraRollMedia("video.mov"), true, "mov is Camera Roll");
        AssertEqual(FileClassifier.IsCameraRollMedia("movie.mkv"), false, "mkv is NOT Camera Roll");
    }

    public static void TestRecommendations()
    {
        var vlc = new FileSharingApp("org.videolan.vlc-ios", "VLC");
        var books = new FileSharingApp("com.apple.iBooks", "Books");
        var readdle = new FileSharingApp("com.readdle.ReaddleDocsIPad", "Documents");
        var word = new FileSharingApp("com.microsoft.Office.Word", "Word");
        var outlook = new FileSharingApp("com.microsoft.Office.Outlook", "Outlook");

        var apps = new[] { readdle, word, books, vlc, outlook };

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

        // Test Contact recommendation
        var contactPayloads = new[] { FileClassifier.Describe("clients.vcf") };
        var bestContact = AppRecommendationEngine.GetBestRecommendation(contactPayloads, apps);
        AssertEqual(bestContact?.BundleId, "com.microsoft.Office.Outlook", "Outlook recommended for VCF");
    }

    public static void TestVCardContactCounting()
    {
        AssertEqual(QuickTransferService.CountVCardContacts(null!), 0, "Null path returns 0");
        AssertEqual(QuickTransferService.CountVCardContacts("non_existent_file.vcf"), 0, "Non-existent path returns 0");

        var tempVcf = System.IO.Path.GetTempFileName() + ".vcf";
        try
        {
            var vcfContent = """
            BEGIN:VCARD
            VERSION:3.0
            FN:Alice Smith
            TEL;TYPE=CELL:+1234567890
            END:VCARD
            BEGIN:VCARD
            VERSION:3.0
            FN:Bob Jones
            TEL;TYPE=CELL:+0987654321
            END:VCARD
            BEGIN:VCARD
            VERSION:3.0
            FN:Charlie Brown
            TEL;TYPE=WORK:+1122334455
            END:VCARD
            """;
            System.IO.File.WriteAllText(tempVcf, vcfContent);

            var count = QuickTransferService.CountVCardContacts(tempVcf);
            AssertEqual(count, 3, "Counted 3 vCard contact entries");
        }
        finally
        {
            if (System.IO.File.Exists(tempVcf)) System.IO.File.Delete(tempVcf);
        }
    }

    public static void TestQuickTransferPayloadClassification()
    {
        var vlc = new FileSharingApp("org.videolan.vlc-ios", "VLC");
        var apps = new[] { vlc };

        var files = new[]
        {
            "vacation.jpg",
            "family.mp4",
            "addressbook.vcf",
            "calculator.ipa",
            "documentary.mkv"
        };

        // When userSelectedApp is null, native files route to native apps, and mkv routes to VLC
        var payloads = QuickTransferService.ClassifyPayloads(files, userSelectedApp: null, availableApps: apps);
        AssertEqual(payloads.Count, 5, "5 payloads classified");

        var jpgPayload = payloads.First(p => p.FullPath.EndsWith("vacation.jpg", StringComparison.OrdinalIgnoreCase));
        AssertEqual(jpgPayload.Category, FileCategory.Photo, "vacation.jpg is Photo");
        AssertEqual(jpgPayload.TargetAppDisplayName, "Photos Library (iPhone)", "vacation.jpg target is Photos Library");

        var mp4Payload = payloads.First(p => p.FullPath.EndsWith("family.mp4", StringComparison.OrdinalIgnoreCase));
        AssertEqual(mp4Payload.Category, FileCategory.Video, "family.mp4 is Video");
        AssertEqual(mp4Payload.TargetAppDisplayName, "Photos Library (iPhone)", "family.mp4 target is Photos Library");

        var vcfPayload = payloads.First(p => p.FullPath.EndsWith("addressbook.vcf", StringComparison.OrdinalIgnoreCase));
        AssertEqual(vcfPayload.Category, FileCategory.Contact, "addressbook.vcf is Contact");
        AssertEqual(vcfPayload.TargetAppDisplayName, "Contacts", "addressbook.vcf target is Contacts");

        var ipaPayload = payloads.First(p => p.FullPath.EndsWith("calculator.ipa", StringComparison.OrdinalIgnoreCase));
        AssertEqual(ipaPayload.Category, FileCategory.App, "calculator.ipa is App");
        AssertEqual(ipaPayload.TargetAppDisplayName, "Install to iPhone", "calculator.ipa target is Install to iPhone");

        var mkvPayload = payloads.First(p => p.FullPath.EndsWith("documentary.mkv", StringComparison.OrdinalIgnoreCase));
        AssertEqual(mkvPayload.Category, FileCategory.Video, "documentary.mkv is Video");
        AssertEqual(mkvPayload.TargetAppDisplayName, "VLC", "documentary.mkv target is VLC");

        // When user explicitly selects VLC as destination for document/media files:
        // Native apps (Photos, Contacts, Install) strictly preserve their native destinations!
        var forcedPayloads = QuickTransferService.ClassifyPayloads(files, userSelectedApp: vlc, availableApps: apps);
        var forcedMkv = forcedPayloads.First(p => p.FullPath.EndsWith("documentary.mkv", StringComparison.OrdinalIgnoreCase));
        AssertEqual(forcedMkv.TargetAppDisplayName, "VLC", "forced mkv target is VLC");

        var forcedJpg = forcedPayloads.First(p => p.FullPath.EndsWith("vacation.jpg", StringComparison.OrdinalIgnoreCase));
        AssertEqual(forcedJpg.TargetAppDisplayName, "Photos Library (iPhone)", "forced jpg preserves Photos Library");

        var forcedVcf = forcedPayloads.First(p => p.FullPath.EndsWith("addressbook.vcf", StringComparison.OrdinalIgnoreCase));
        AssertEqual(forcedVcf.TargetAppDisplayName, "Contacts", "forced vcf preserves Contacts");

        var forcedIpa = forcedPayloads.First(p => p.FullPath.EndsWith("calculator.ipa", StringComparison.OrdinalIgnoreCase));
        AssertEqual(forcedIpa.TargetAppDisplayName, "Install to iPhone", "forced ipa preserves Install to iPhone");

        // When no 3rd-party file sharing apps are available, unhandled files route to device storage (/Downloads)
        var noAppPayloads = QuickTransferService.ClassifyPayloads(files, userSelectedApp: null, availableApps: Array.Empty<FileSharingApp>());
        var storageMkv = noAppPayloads.First(p => p.FullPath.EndsWith("documentary.mkv", StringComparison.OrdinalIgnoreCase));
        AssertEqual(storageMkv.TargetAppDisplayName, "iPhone Storage (Downloads)", "no app mkv routes to iPhone Storage (Downloads)");
    }

    private static void AssertEqual<T>(T actual, T expected, string testName)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            throw new InvalidOperationException($"Test failed: {testName}. Expected: {expected}, Actual: {actual}");
        }
        Console.WriteLine($"  [PASS] {testName}");
    }
}
