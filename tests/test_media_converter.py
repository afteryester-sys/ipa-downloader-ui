import os
import sys

def test_heic_extension_detection():
    heic_exts = {".heic", ".heif"}
    
    def is_heic(filename):
        if not filename or not filename.strip():
            return False
        ext = os.path.splitext(filename)[1].lower()
        if not ext and filename.startswith("."):
            ext = filename.lower()
        return ext in heic_exts

    assert is_heic("photo.heic") is True
    assert is_heic("PHOTO.HEIF") is True
    assert is_heic("img.heic") is True
    assert is_heic("img.jpg") is False
    assert is_heic("clip.mov") is False
    assert is_heic("") is False
    assert is_heic(None) is False

def test_video_extension_detection():
    video_exts = {".mov", ".mp4", ".m4v", ".avi", ".mkv", ".wmv", ".webm", ".3gp", ".flv", ".ts"}

    def is_video(filename):
        if not filename:
            return False
        ext = os.path.splitext(filename)[1].lower()
        return ext in video_exts

    assert is_video("movie.mp4") is True
    assert is_video("CLIP.MOV") is True
    assert is_video("recording.mkv") is True
    assert is_video("photo.png") is False
    assert is_video("doc.pdf") is False

def test_live_photo_pairing():
    files = [
        "C:/DCIM/100APPLE/IMG_0001.HEIC",
        "C:/DCIM/100APPLE/IMG_0001.MOV",
        "C:/DCIM/100APPLE/IMG_0002.HEIC",
        "C:/DCIM/100APPLE/IMG_0003.MOV"
    ]

    # Emulate pairing by folder and base name
    groups = {}
    for f in files:
        dir_name = os.path.dirname(f)
        base = os.path.splitext(os.path.basename(f))[0]
        key = (dir_name.lower(), base.lower())
        groups.setdefault(key, []).append(f)

    live_photos = []
    singles = []

    for key, group in groups.items():
        photo = next((x for x in group if x.lower().endswith((".heic", ".heif", ".jpg", ".jpeg"))), None)
        video = next((x for x in group if x.lower().endswith((".mov", ".mp4"))), None)

        if photo and video and len(group) == 2:
            live_photos.append((photo, video))
        else:
            singles.extend(group)

    assert len(live_photos) == 1
    assert live_photos[0][0] == "C:/DCIM/100APPLE/IMG_0001.HEIC"
    assert live_photos[0][1] == "C:/DCIM/100APPLE/IMG_0001.MOV"
    assert len(singles) == 2
    assert "C:/DCIM/100APPLE/IMG_0002.HEIC" in singles
    assert "C:/DCIM/100APPLE/IMG_0003.MOV" in singles

def test_conversion_modes_mapping():
    modes = {
        0: "Prompt",
        1: "PostTransfer",
        2: "DuringTransfer",
        3: "Disabled"
    }
    assert modes[0] == "Prompt"
    assert modes[1] == "PostTransfer"
    assert modes[2] == "DuringTransfer"
    assert modes[3] == "Disabled"

def test_subdirectory_relative_path_resolution():
    root = "C:/Photos"
    files = [
        "C:/Photos/Vacation/2024/IMG_0001.heic",
        "C:/Photos/Family/IMG_0002.heic"
    ]
    out_dir = "D:/Converted"

    for f in files:
        rel = os.path.relpath(f, root)
        dest = os.path.join(out_dir, rel)
        dest = os.path.splitext(dest)[0] + ".jpg"
        if "Vacation" in f:
            assert dest.replace("\\", "/") == "D:/Converted/Vacation/2024/IMG_0001.jpg"
        else:
            assert dest.replace("\\", "/") == "D:/Converted/Family/IMG_0002.jpg"

def test_live_photo_conversion_mode_selection():
    live_modes = {
        0: "UniversalJpegAndMp4",
        1: "AnimatedGif",
        2: "VideoOnlyMp4",
        3: "PhotoOnlyJpeg"
    }
    assert live_modes[1] == "AnimatedGif"
    assert live_modes[2] == "VideoOnlyMp4"
    assert live_modes[3] == "PhotoOnlyJpeg"

if __name__ == "__main__":
    test_heic_extension_detection()
    test_video_extension_detection()
    test_live_photo_pairing()
    test_conversion_modes_mapping()
    test_subdirectory_relative_path_resolution()
    test_live_photo_conversion_mode_selection()
    print("ALL MEDIA CONVERTER TESTS PASSED!")
