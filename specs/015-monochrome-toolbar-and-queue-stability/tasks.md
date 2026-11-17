# Tasks: Monochrome Toolbar, Background Hover Fix & Download Queue Reliability

- [x] Task 1: Fix missing resources and UnsetValue exception on hover <!-- id: 1 -->
  - Add `Color.SurfaceActive`, `Brush.SurfaceActive`, `Brush.Hover`, `Brush.Text.Primary`, `Brush.Text.Secondary` to dark and light palettes.
  - Fix `ToolCardButton` hover background setter in `DevicesView.xaml`.
  - Fix toast text foreground brushes in `MainWindow.xaml`.
- [x] Task 2: Redesign toolbar buttons on DevicesView to unified monochrome style <!-- id: 2 -->
  - Remove multicolored backgrounds (`#18BF5AF2`, `#185AC8FA`, `#1830D158`, `#18FF9F0A`).
  - Standardize all 5 badges on `Brush.AccentSoft` with `Brush.Accent` glyph foreground.
- [x] Task 3: Fix Direct Download queue addition, immediate visibility, and threading <!-- id: 3 -->
  - Fix deduplication check in `DirectDownloadViewModel.cs` to prevent null bundle ID collision.
  - Automatically clear previous finished/failed cards when starting a fresh download.
  - Insert new downloads at top of `DownloadQueue` on UI thread.
  - Execute queue in FIFO order with `LastOrDefault(Queued)`.
  - Marshal all item state, progress, and device collection mutations to the UI Dispatcher.
- [x] Task 4: Bump version to 1.8.71 and release notes <!-- id: 4 -->
  - Update `IPAStudio.App.csproj` to 1.8.71.
  - Add RU and EN release notes in `ReleaseNotesService.cs`.
  - Verify static analysis (0 missing keys, 0 build warnings/errors).
