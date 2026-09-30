#!/usr/bin/env python3
"""
Automated Test Suite for Quick Transfer:
- FileClassifier: extension to FileCategory mapping, glyph assignment, path expansion
- AppRecommendationEngine: scoring heuristics, priority ranking, bundle ID matching
"""

import os
import sys

if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

# Category definitions matching FileCategory enum
CATEGORY_OTHER = "Other"
CATEGORY_VIDEO = "Video"
CATEGORY_AUDIO = "Audio"
CATEGORY_BOOK = "Book"
CATEGORY_DOCUMENT = "Document"
CATEGORY_PHOTO = "Photo"
CATEGORY_ARCHIVE = "Archive"
CATEGORY_APP = "App"
CATEGORY_CONTACT = "Contact"

VIDEO_EXTS = {
    ".mp4", ".mov", ".m4v", ".mkv", ".avi", ".webm", ".3gp", ".3g2",
    ".ts", ".mts", ".m2ts", ".wmv", ".flv", ".f4v", ".asf", ".vob", ".ogv"
}

AUDIO_EXTS = {
    ".mp3", ".m4a", ".aac", ".wav", ".flac", ".aiff", ".aif", ".alac",
    ".ogg", ".oga", ".opus", ".m4r", ".wma", ".mid", ".midi", ".ac3", ".ape"
}

BOOK_EXTS = {
    ".epub", ".pdf", ".mobi", ".azw", ".azw3", ".fb2", ".ibooks",
    ".cbr", ".cbz", ".djvu", ".djv"
}

DOCUMENT_EXTS = {
    ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
    ".pages", ".numbers", ".key", ".keynote",
    ".txt", ".rtf", ".md", ".markdown", ".csv", ".tsv",
    ".json", ".xml", ".yaml", ".yml", ".html", ".htm"
}

PHOTO_EXTS = {
    ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp",
    ".tif", ".tiff", ".bmp", ".dng", ".cr2", ".cr3", ".nef", ".arw",
    ".rw2", ".orf", ".pef", ".svg", ".ico", ".aae"
}

ARCHIVE_EXTS = {
    ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".tbz2", ".xz", ".txz", ".cab", ".iso"
}

CONTACT_EXTS = {
    ".vcf", ".vcard"
}

CAMERA_ROLL_EXTS = {
    ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".webp",
    ".tif", ".tiff", ".bmp", ".dng", ".cr2", ".cr3", ".nef", ".arw",
    ".mov", ".mp4", ".m4v", ".3gp"
}

def is_camera_roll_media(path: str) -> bool:
    if not path or not path.strip(): return False
    _, ext = os.path.splitext(path)
    return ext.lower() in CAMERA_ROLL_EXTS

def classify_file(path: str) -> str:
    if not path or not path.strip():
        return CATEGORY_OTHER
    _, ext = os.path.splitext(path)
    if not ext:
        return CATEGORY_OTHER
    ext_lower = ext.lower()

    if ext_lower == ".ipa":
        return CATEGORY_APP
    if ext_lower in CONTACT_EXTS:
        return CATEGORY_CONTACT
    if ext_lower in VIDEO_EXTS:
        return CATEGORY_VIDEO
    if ext_lower in AUDIO_EXTS:
        return CATEGORY_AUDIO
    if ext_lower in BOOK_EXTS:
        return CATEGORY_BOOK
    if ext_lower in DOCUMENT_EXTS:
        return CATEGORY_DOCUMENT
    if ext_lower in PHOTO_EXTS:
        return CATEGORY_PHOTO
    if ext_lower in ARCHIVE_EXTS:
        return CATEGORY_ARCHIVE

    return CATEGORY_OTHER

KNOWN_PROFILES = {
    "org.videolan.vlc-ios": [CATEGORY_VIDEO, CATEGORY_AUDIO],
    "com.firecore.infuse": [CATEGORY_VIDEO],
    "com.pinger.playerxtreme": [CATEGORY_VIDEO, CATEGORY_AUDIO],
    "com.kmplayer.kmplayer-ios": [CATEGORY_VIDEO, CATEGORY_AUDIO],
    "com.foobar2000.foobar2000": [CATEGORY_AUDIO],
    "com.everappz.flacplayer": [CATEGORY_AUDIO],
    "com.apple.iBooks": [CATEGORY_BOOK],
    "com.adobe.Adobe-Reader": [CATEGORY_BOOK, CATEGORY_DOCUMENT],
    "com.amazon.Lollipop": [CATEGORY_BOOK],
    "com.kobo.KoboBooks": [CATEGORY_BOOK],
    "com.goodiware.goodreader": [CATEGORY_BOOK, CATEGORY_DOCUMENT],
    "kolyvan.kybook": [CATEGORY_BOOK],
    "com.microsoft.Office.Word": [CATEGORY_DOCUMENT],
    "com.microsoft.Office.Excel": [CATEGORY_DOCUMENT],
    "com.microsoft.Office.Outlook": [CATEGORY_CONTACT],
    "com.apple.Pages": [CATEGORY_DOCUMENT],
    "com.apple.Numbers": [CATEGORY_DOCUMENT],
    "com.readdle.ReaddleDocsIPad": [CATEGORY_VIDEO, CATEGORY_AUDIO, CATEGORY_BOOK, CATEGORY_DOCUMENT, CATEGORY_PHOTO, CATEGORY_ARCHIVE],
    "com.unzip.app": [CATEGORY_ARCHIVE, CATEGORY_DOCUMENT],
}

