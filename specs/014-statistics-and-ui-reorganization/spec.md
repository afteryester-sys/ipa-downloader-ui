# Specification: Statistics Dashboard, Tools Reorganization & Operations Popup Bounds

## 1. Overview
This feature introduces a comprehensive analytics and statistics dashboard ("Статистика"), relocates tool action buttons from the Settings popup directly onto the primary workspace with rich icons and animations, fixes the Operations flyout popup from overflowing outside the main window boundary, and improves task completion notifications with exact item counts (e.g. "Установка 10 приложений успешно завершена").

## 2. Requirements

### 2.1 Statistics & Analytics ("Статистика")
- Provide detailed historical and current statistics by Day, Month, Year, and All Time.
- Track and display:
  - Total and today app installs, and number of unique devices served.
  - Most frequently installed apps in the selected period.
  - Installation methods breakdown (App Catalog, Direct File, Device-to-Device Transfer).
  - Total IPA files downloaded and storage volume.
  - Total IPSW firmwares downloaded and storage volume.
  - Connected devices statistics and most popular device models.
- Visual charts:
  - Interactive activity timeline bar chart (installs/downloads by day/month).
  - Method distribution breakdown.
  - Ranked lists with visual progress bars.
  - Recent activity feed with timestamps and status indicators.
- Seamless persistence to local JSON storage (`statistics.json`).

### 2.2 Tools Reorganization
- Relocate tool buttons (`Скачать IPA`, `Прошивки`, `iCloud`, `Фото с носителя`, and new `Статистика`) from the Settings gear menu into a prominent, elegant Tools toolbar on the main page (`DevicesView`).
- Each tool receives a custom Segoe MDL2 icon, distinct color accent, and fluid tactile hover/press micro-animations.
- Settings menu retains only configuration items: general settings, updates, release notes, diagnostic logs, theme switch, and cache maintenance.

### 2.3 Operations Popup Bounds Fix
- The Operations flyout popup (`OperationsPopup`) opened by clicking the circular badge currently hangs off the right edge of the main window.
- Fix `HorizontalOffset` and alignment so the flyout is strictly constrained within the main window viewport without clipping.

### 2.4 Notification Item Counts
- When batch installation or app transfer completes, notifications and operation cards must state the exact count of items processed (e.g. "Установка 10 приложений успешно завершена").
