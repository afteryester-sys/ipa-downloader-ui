namespace IPAStudio.Core.Tests;

using System;
using System.Collections.Generic;
using System.Xml.Linq;
using IPAStudio.Core.Models;
using IPAStudio.Core.Native;
using IPAStudio.Core.Services;

/// <summary>
/// Unit tests for AirTrafficPhotoSyncService manifest generation, UTI mapping,
/// media extension detection, and native bridge availability.
/// </summary>
public static class AirTrafficPhotoSyncTests
{
    public static void RunAll()
    {
        TestSupportedMedia();
        TestUtiMappings();
        TestManifestGeneration();
        TestMultiAlbumPreservation();
        TestXmlEscaping();
        TestDimensionsProbing();
        TestNativeEngineQuery();
    }

    public static void TestMultiAlbumPreservation()
    {
        var existingXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>LibraryUUID</key>
            <string>LIB-ORIGINAL</string>
            <key>Albums</key>
            <array>
                <dict>
                    <key>AlbumUUID</key>
                    <string>ALB-VACATION</string>
                    <key>AlbumName</key>
                    <string>Vacation</string>
                    <key>KeyAssetUUID</key>
                    <string>A1</string>
                    <key>AssetUUIDs</key>
                    <array>
                        <string>A1</string>
                        <string>A2</string>
                    </array>
                </dict>
                <dict>
                    <key>AlbumUUID</key>
                    <string>ALB-FAMILY</string>
                    <key>AlbumName</key>
                    <string>Family</string>
                    <key>KeyAssetUUID</key>
                    <string>A3</string>
                    <key>AssetUUIDs</key>
                    <array>
                        <string>A3</string>
                    </array>
                </dict>
            </array>
            <key>Assets</key>
            <array>
                <dict>
                    <key>AssetUUID</key>
                    <string>A1</string>
                </dict>
                <dict>
                    <key>AssetUUID</key>
                    <string>A2</string>
                </dict>
                <dict>
                    <key>AssetUUID</key>
                    <string>A3</string>
                </dict>
            </array>
        </dict>
        </plist>
        """;

        var existingDoc = XDocument.Parse(existingXml);
        var newAssets = new List<StagedAssetEntry>
        {
            new StagedAssetEntry("A4", "new.jpg", "A4.jpg", "public.jpeg", 1234, DateTime.UtcNow, 800, 600)
        };

        var mergedXml = AirTrafficPhotoSyncService.GeneratePhotoLibraryPlist("LIB-ORIGINAL", "IPA Studio", newAssets, existingDoc);

        // Verification: existing albums MUST be preserved!
        AssertEqual(mergedXml.Contains("Vacation"), true, "Vacation album preserved");
        AssertEqual(mergedXml.Contains("ALB-VACATION"), true, "Vacation album UUID preserved");
        AssertEqual(mergedXml.Contains("Family"), true, "Family album preserved");
        AssertEqual(mergedXml.Contains("IPA Studio"), true, "IPA Studio album added");

        var doc = XDocument.Parse(mergedXml);
        var dict = doc.Root!.Element("dict")!;
        var elements = dict.Elements().ToList();

        // Check albums
        XElement? albumsArr = null;
        XElement? assetsArr = null;
        for (var i = 0; i < elements.Count - 1; i++)
        {
            if (elements[i].Name == "key" && elements[i].Value == "Albums") albumsArr = elements[i + 1];
            if (elements[i].Name == "key" && elements[i].Value == "Assets") assetsArr = elements[i + 1];
        }

        AssertEqual(albumsArr != null, true, "Albums array found");
        AssertEqual(assetsArr != null, true, "Assets array found");

        var albumList = albumsArr!.Elements("dict").ToList();
        AssertEqual(albumList.Count, 3, "Exactly 3 albums present");

        // IPA Studio album should contain only A4, not A1, A2, A3
        var ipaStudioAlb = albumList.FirstOrDefault(a =>
        {
            var elems = a.Elements().ToList();
            for (var k = 0; k < elems.Count - 1; k++)
            {
                if (elems[k].Name == "key" && elems[k].Value == "AlbumName" && elems[k + 1].Value == "IPA Studio") return true;
            }
            return false;
        });
        AssertEqual(ipaStudioAlb != null, true, "IPA Studio album element found");

        var assetUuidsElem = ipaStudioAlb!.Elements().SkipWhile(e => e.Name != "key" || e.Value != "AssetUUIDs").Skip(1).FirstOrDefault();
        AssertEqual(assetUuidsElem != null, true, "IPA Studio AssetUUIDs array found");
        var ipaAssetUuids = assetUuidsElem!.Elements("string").Select(s => s.Value).ToList();
        AssertEqual(ipaAssetUuids.Count, 1, "IPA Studio has exactly 1 asset");
        AssertEqual(ipaAssetUuids[0], "A4", "IPA Studio has asset A4");

        // Vacation album should still have exactly A1, A2
        var vacAlb = albumList.FirstOrDefault(a =>
        {
            var elems = a.Elements().ToList();
            for (var k = 0; k < elems.Count - 1; k++)
            {
                if (elems[k].Name == "key" && elems[k].Value == "AlbumName" && elems[k + 1].Value == "Vacation") return true;
            }
            return false;
        });
        AssertEqual(vacAlb != null, true, "Vacation album element found");
        var vacAssetUuidsElem = vacAlb!.Elements().SkipWhile(e => e.Name != "key" || e.Value != "AssetUUIDs").Skip(1).FirstOrDefault();
        var vacAssetUuids = vacAssetUuidsElem!.Elements("string").Select(s => s.Value).ToList();
        AssertEqual(vacAssetUuids.Count, 2, "Vacation album still has 2 assets");

        // Assets array should contain A1, A2, A3, A4
        AssertEqual(assetsArr!.Elements("dict").Count(), 4, "Global Assets array has 4 assets");
    }

    public static void TestDimensionsProbing()
    {
        // 1. Test PNG dimension probing with synthetic minimal valid PNG
        var tempPng = Path.GetTempFileName() + ".png";
        try
        {
            using (var fs = File.Create(tempPng))
            {
                // PNG magic
                fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
                // IHDR length (13) + "IHDR"
                fs.Write(new byte[] { 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52 });
                // Width = 1920 (0x00000780), Height = 1080 (0x00000438)
                fs.Write(new byte[] { 0x00, 0x00, 0x07, 0x80, 0x00, 0x00, 0x04, 0x38 });
                // Bit depth, color type, compression, filter, interlace
                fs.Write(new byte[] { 0x08, 0x02, 0x00, 0x00, 0x00 });
                // CRC (arbitrary for header test)
                fs.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 });
            }

            var (w, h) = AirTrafficPhotoSyncService.ProbeDimensions(tempPng);
            AssertEqual(w, 1920, "PNG width probed correctly");
            AssertEqual(h, 1080, "PNG height probed correctly");
        }
        finally
        {
            if (File.Exists(tempPng)) File.Delete(tempPng);
        }

        // 2. Test JPEG dimension probing with synthetic minimal valid JPEG SOF0
        var tempJpg = Path.GetTempFileName() + ".jpg";
        try
        {
            using (var fs = File.Create(tempJpg))
            {
                // SOI
                fs.Write(new byte[] { 0xFF, 0xD8 });
                // SOF0 marker (0xFF, 0xC0)
                fs.Write(new byte[] { 0xFF, 0xC0 });
                // Length (17 bytes: 2 bytes length + 1 precision + 2 height + 2 width + 1 components + 3*3)
                fs.Write(new byte[] { 0x00, 0x11 });
                // Precision (8)
                fs.WriteByte(0x08);
                // Height = 600 (0x0258)
                fs.Write(new byte[] { 0x02, 0x58 });
                // Width = 800 (0x0320)
                fs.Write(new byte[] { 0x03, 0x20 });
                // Components
                fs.WriteByte(0x03);
                fs.Write(new byte[] { 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0 });
                // EOI
                fs.Write(new byte[] { 0xFF, 0xD9 });
            }

            var (jw, jh) = AirTrafficPhotoSyncService.ProbeDimensions(tempJpg);
            AssertEqual(jw, 800, "JPEG width probed correctly");
            AssertEqual(jh, 600, "JPEG height probed correctly");
        }
        finally
        {
            if (File.Exists(tempJpg)) File.Delete(tempJpg);
        }
    }

    public static void TestSupportedMedia()
    {
        AssertEqual(AirTrafficPhotoSyncService.IsSupportedMedia("photo.jpg"), true, "jpg is supported");
        AssertEqual(AirTrafficPhotoSyncService.IsSupportedMedia("photo.JPEG"), true, "JPEG is supported");
        AssertEqual(AirTrafficPhotoSyncService.IsSupportedMedia("photo.png"), true, "png is supported");
        AssertEqual(AirTrafficPhotoSyncService.IsSupportedMedia("photo.HEIC"), true, "HEIC is supported");
        AssertEqual(AirTrafficPhotoSyncService.IsSupportedMedia("video.mov"), true, "mov is supported");
        AssertEqual(AirTrafficPhotoSyncService.IsSupportedMedia("video.mp4"), true, "mp4 is supported");

        AssertEqual(AirTrafficPhotoSyncService.IsSupportedMedia("doc.pdf"), false, "pdf not supported");
        AssertEqual(AirTrafficPhotoSyncService.IsSupportedMedia("app.ipa"), false, "ipa not supported");
        AssertEqual(AirTrafficPhotoSyncService.IsSupportedMedia("archive.zip"), false, "zip not supported");
        AssertEqual(AirTrafficPhotoSyncService.IsSupportedMedia(""), false, "empty string not supported");
    }

    public static void TestUtiMappings()
    {
        AssertEqual(AirTrafficPhotoSyncService.GetUtiForExtension(".jpg"), "public.jpeg", "jpg maps to public.jpeg");
        AssertEqual(AirTrafficPhotoSyncService.GetUtiForExtension(".png"), "public.png", "png maps to public.png");
        AssertEqual(AirTrafficPhotoSyncService.GetUtiForExtension(".heic"), "public.heic", "heic maps to public.heic");
        AssertEqual(AirTrafficPhotoSyncService.GetUtiForExtension(".mov"), "com.apple.quicktime-movie", "mov maps to quicktime");
        AssertEqual(AirTrafficPhotoSyncService.GetUtiForExtension(".mp4"), "public.mpeg-4", "mp4 maps to mpeg-4");
    }

    public static void TestManifestGeneration()
    {
        var libUuid = "E7B44431-7B1A-4560-A864-F3F3E8C10001";
        var assetUuid = "B4E38A4B-391B-4C0A-949B-89E38E7E4F28";

        var assets = new List<StagedAssetEntry>
        {
            new StagedAssetEntry(assetUuid, "test.jpg", $"{assetUuid}.jpg", "public.jpeg", 50000, DateTime.UtcNow, 1920, 1080)
        };

        var xml = AirTrafficPhotoSyncService.GeneratePhotoLibraryPlist(libUuid, "IPA Studio", assets);
        AssertEqual(xml.Contains(libUuid), true, "Xml contains library uuid");
        AssertEqual(xml.Contains(assetUuid), true, "Xml contains asset uuid");
        AssertEqual(xml.Contains("IPA Studio"), true, "Xml contains album name");
        AssertEqual(xml.Contains("public.jpeg"), true, "Xml contains uti");
        AssertEqual(xml.Contains("<key>Directory</key>") && xml.Contains("<string>100SYNCD</string>"), true, "Xml contains Directory 100SYNCD for iOS assetsd");

        // Parse XML to verify well-formedness
        var doc = XDocument.Parse(xml);
        AssertEqual(doc.Root != null, true, "Plist XML root parsed");
        AssertEqual(doc.Root!.Name.LocalName, "plist", "Root is plist");
    }

    public static void TestXmlEscaping()
    {
        var libUuid = "E7B44431-7B1A-4560-A864-F3F3E8C10001";
        var assetUuid = "B4E38A4B-391B-4C0A-949B-89E38E7E4F28";

        var assets = new List<StagedAssetEntry>
        {
            new StagedAssetEntry(assetUuid, "Me & You <Special>.jpg", $"{assetUuid}.jpg", "public.jpeg", 1000, DateTime.UtcNow, 0, 0)
        };

        var xml = AirTrafficPhotoSyncService.GeneratePhotoLibraryPlist(libUuid, "Rock & Roll <Live>", assets);
        AssertEqual(xml.Contains("Rock &amp; Roll &lt;Live&gt;"), true, "Album name escaped");
        AssertEqual(xml.Contains("Me &amp; You &lt;Special&gt;.jpg"), true, "Original filename escaped");

        // Parsing will fail if escaping was improper
        var doc = XDocument.Parse(xml);
        AssertEqual(doc.Root != null, true, "Escaped XML is well-formed");
    }

    public static void TestNativeEngineQuery()
    {
        // Must not throw or crash on invocation
        var available = AirTrafficHostApi.IsAvailable;
        AssertEqual(available == true || available == false, true, "IsAvailable returns a valid boolean");

        if (available)
        {
            var res = AirTrafficHostApi.TriggerSync("00000000-0000000000000000", "Photos", "LIB-TEST");
            AssertEqual(res == true || res == false, true, "TriggerSync handles invocation safely");
        }
    }

    private static void AssertEqual<T>(T actual, T expected, string testName)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            throw new InvalidOperationException($"FAILED: {testName}. Expected '{expected}', got '{actual}'.");
        }
        Console.WriteLine($"  [PASS] {testName}");
    }
}
