# Tasks: Smooth "What's New" Changelog Modal

- [x] [T001] Add `LastSeenReleaseNotesVersion` property to `AppSettings` in `SettingsService.cs`.
- [x] [T002] Create `ReleaseNotesService.cs` in `IPAStudio.Core` with structured release notes data (RU and EN) and fallback logic.
- [x] [T003] Create `WhatsNewViewModel.cs` in `IPAStudio.App/ViewModels` and register in DI and `ShellViewModel`.
- [x] [T004] Implement the smooth animated What's New modal overlay in `MainWindow.xaml` and code-behind transition triggers in `MainWindow.xaml.cs`.
- [x] [T005] Add manual triggers in `SettingsView.xaml` and the gear menu in `MainWindow.xaml`.
- [x] [T006] Add localization strings to `Strings.ru.xaml` and `Strings.en.xaml` and verify parity with `check_loc.py`.
- [x] [T007] Verify build with `dotnet build` and run tests.
- [x] [T008] Bump version to `1.8.48` in `IPAStudio.App.csproj`.
- [x] [T009] Commit and push to trigger GitHub Actions release.
