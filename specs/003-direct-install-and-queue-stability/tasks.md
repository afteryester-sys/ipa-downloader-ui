# Tasks: Direct Install to Device & Queue Stability

- [x] [T001] Fix `QueueService.cs` false cancellation and error escalation in `Parallel.ForEachAsync`.
- [x] [T002] Fix `QueueService.cs` stale token reuse in `RetryAsync`.
- [x] [T003] Fix `DownloadService.cs` internal timeout leaks in `ResolveExternalVersionIdAsync` and `TryLookupFileSizeAsync`.
- [x] [T004] Support permissive account install in `InstallService.cs` when initiated from queue or direct install.
- [x] [T005] Update `DirectDownloadViewModel.cs` with device discovery, selection, and `InstallToDeviceCommand`.
- [x] [T006] Add device selection cards/chips and «Установить на устройство» button to `DirectDownloadView.xaml`.
- [x] [T007] Add localization strings to `Strings.ru.xaml` and `Strings.en.xaml`.
- [x] [T008] Compile solution with `dotnet build` and ensure 0 warnings/errors.
- [x] [T009] Bump version to `1.8.46` in `IPAStudio.App.csproj`.
- [ ] [T010] Commit and push changes to trigger GitHub Actions `auto-release.yml`.
