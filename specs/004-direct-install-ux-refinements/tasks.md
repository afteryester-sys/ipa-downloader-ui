# Tasks: Direct Install UX Refinements & Unicode Filename Support

- [x] [T001] Add `MakeValidFileName` in `DownloadService.cs` preserving Cyrillic/Unicode and update `TryNormalizeIpaFileName` and `BuildDefaultFileName`.
- [x] [T002] Create `SelectableDevice` wrapper and update `DirectDownloadViewModel.cs` with `TargetDevices`, `IsDevicePickerOpen`, `ToggleDevicePickerCommand`, multi-device installation logic.
- [x] [T003] Update `DirectDownloadView.xaml`: increase `MaxWidth` to 960, use `WrapPanel` in Action Row to prevent button clipping.
- [x] [T004] Implement on-demand target device selector in `DirectDownloadView.xaml` with tactile pop & ring selection animations from `OnDeviceView.xaml`.
- [x] [T005] Add and verify localization keys in `Strings.ru.xaml` and `Strings.en.xaml`.
- [x] [T006] Verify build with `dotnet build` and run localization check `scratch/check_loc.py`.
- [x] [T007] Bump version to `1.8.47` in `IPAStudio.App.csproj`.
- [x] [T008] Commit and push changes to trigger GitHub Actions release.
