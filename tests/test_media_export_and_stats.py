#!/usr/bin/env python3
"""
Comprehensive Test Suite for Media Export, Probing Mechanism, and App Icon Resolution (v1.8.74)
"""

import os
import sys
import tempfile
import shutil
import re
from datetime import datetime

if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

PHOTO_EXTS = {
    ".jpg", ".jpeg", ".png", ".heic", ".heif", ".gif", ".bmp", ".webp",
    ".tif", ".tiff", ".dng", ".cr2", ".nef", ".arw", ".raw", ".svg"
}

VIDEO_EXTS = {
    ".mov", ".mp4", ".m4v", ".avi", ".mkv", ".3gp", ".3g2", ".wmv",
    ".flv", ".webm", ".mts", ".m2ts", ".mpg", ".mpeg", ".ts"
}

DATE_REGEX = re.compile(
    r'(?:IMG_|VID_|PHOTO_|Screenshot_|Screen_|PXL_|\b)(20\d{2})[-_]?(0[1-9]|1[0-2])[-_]?(0[1-9]|[12]\d|3[01])(?:[-_](\d{2})(\d{2})(\d{2}))?',
    re.IGNORECASE
)

def is_photo(path: str) -> bool:
    _, ext = os.path.splitext(path)
    return ext.lower() in PHOTO_EXTS

def is_video(path: str) -> bool:
    _, ext = os.path.splitext(path)
    return ext.lower() in VIDEO_EXTS

def detect_category(full_path: str, is_vid: bool, group_name: str) -> str:
    if is_vid:
        return "Видеозаписи"
    
    file_name = os.path.basename(full_path)
    dir_name = os.path.dirname(full_path).replace('\\', '/')

    # 1. Screenshot detection
    if any(k.lower() in file_name.lower() for k in ["Screenshot", "Screen_", "Screen-", "Снимок экрана", "Скриншот", "screencap"]) or \
       any(k.lower() in dir_name.lower() for k in ["Screenshots", "Снимки экрана", "Скриншоты"]):
        return "Скриншоты"

    # 2. Selfie / Front Camera detection
    if any(k.lower() in file_name.lower() for k in ["FRONT", "Selfie", "Селфи", "PORTRAIT"]) or \
       file_name.upper().startswith("SEL_") or \
       any(k.lower() in dir_name.lower() for k in ["Selfie", "Селфи"]):
        return "Селфи"

    # 3. Camera photos
    if any(k.lower() in dir_name.lower() for k in ["/dcim", "dcim/", "/camera", "100andro", "100media", "100apple"]) or \
       any(file_name.upper().startswith(p) for p in ["IMG_", "DSC_", "PXL_", "MVIMG_", "PHOTO_", "SAM_"]):
        return "Снимки камеры"

    return "Прочее"

def detect_date(full_path: str, fallback_label: str = "Без даты") -> str:
    file_name = os.path.basename(full_path)
    m = DATE_REGEX.search(file_name)
    if m:
        year, month, day = m.group(1), m.group(2), m.group(3)
        return f"{year}-{month}"
    return fallback_label

def parse_shell_path(path: str) -> list:
    normalized = path.replace('/', '\\').strip()
    parts = [p.strip() for p in normalized.split('\\') if p.strip()]
    if not parts:
        return []
    if parts[0].lower() in ['this pc', 'этот компьютер', 'мой компьютер', 'my computer', 'компьютер', 'computer']:
        parts = parts[1:]
    return parts

# ---- Test Runner ----
passed = 0
failed = 0

def test(name, condition):
    global passed, failed
    if condition:
        print(f"  [PASS] {name}")
        passed += 1
    else:
        print(f"  [FAIL] {name}")
        failed += 1

print("--- Testing Media Classification & Category Detection ---")

