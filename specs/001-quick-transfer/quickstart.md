# Quickstart: Quick Transfer (iMazing-Style)

**Feature**: Quick Transfer (001-quick-transfer)
**Date**: 2026-10-01

## 1. Running the Automated Test Suite

Before building or releasing the application, execute the test suite:

```powershell
python build/Run-Tests.py
```
Or with .NET CLI (when .NET SDK is available):
```powershell
dotnet test tests/IPAStudio.Core.Tests/IPAStudio.Core.Tests.csproj
```

**Expected Result**:
- `FileClassifierTests`: 100% PASS (50+ scenarios)
- `AppRecommendationEngineTests`: 100% PASS (ranking VLC for video, Books for epub, fallback)
- `TransferVerificationTests`: 100% PASS

---

## 2. Interactive Testing with IPA Studio

1. Launch IPA Studio (`IPAStudio.exe` or development build).
2. Connect an iPhone or iPad via USB and unlock the screen.
3. On the **Устройства** (Devices) tab, select the connected device card.
4. Click **Быстрый перенос** (Quick Transfer).
5. Drag and drop any file:
   - Drop a movie (e.g. `test.mkv`): UI detects "Видео" and highlights VLC or video player.
   - Drop a book (e.g. `book.epub`): UI detects "Книга" and highlights Apple Books or reader.
   - Drop an app (e.g. `test.ipa`): UI detects "Приложение" and routes to direct install.
6. Click **Перенести** (Transfer).
7. Observe real-time progress bar, speed (MB/s), and completion checkmark.
8. Open the selected app on the iPhone: the file is present in the app's Documents folder.
