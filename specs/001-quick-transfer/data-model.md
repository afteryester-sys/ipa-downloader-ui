# Data Model: Quick Transfer

**Feature**: Quick Transfer (001-quick-transfer)
**Date**: 2026-10-01

## Enumerations

### `FileCategory`
Represents the detected type of payload to be transferred.
- `Video`: Movies, video clips, series (`.mp4`, `.mov`, `.mkv`, etc.)
- `Audio`: Songs, audiobooks, podcasts (`.mp3`, `.m4a`, `.flac`, etc.)
- `Book`: E-books, PDFs, comic archives (`.epub`, `.pdf`, `.mobi`, etc.)
- `Document`: Office documents, sheets, text, code (`.docx`, `.xlsx`, `.pages`, etc.)
- `Photo`: Still images, raw photos (`.jpg`, `.png`, `.heic`, etc.)
- `Archive`: Compressed archives (`.zip`, `.7z`, `.rar`, etc.)
- `App`: iOS application archive (`.ipa`)
- `Other`: Unclassified file formats

---

## Records & Models

### `TransferPayload`
Represents an individual file selected or dropped for transfer.
- `FullPath`: `string` - Absolute path on the host filesystem.
- `FileName`: `string` - Base filename.
- `FileSizeBytes`: `long` - Size of file in bytes.
- `Category`: `FileCategory` - Detected category.
- `CategoryDisplayName`: `string` - Localized category label (e.g., "Видео", "Книга").
- `Glyph`: `string` - Segoe MDL2 icon glyph code.

### `FileSharingApp` (Extended)
Represents an installed application on the iOS device capable of receiving files.
- `BundleId`: `string` - Unique iOS bundle identifier (e.g., `org.videolan.vlc-ios`).
- `Name`: `string` - Localized display name (e.g., "VLC").
- `Version`: `string` - App version.
- `IsSystem`: `bool` - True if Apple built-in app.
- `SupportedCategories`: `IReadOnlySet<FileCategory>` - Categories this app natively supports.

### `AppMatchScore`
Represents the compatibility ranking of an installed app for the currently selected file set.
- `App`: `FileSharingApp` - The target application.
- `Score`: `int` - Calculated priority (100 = primary specialized match, 50 = secondary match, 10 = generic container).
- `IsRecommended`: `bool` - True if this app is the top-ranked destination.
- `MatchReason`: `string` - Localized explanation (e.g. "Рекомендуется для видео (VLC)").

### `TransferProgressState`
Real-time state emitted during file upload.
- `CurrentFileIndex`: `int` (1-based index)
- `TotalFiles`: `int`
- `CurrentFileName`: `string`
- `BytesTransferred`: `long`
- `TotalBytes`: `long`
- `Percent`: `double` (0.0 to 100.0)
- `SpeedBps`: `double` (bytes/sec)
- `TargetAppName`: `string`