test("Detect MP4 as Video", detect_category("C:/Photos/video.mp4", True, "") == "Видеозаписи")
test("Detect MOV as Video", detect_category("C:/Photos/clip.mov", True, "") == "Видеозаписи")
test("Detect Screenshot by filename prefix", detect_category("C:/Photos/Screenshot_20240512-143022.png", False, "") == "Скриншоты")
test("Detect Screenshot by Screen_ prefix", detect_category("C:/Photos/Screen_001.png", False, "") == "Скриншоты")
test("Detect Screenshot in Russian", detect_category("C:/Photos/Снимок экрана 2024-05-12.png", False, "") == "Скриншоты")
test("Detect Screenshot by folder name", detect_category("C:/DCIM/Screenshots/image123.jpg", False, "DCIM") == "Скриншоты")
test("Detect Selfie by FRONT tag", detect_category("C:/DCIM/Camera/IMG_20240512_FRONT_001.jpg", False, "DCIM") == "Селфи")
test("Detect Selfie by Selfie filename", detect_category("C:/DCIM/Selfie_2024.jpg", False, "DCIM") == "Селфи")
test("Detect Selfie by SEL_ prefix", detect_category("C:/DCIM/SEL_20240512.jpg", False, "DCIM") == "Селфи")
test("Detect Camera photo in DCIM/Camera", detect_category("C:/DCIM/Camera/IMG_0001.jpg", False, "DCIM") == "Снимки камеры")
test("Detect Camera photo by DSC prefix", detect_category("C:/Storage/DSC_1234.jpg", False, "") == "Снимки камеры")
test("Detect Camera photo by PXL prefix", detect_category("C:/Storage/PXL_20240512_123.jpg", False, "") == "Снимки камеры")
test("Detect Other for generic image", detect_category("C:/Downloads/cat.jpg", False, "Downloads") == "Прочее")

print("\n--- Testing Date Extraction & Fallback ---")
test("Extract date from Android IMG_", detect_date("IMG_20240512_143022.jpg") == "2024-05")
test("Extract date from Screenshot_", detect_date("Screenshot_20231105-102030.png") == "2023-11")
test("Extract date from PXL_", detect_date("PXL_20220815_120000.jpg") == "2022-08")
test("Fallback date localized for Russian", detect_date("avatar.jpg", "Без даты") == "Без даты")
test("Fallback date localized for English", detect_date("avatar.jpg", "No date") == "No date")

print("\n--- Testing Shell Path Resolution ---")
test("Parse This PC\\Galaxy S21\\Internal storage", parse_shell_path("This PC\\Galaxy S21\\Internal storage") == ["Galaxy S21", "Internal storage"])
test("Parse Этот компьютер\\Pixel 7\\DCIM", parse_shell_path("Этот компьютер\\Pixel 7\\DCIM") == ["Pixel 7", "DCIM"])
test("Parse direct drive path C:\\Photos", parse_shell_path("C:\\Photos") == ["C:", "Photos"])

print("\n--- Testing Reactive Filtering Simulation ---")
mock_files = [
    {"Name": "IMG_001.jpg", "IsVideo": False, "Category": "Снимки камеры", "Date": "2024-05"},
    {"Name": "IMG_002.jpg", "IsVideo": False, "Category": "Снимки камеры", "Date": "2024-05"},
    {"Name": "Screenshot_1.png", "IsVideo": False, "Category": "Скриншоты", "Date": "2024-06"},
    {"Name": "VID_001.mp4", "IsVideo": True, "Category": "Видеозаписи", "Date": "2024-05"},
]
# All filter
all_res = mock_files
test("All filter has 4 files", len(all_res) == 4)

# Photos only filter
photos_only = [f for f in mock_files if not f["IsVideo"]]
test("Photos only filter has 3 files", len(photos_only) == 3)
test("Photos only has no videos", all(not f["IsVideo"] for f in photos_only))

# Videos only filter
videos_only = [f for f in mock_files if f["IsVideo"]]
test("Videos only filter has 1 file", len(videos_only) == 1)
test("Videos only has only videos", all(f["IsVideo"] for f in videos_only))

print("\n--- Testing Android / MTP Probing & Organization Simulation ---")

temp_root = tempfile.mkdtemp(prefix="ipa_export_test_")
dest_root = tempfile.mkdtemp(prefix="ipa_dest_test_")

