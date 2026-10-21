# Feature Specification: Direct Download Queue & Menu Overflow Fix

- **Feature Name**: `direct-download-queue-and-menu-bounds`
- **Target Version**: `1.8.56`
- **Spec Directory**: `specs/010-direct-download-queue`

---

## 1. Context & Motivation

### Issue 1: Settings Menu Height Overflow
In `v1.8.55` and earlier, when opening the settings menu (gear button) on standard or smaller window heights, the bottom of the popup card (`UpdaterPopup`) extended past the bottom edge of the main application window. This exposed the desktop background and covered other windows/taskbars outside the app. The root cause was `ConverterParameter=110` in `ScrollViewer.MaxHeight`, which did not account for the combined vertical space of the gear button margin (`8px`), button height (`34px`), popup offset (`6px`), card margins (`16px`), card padding (`32px`), title bar height (`~32px`), and window border padding (`~16px`). Total non-content vertical height is ~`152px`. Setting `ConverterParameter=165` ensures the popup is always contained inside the window boundaries.

### Issue 2: Direct Download Queue in "Скачать IPA"
Currently, initiating a direct IPA download locks the entire search and input UI until the download completes or errors. The user cannot search for another app, cannot acquire another free license via "Купить", and cannot start concurrent or sequential downloads.
The user requested:
- The ability to add downloads to a queue ("очередь") displayed below the input section.
- The search, lookup, and purchase capabilities remain completely free and responsive immediately after initiating a download.
- Each queue item displays full progress, speed, remaining time, and state.
- Once an item completes, it offers direct installation onto connected iOS devices ("Установить"), folder access ("Папка"), or dismissal.
- Full fluid animations and error handling.

---

## 2. Requirements

### Functional Requirements
1. **Menu Bounds (Window Containment)**:
   - The settings flyout (`UpdaterPopup`) must never extend past the bottom border of the main window on any window size (down to minimum window height 620px).
   - `ScrollViewer.MaxHeight` parameter must be adjusted to `165` to guarantee a clean margin above the bottom window border.

2. **Direct Download Queue**:
   - Introduce `DirectDownloadQueueItem` model to represent an app in the direct download pipeline:
     - App info: `AppEntry`, Name, BundleId, Version, Developer, IconUrl/IconImage.
     - State: `Queued`, `Connecting`, `Transferring`, `Finalizing`, `Done`, `Failed`, `Cancelled`, `Installing`.
     - Progress: Percentage (`0..100`), speed (`SpeedBps`), bytes transferred, total bytes, remaining time.
     - Target destination path: `SavedPath` (resolved .ipa location).
     - State-specific actions: Cancel, Retry, Remove, Open Folder, Install to Device.
   - Introduce queue management in `DirectDownloadViewModel`:
     - `ObservableCollection<DirectDownloadQueueItem> DownloadQueue`.
     - Sequential queue processing with staggering to prevent Apple ID authentication collisions.
     - Automatic transition from `Queued` to `Downloading` as previous downloads complete.
     - Background operations registered in `OperationService` so the top-right status circle tracks active downloads.
   - Unlocked Search & Purchase:
     - After adding an app to the download queue, the search box, lookup, and purchase button remain fully available.
     - The user can look up another app and purchase or queue it immediately.

3. **In-Queue Device Installation**:
   - For any completed download in the queue, an "Установить" (Install) button allows deploying the downloaded IPA directly onto connected iOS devices.
   - Shows device target selection inline on the card, with install progress bar and success confirmation.

4. **Animations & Polish**:
   - Queue items animate into view when added (slide/fade).
   - Progress bar updates smoothly.
   - Status badge transitions between states with appropriate icons and colors.

5. **Localization**:
   - All new strings must be added symmetrically to `Strings.ru.xaml` and `Strings.en.xaml`.