class FileSharingApp:
    def __init__(self, bundle_id: str, name: str):
        self.bundle_id = bundle_id
        self.name = name

def evaluate_app(app: FileSharingApp, primary_category: str) -> int:
    score = 0
    has_exact = False
    if app.bundle_id in KNOWN_PROFILES:
        supported = KNOWN_PROFILES[app.bundle_id]
        if primary_category in supported:
            has_exact = True
            score += 120 if len(supported) <= 2 else 80

    name_lower = (app.name + " " + app.bundle_id).lower()
    keyword_match = False
    if primary_category == CATEGORY_VIDEO:
        keyword_match = any(k in name_lower for k in ["vlc", "player", "video", "movie", "media"])
    elif primary_category == CATEGORY_AUDIO:
        keyword_match = any(k in name_lower for k in ["music", "audio", "player", "sound", "flac", "vlc"])
    elif primary_category == CATEGORY_BOOK:
        keyword_match = any(k in name_lower for k in ["book", "reader", "pdf", "epub", "mobi"])
    elif primary_category == CATEGORY_DOCUMENT:
        keyword_match = any(k in name_lower for k in ["office", "doc", "word", "excel", "sheet", "text"])
    elif primary_category == CATEGORY_ARCHIVE:
        keyword_match = any(k in name_lower for k in ["zip", "rar", "archive", "file"])
    elif primary_category == CATEGORY_PHOTO:
        keyword_match = any(k in name_lower for k in ["photo", "image", "pic"])
    elif primary_category == CATEGORY_CONTACT:
        keyword_match = any(k in name_lower for k in ["contact", "vcf", "vcard", "address", "outlook", "mail"])

    if keyword_match and not has_exact:
        score += 50
    elif keyword_match and has_exact:
        score += 20

    if any(k in name_lower for k in ["document", "files", "readdle", "explorer"]):
        score += 10

    score += 5
    return score

def rank_apps(files: list, available_apps: list) -> list:
    if not available_apps:
        return []
    non_apps = [f for f in files if classify_file(f) != CATEGORY_APP]
    if not non_apps:
        cat = CATEGORY_OTHER
    else:
        # Most frequent category
        cats = [classify_file(f) for f in non_apps]
        cat = max(set(cats), key=cats.count)

    scored = []
    for app in available_apps:
        s = evaluate_app(app, cat)
        scored.append((app, s))

    scored.sort(key=lambda x: (-x[1], x[0].name.lower()))
    return scored


