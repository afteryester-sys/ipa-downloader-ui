# Implementation Plan: Smooth "What's New" Changelog Modal

## Architecture & Design Decisions

### 1. Data Model & Release Notes Provider (`IPAStudio.Core/Services/ReleaseNotesService.cs`)
- Define `ReleaseItem`:
  - `Category`: `Feature` (Новое / New), `Improvement` (Улучшение / Improvement), `Fix` (Исправление / Fix).
  - `Title`: Short descriptive title.
  - `Description`: Clear explanation of the change.
- Define `ReleaseNote`:
  - `Version`: string (e.g. "1.8.48").
  - `Date`: string.
  - `Title`: string.
  - `Items`: `IReadOnlyList<ReleaseItem>`.
- `ReleaseNotesService`:
  - Maintains structured notes for current and recent releases (1.8.48, 1.8.47, 1.8.46, etc.) in both Russian and English.
  - Provides `GetReleaseNote(string version, string lang)` and `GetAllReleaseNotes(string lang)`.
  - Fallback logic: if a running version is not statically listed, synthesizes a clean default release note or loads from GitHub release cache.

### 2. Settings Persistence (`IPAStudio.Core/Services/SettingsService.cs`)
- Add property to `AppSettings`:
  - `public string? LastSeenReleaseNotesVersion { get; set; }`
- When the modal is dismissed or closed, update `settings.Current.LastSeenReleaseNotesVersion = currentVersion` and save settings asynchronously.

### 3. ViewModel (`IPAStudio.App/ViewModels/WhatsNewViewModel.cs`)
- Properties:
  - `bool IsOpen`: controls overlay visibility.
  - `string CurrentVersionText`: formatted running version (e.g. "1.8.48").
  - `string Title`: header text.
  - `ObservableCollection<ReleaseNoteViewModel> Notes`: list of release notes (current active version and recent history).
  - `ReleaseNoteViewModel? SelectedNote`: currently displayed note.
- Commands:
  - `CloseCommand`: triggers smooth exit animation, sets `IsOpen = false`, updates `LastSeenReleaseNotesVersion`.
  - `OpenCommand`: manually opens the modal (e.g. from Settings or Menu).
- Startup check:
  - `CheckOnStartup(string currentVersion)`: checks if `LastSeenReleaseNotesVersion != currentVersion`. If so, schedules modal presentation.

### 4. Integration into Shell & Window (`ShellViewModel.cs` & `MainWindow.xaml` / `MainWindow.xaml.cs`)
- Expose `WhatsNewViewModel WhatsNew { get; }` in `ShellViewModel`.
- In `MainWindow.xaml`:
  - Add `Panel.ZIndex="100"` overlay container with semi-transparent backdrop (`#85000000`).
  - Centered card: width 560px, max height 620px, rounded corners (18px), drop shadow/border.
  - Card elements:
    - Rocket / Sparkle icon badge.
    - Title & version badge.
    - Close button `✕`.
    - Scrollable list of categorized items with color-coded pills (Green = Feature, Purple = Improvement, Orange/Amber = Fix).
    - Footer with "Отлично, продолжить" primary button and link to GitHub releases.
- Animation in `MainWindow.xaml.cs` or XAML Storyboards:
  - Entrance: Backdrop opacity 0 -> 1; Card scale 0.94 -> 1.0, translateY 16 -> 0, opacity 0 -> 1 with `CubicEase` (260ms).
  - Exit: Opacity 1 -> 0, scale 1.0 -> 0.96 (180ms), followed by closing.

### 5. Manual Access
- In `SettingsView.xaml`: Add a button "Что нового в этой версии" next to the app version.
- In `MainWindow.xaml` (Menu popup): Add "Что нового" entry to the gear menu.

### 6. Localization
- Parity between `Strings.ru.xaml` and `Strings.en.xaml` verified with `check_loc.py`.
