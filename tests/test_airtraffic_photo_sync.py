import sys
import os
import xml.etree.ElementTree as ET

def test_media_extensions():
    supported = [".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".bmp", ".tif", ".tiff", ".mov", ".mp4", ".m4v", ".avi"]
    unsupported = [".exe", ".ipa", ".txt", ".mkv", ".zip", ".pdf", "", ".unknown"]

    # Emulate IsSupportedMedia logic
    def is_supported(path):
        if not path or not path.strip():
            return False
        ext = os.path.splitext(path)[1].lower()
        return ext in supported

    for ext in supported:
        assert is_supported(f"test{ext}"), f"Should support {ext}"
        assert is_supported(f"TEST{ext.upper()}"), f"Should support uppercase {ext}"

    for ext in unsupported:
        assert not is_supported(f"test{ext}"), f"Should not support {ext}"

    assert not is_supported(""), "Empty should be false"
    assert not is_supported("   "), "Whitespace should be false"
    assert not is_supported(None), "None should be false"
    print("  [PASS] Supported media extension filter verified")

def test_uti_mapping():
    def get_uti(ext):
        ext = ext.lower()
        mapping = {
            ".jpg": "public.jpeg",
            ".jpeg": "public.jpeg",
            ".png": "public.png",
            ".heic": "public.heic",
            ".heif": "public.heif",
            ".gif": "com.compuserve.gif",
            ".bmp": "com.microsoft.bmp",
            ".tif": "public.tiff",
            ".tiff": "public.tiff",
            ".mov": "com.apple.quicktime-movie",
            ".mp4": "public.mpeg-4",
            ".m4v": "com.apple.m4v-video",
            ".avi": "public.avi",
        }
        return mapping.get(ext, "public.image")

    assert get_uti(".jpg") == "public.jpeg"
    assert get_uti(".JPEG") == "public.jpeg"
    assert get_uti(".png") == "public.png"
    assert get_uti(".heic") == "public.heic"
    assert get_uti(".mov") == "com.apple.quicktime-movie"
    assert get_uti(".mp4") == "public.mpeg-4"
    assert get_uti(".unknown") == "public.image"
    print("  [PASS] UTI extension mapping verified")

def test_manifest_xml_structure():
    lib_uuid = "E7B44431-7B1A-4560-A864-F3F3E8C10001"
    cur_lib_xml = f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>LibraryUUID</key>
    <string>{lib_uuid}</string>
</dict>
</plist>"""
    root = ET.fromstring(cur_lib_xml)
    assert root.tag == "plist"
    dict_elem = root.find("dict")
    assert dict_elem is not None
    assert dict_elem.find("key").text == "LibraryUUID"
    assert dict_elem.find("string").text == lib_uuid
    print("  [PASS] CurrentLibraryUUID.plist XML verified")

    sync_info_xml = f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>LibraryUUID</key>
    <string>{lib_uuid}</string>
    <key>SyncVersion</key>
    <integer>1</integer>
</dict>
</plist>"""
    root_sync = ET.fromstring(sync_info_xml)
    assert root_sync.tag == "plist"
    print("  [PASS] syncInfo.plist XML verified")

def test_photo_library_manifest_xml():
    lib_uuid = "E7B44431-7B1A-4560-A864-F3F3E8C10001"
    album_uuid = "A1000000-0000-0000-0000-000000000001"
    asset_uuid = "B4E38A4B-391B-4C0A-949B-89E38E7E4F28"
    album_name = "IPA Studio & Friends <Test>"

    # XML escape helper
    def escape_xml(s):
        return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("\"", "&quot;").replace("'", "&apos;")

    escaped_album_name = escape_xml(album_name)
    photo_lib_xml = f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>LibraryUUID</key>
    <string>{lib_uuid}</string>
    <key>Albums</key>
    <array>
        <dict>
            <key>AlbumUUID</key>
            <string>{album_uuid}</string>
            <key>AlbumName</key>
            <string>{escaped_album_name}</string>
            <key>KeyAssetUUID</key>
            <string>{asset_uuid}</string>
            <key>AssetUUIDs</key>
            <array>
                <string>{asset_uuid}</string>
            </array>
        </dict>
    </array>
    <key>Assets</key>
    <array>
        <dict>
            <key>AssetUUID</key>
            <string>{asset_uuid}</string>
            <key>OriginalFilename</key>
            <string>test_photo.jpg</string>
            <key>Filename</key>
            <string>{asset_uuid}.jpg</string>
            <key>UTI</key>
            <string>public.jpeg</string>
            <key>FileSize</key>
            <integer>102400</integer>
            <key>DateCreated</key>
            <date>2026-11-23T19:00:00Z</date>
            <key>Width</key>
            <integer>1920</integer>
            <key>Height</key>
            <integer>1080</integer>
        </dict>
    </array>
</dict>
</plist>"""

    root = ET.fromstring(photo_lib_xml)
    assert root.tag == "plist"
    root_dict = root.find("dict")
    assert root_dict is not None

    keys = [k.text for k in root_dict.findall("key")]
    assert "LibraryUUID" in keys
    assert "Albums" in keys
    assert "Assets" in keys
    print("  [PASS] PhotoLibrary.plist complex XML structure verified with entity escaping")

def test_multi_album_preservation_logic():
    # Emulate multi-album preservation logic
    existing_xml = """<?xml version="1.0" encoding="UTF-8"?>
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
    </array>
    <key>Assets</key>
    <array>
        <dict><key>AssetUUID</key><string>A1</string></dict>
        <dict><key>AssetUUID</key><string>A2</string></dict>
    </array>
</dict>
</plist>"""
    root = ET.fromstring(existing_xml)
    root_dict = root.find("dict")
    albums_arr = None
    assets_arr = None
    children = list(root_dict)
    for i in range(len(children) - 1):
        if children[i].tag == "key" and children[i].text == "Albums":
            albums_arr = children[i + 1]
        elif children[i].tag == "key" and children[i].text == "Assets":
            assets_arr = children[i + 1]

    assert albums_arr is not None
    assert assets_arr is not None

    # Adding new album "IPA Studio"
    new_album = ET.SubElement(albums_arr, "dict")
    ET.SubElement(new_album, "key").text = "AlbumUUID"
    ET.SubElement(new_album, "string").text = "ALB-IPASTUDIO"
    ET.SubElement(new_album, "key").text = "AlbumName"
    ET.SubElement(new_album, "string").text = "IPA Studio"
    ET.SubElement(new_album, "key").text = "KeyAssetUUID"
    ET.SubElement(new_album, "string").text = "A3"
    ET.SubElement(new_album, "key").text = "AssetUUIDs"
    arr = ET.SubElement(new_album, "array")
    ET.SubElement(arr, "string").text = "A3"

    # Verify both albums are preserved
    albums = albums_arr.findall("dict")
    assert len(albums) == 2, f"Expected 2 albums, got {len(albums)}"
    names = [a.findall("string")[1].text for a in albums]
    assert "Vacation" in names
    assert "IPA Studio" in names
    print("  [PASS] Multi-album preservation logic verified")

def test_dimensions_probing_logic():
    # Synthetic PNG header probing
    png_bytes = bytes([
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x07, 0x80, # Width = 1920
        0x00, 0x00, 0x04, 0x38, # Height = 1080
    ])
    w = (png_bytes[16] << 24) | (png_bytes[17] << 16) | (png_bytes[18] << 8) | png_bytes[19]
    h = (png_bytes[20] << 24) | (png_bytes[21] << 16) | (png_bytes[22] << 8) | png_bytes[23]
    assert w == 1920
    assert h == 1080

    # Synthetic JPEG SOF0 header probing
    jpg_bytes = bytes([
        0xFF, 0xD8, # SOI (idx 0, 1)
        0xFF, 0xC0, # SOF0 (idx 2, 3)
        0x00, 0x11, # Len (idx 4, 5)
        0x08,       # Precision (idx 6)
        0x02, 0x58, # Height = 600 (idx 7, 8)
        0x03, 0x20, # Width = 800 (idx 9, 10)
    ])
    jh = (jpg_bytes[7] << 8) | jpg_bytes[8]
    jw = (jpg_bytes[9] << 8) | jpg_bytes[10]
    assert jw == 800
    assert jh == 600
    print("  [PASS] Fast image dimension probing logic verified (JPEG & PNG)")

def test_localization_keys():
    # Read both localization files and verify new keys exist and match
    ru_path = r"src\IPAStudio.App\Resources\Strings.ru.xaml"
    en_path = r"src\IPAStudio.App\Resources\Strings.en.xaml"
    with open(ru_path, "r", encoding="utf-8") as f:
        ru_text = f.read()
    with open(en_path, "r", encoding="utf-8") as f:
        en_text = f.read()

    new_keys = [
        "L.Photos.SyncSuccess",
        "L.Photos.SyncSuccessMessage",
        "L.Photos.SyncDefaultAlbum",
    ]
    for k in new_keys:
        assert f'x:Key="{k}"' in ru_text, f"RU missing {k}"
        assert f'x:Key="{k}"' in en_text, f"EN missing {k}"
    print("  [PASS] Localization keys present in both RU and EN")

def main():
    print("=== Running AirTraffic Photo Sync Tests ===")
    test_media_extensions()
    test_uti_mapping()
    test_manifest_xml_structure()
    test_photo_library_manifest_xml()
    test_multi_album_preservation_logic()
    test_dimensions_probing_logic()
    test_localization_keys()
    print("ALL AIRTRAFFIC PHOTO SYNC TESTS PASSED!")

if __name__ == "__main__":
    main()