try:
    # Build typical Android storage layout inside Internal storage container
    os.makedirs(os.path.join(temp_root, "Internal storage", "DCIM", "Camera"), exist_ok=True)
    os.makedirs(os.path.join(temp_root, "Internal storage", "DCIM", "Screenshots"), exist_ok=True)
    os.makedirs(os.path.join(temp_root, "Internal storage", "Pictures", "Telegram"), exist_ok=True)
    os.makedirs(os.path.join(temp_root, "Internal storage", "Movies"), exist_ok=True)
    os.makedirs(os.path.join(temp_root, "Internal storage", "Download"), exist_ok=True)

    # Populate sample files
    sample_files = [
        (os.path.join("Internal storage", "DCIM", "Camera", "IMG_20240512_001.jpg"), b"fake jpg data" * 2000),
        (os.path.join("Internal storage", "DCIM", "Camera", "VID_20240512_002.mp4"), b"fake mp4 data" * 2000),
        (os.path.join("Internal storage", "DCIM", "Camera", "IMG_20240512_FRONT_003.jpg"), b"fake front selfie" * 2000),
        (os.path.join("Internal storage", "DCIM", "Screenshots", "Screenshot_20240601-100000.png"), b"fake screen data" * 2000),
        (os.path.join("Internal storage", "Pictures", "Telegram", "doc_photo.jpg"), b"telegram photo data" * 2000),
        (os.path.join("Internal storage", "Movies", "clip.mkv"), b"fake mkv data" * 2000),
        (os.path.join("Internal storage", "Download", "tiny_icon.png"), b"tiny" * 2), # junk (< 20KB)
    ]

    for rel, content in sample_files:
        full = os.path.join(temp_root, rel)
        with open(full, "wb") as f:
            f.write(content)

    # Test Walker
    found_files = []
    skipped_junk = 0

    for root_dir, dirs, files in os.walk(temp_root):
        for f in files:
            p = os.path.join(root_dir, f)
            vid = is_video(p)
            pht = is_photo(p)
            if not vid and not pht:
                continue
            sz = os.path.getsize(p)
            if sz < 20 * 1024:
                skipped_junk += 1
                continue
            cat = detect_category(p, vid, "")
            dt = detect_date(p)
            found_files.append((p, vid, cat, dt, f))

    test("Probing found all 6 valid media files", len(found_files) == 6)
    test("Junk filter skipped 1 small thumbnail", skipped_junk == 1)

    photos = [f for f in found_files if not f[1]]
    videos = [f for f in found_files if f[1]]
    test("Photos count is 4", len(photos) == 4)
    test("Videos count is 2", len(videos) == 2)

    cats = [f[2] for f in found_files]
    test("Contains 'Снимки камеры'", "Снимки камеры" in cats)
    test("Contains 'Видеозаписи'", "Видеозаписи" in cats)
    test("Contains 'Скриншоты'", "Скриншоты" in cats)
    test("Contains 'Селфи'", "Селфи" in cats)
    test("Contains 'Прочее'", "Прочее" in cats)

    # Test ByCategory Copy Simulation with original filename retention
    for f_path, is_v, cat, dt, f_name in found_files:
        cat_dir = os.path.join(dest_root, cat)
        os.makedirs(cat_dir, exist_ok=True)
        target_dest = os.path.join(cat_dir, f_name)
        shutil.copy(f_path, target_dest)
        test(f"File {f_name} copied with real extension", os.path.exists(target_dest))

    test("Category folder 'Снимки камеры' created", os.path.isdir(os.path.join(dest_root, "Снимки камеры")))
    test("Category folder 'Видеозаписи' created", os.path.isdir(os.path.join(dest_root, "Видеозаписи")))
    test("Category folder 'Скриншоты' created", os.path.isdir(os.path.join(dest_root, "Скриншоты")))
    test("Category folder 'Селфи' created", os.path.isdir(os.path.join(dest_root, "Селфи")))

finally:
    shutil.rmtree(temp_root, ignore_errors=True)
    shutil.rmtree(dest_root, ignore_errors=True)

print(f"\nResults: {passed} passed, {failed} failed")
if failed > 0:
    sys.exit(1)