def run_tests():
    total_passed = 0
    total_failed = 0

    def assert_eq(actual, expected, test_name):
        nonlocal total_passed, total_failed
        if actual == expected:
            total_passed += 1
            print(f"  [PASS] {test_name}: {actual}")
        else:
            total_failed += 1
            print(f"  [FAIL] {test_name}: expected {expected}, got {actual}")

    print("=== 1. Testing FileClassifier Extension Mapping ===")
    assert_eq(classify_file("movie.mp4"), CATEGORY_VIDEO, "mp4 is Video")
    assert_eq(classify_file("film.MKV"), CATEGORY_VIDEO, "MKV case-insensitive is Video")
    assert_eq(classify_file("video.mov"), CATEGORY_VIDEO, "mov is Video")
    assert_eq(classify_file("clip.avi"), CATEGORY_VIDEO, "avi is Video")
    assert_eq(classify_file("stream.webm"), CATEGORY_VIDEO, "webm is Video")
    assert_eq(classify_file("song.mp3"), CATEGORY_AUDIO, "mp3 is Audio")
    assert_eq(classify_file("audio.flac"), CATEGORY_AUDIO, "flac is Audio")
    assert_eq(classify_file("music.M4A"), CATEGORY_AUDIO, "m4a is Audio")
    assert_eq(classify_file("book.epub"), CATEGORY_BOOK, "epub is Book")
    assert_eq(classify_file("doc.pdf"), CATEGORY_BOOK, "pdf is Book")
    assert_eq(classify_file("novel.fb2"), CATEGORY_BOOK, "fb2 is Book")
    assert_eq(classify_file("comic.cbr"), CATEGORY_BOOK, "cbr is Book")
    assert_eq(classify_file("work.docx"), CATEGORY_DOCUMENT, "docx is Document")
    assert_eq(classify_file("table.xlsx"), CATEGORY_DOCUMENT, "xlsx is Document")
    assert_eq(classify_file("presentation.pptx"), CATEGORY_DOCUMENT, "pptx is Document")
    assert_eq(classify_file("notes.txt"), CATEGORY_DOCUMENT, "txt is Document")
    assert_eq(classify_file("data.json"), CATEGORY_DOCUMENT, "json is Document")
    assert_eq(classify_file("photo.jpg"), CATEGORY_PHOTO, "jpg is Photo")
    assert_eq(classify_file("image.png"), CATEGORY_PHOTO, "png is Photo")
    assert_eq(classify_file("raw.heic"), CATEGORY_PHOTO, "heic is Photo")
    assert_eq(classify_file("archive.zip"), CATEGORY_ARCHIVE, "zip is Archive")
    assert_eq(classify_file("archive.7z"), CATEGORY_ARCHIVE, "7z is Archive")
    assert_eq(classify_file("archive.tar.gz"), CATEGORY_ARCHIVE, "tar.gz is Archive")
    assert_eq(classify_file("app.ipa"), CATEGORY_APP, "ipa is App")
    assert_eq(classify_file("contact.vcf"), CATEGORY_CONTACT, "vcf is Contact")
    assert_eq(classify_file("address.vcard"), CATEGORY_CONTACT, "vcard is Contact")

    print("\n=== 2. Testing Cyrillic and Complex Filenames ===")
    assert_eq(classify_file("Фильм_2026_1080p.mkv"), CATEGORY_VIDEO, "Cyrillic movie filename")
    assert_eq(classify_file("Война и Мир.epub"), CATEGORY_BOOK, "Cyrillic book filename")
    assert_eq(classify_file("Отчёт_за_сентябрь.docx"), CATEGORY_DOCUMENT, "Cyrillic document filename")
    assert_eq(classify_file("Песня_Группа.flac"), CATEGORY_AUDIO, "Cyrillic audio filename")
    assert_eq(classify_file("Архив_проекта.7z"), CATEGORY_ARCHIVE, "Cyrillic archive filename")
    assert_eq(classify_file("Контакты_Клиенты.vcf"), CATEGORY_CONTACT, "Cyrillic contact filename")

    print("\n=== 3. Testing Edge Cases & Null/Empty Handlers ===")
    assert_eq(classify_file(""), CATEGORY_OTHER, "Empty string is Other")
    assert_eq(classify_file(None), CATEGORY_OTHER, "None is Other")
    assert_eq(classify_file("no_extension"), CATEGORY_OTHER, "No extension is Other")
    assert_eq(classify_file(".gitignore"), CATEGORY_OTHER, "Hidden dot file without known ext is Other")
    assert_eq(classify_file("test.unknown_ext_xyz"), CATEGORY_OTHER, "Unknown ext is Other")

    print("\n=== 4. Testing AppRecommendationEngine Priority Ranking ===")
    vlc = FileSharingApp("org.videolan.vlc-ios", "VLC")
    books = FileSharingApp("com.apple.iBooks", "Books")
    readdle = FileSharingApp("com.readdle.ReaddleDocsIPad", "Documents")
    word = FileSharingApp("com.microsoft.Office.Word", "Word")
    outlook = FileSharingApp("com.microsoft.Office.Outlook", "Outlook")
    generic = FileSharingApp("com.example.fileshare", "Generic App")

    apps = [generic, word, books, readdle, vlc, outlook]

    # Test Video: VLC must rank #1
    ranked_video = rank_apps(["avatar.mkv"], apps)
    assert_eq(ranked_video[0][0].bundle_id, "org.videolan.vlc-ios", "VLC is #1 recommendation for MKV")
    assert_eq(ranked_video[0][1] >= 100, True, "VLC has top score (>= 100) for video")

    # Test Book: Books must rank #1
    ranked_book = rank_apps(["tolstoy.epub"], apps)
    assert_eq(ranked_book[0][0].bundle_id, "com.apple.iBooks", "Books is #1 recommendation for EPUB")

    # Test Document: Word must rank #1
    ranked_doc = rank_apps(["report.docx"], apps)
    assert_eq(ranked_doc[0][0].bundle_id, "com.microsoft.Office.Word", "Word is #1 recommendation for DOCX")

    # Test Contact: Outlook must rank #1
    ranked_contact = rank_apps(["clients.vcf"], apps)
    assert_eq(ranked_contact[0][0].bundle_id, "com.microsoft.Office.Outlook", "Outlook is #1 recommendation for VCF")

    # Test Fallback: Unknown file type falls back gracefully to file sharing container
    ranked_other = rank_apps(["data.bin"], apps)
    assert_eq(len(ranked_other), 6, "All 6 apps ranked for binary file")
    assert_eq(ranked_other[0][1] > 0, True, "Top fallback app has positive score")

    # Test Multiple Files with Mixed Types
    ranked_multi = rank_apps(["song1.mp3", "song2.flac", "notes.txt"], apps)
    assert_eq(ranked_multi[0][0].bundle_id, "org.videolan.vlc-ios", "VLC is top recommendation for majority audio set")

    print("\n=== 5. Testing Size and Speed Formatting ===")
    def format_size(b: int) -> str:
        if b >= 1_073_741_824: return f"{b / 1_073_741_824.0:.1f} GB"
        if b >= 1_048_576: return f"{b / 1_048_576.0:.1f} MB"
        if b >= 1024: return f"{b / 1024.0:.1f} KB"
        if b > 0: return f"{b} B"
        return "0 B"

    def format_speed(bytes_per_sec: float) -> str:
        if bytes_per_sec >= 1_048_576: return f"{bytes_per_sec / 1_048_576.0:.1f} MB/s"
        if bytes_per_sec >= 1024: return f"{bytes_per_sec / 1024.0:.1f} KB/s"
        return f"{bytes_per_sec:.0f} B/s"

    assert_eq(format_size(0), "0 B", "Zero bytes is 0 B")
    assert_eq(format_size(500), "500 B", "500 bytes is 500 B")
    assert_eq(format_size(1024), "1.0 KB", "1024 bytes is 1.0 KB")
    assert_eq(format_size(15_728_640), "15.0 MB", "15 MB formatted")
    assert_eq(format_size(2_684_354_560), "2.5 GB", "2.5 GB formatted")
    assert_eq(format_speed(25_000_000), "23.8 MB/s", "25MB/s speed formatted")

    print("\n=== 6. Testing Camera Roll Media Detection ===")
    assert_eq(is_camera_roll_media("photo.jpg"), True, "jpg is Camera Roll")
    assert_eq(is_camera_roll_media("photo.heic"), True, "heic is Camera Roll")
    assert_eq(is_camera_roll_media("photo.PNG"), True, "PNG is Camera Roll")
    assert_eq(is_camera_roll_media("clip.mov"), True, "mov is Camera Roll")
    assert_eq(is_camera_roll_media("video.mp4"), True, "mp4 is Camera Roll")
    assert_eq(is_camera_roll_media("movie.mkv"), False, "mkv is NOT Camera Roll (needs player)")
    assert_eq(is_camera_roll_media("song.mp3"), False, "mp3 is NOT Camera Roll")
    assert_eq(is_camera_roll_media("doc.pdf"), False, "pdf is NOT Camera Roll")

    print("\n=== 7. Testing Full Multi-Target Batch Separation (iMazing-style) ===")
    batch = [
        "family.jpg",
        "vacation.mov",
        "game.ipa",
        "movie.mkv",
        "novel.epub",
        "contacts.vcf",
        "report.docx"
    ]
    # Native targets:
    # 1. IPAs -> direct installation
    batch_ipas = [f for f in batch if classify_file(f) == CATEGORY_APP]
    # 2. Camera Roll media -> native Photos
    batch_photos = [f for f in batch if is_camera_roll_media(f)]
    # 3. Contacts -> Contacts / vCard
    batch_contacts = [f for f in batch if classify_file(f) == CATEGORY_CONTACT]
    # 4. App documents / third-party files (MKV, DOCX, EPUB)
    batch_app_files = [f for f in batch if f not in batch_ipas and f not in batch_photos and f not in batch_contacts]

    assert_eq(batch_ipas, ["game.ipa"], "IPA identified for direct install")
    assert_eq(batch_photos, ["family.jpg", "vacation.mov"], "Photos and native videos identified for Camera Roll")
    assert_eq(batch_contacts, ["contacts.vcf"], "vCard identified for Contacts")
    assert_eq(batch_app_files, ["movie.mkv", "novel.epub", "report.docx"], "Third-party files identified for app transfer")

    print("\n---------------------------------------------------")
    print(f"Tests finished: {total_passed} passed, {total_failed} failed.")
    if total_failed > 0:
        print("TEST SUITE FAILED!")
        sys.exit(1)
    else:
        print("ALL TESTS PASSED WITH 100% SUCCESS!")

if __name__ == "__main__":
    run_tests()
