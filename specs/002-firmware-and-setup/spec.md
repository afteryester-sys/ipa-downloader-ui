# Feature Specification: Production Firmware IPSW Center & Intelligent Driver Setup Recovery

**Feature Branch**: `specs/002-firmware-and-setup`  
**Created**: 2026-10-05  
**Status**: Ready for Planning  
**Input**: User description: "Давай это все внедряем в программу без ошибок ! А так же нужно сделать проверку - на начальном экране есть проверка установленного софта .(itunes ,драйвера) Так вот не найдя нужных он пытается их установить ,но у программы не выходит . Если вручную удалить старый айтюнс и установить нужный скачанный через кнопку скачать - тогда все начсинает снова работать .) Ну и прошивки внедрить в новую версию . Главное чтоб все работало и работало без ошибок и прога сама подтянула обнову ."

---

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Intelligent iTunes & Apple Driver Setup Recovery (Priority: P1)

When a user starts IPA Studio and reaches the Environment Check ("Проверка окружения") screen, if iTunes or Apple Mobile Device Support (AMDS) drivers are missing or an incompatible legacy version (such as iTunes 12.6.5.3) is present:
The system automatically detects the conflict, explains the situation clearly to the user, offers a 1-click action to remove the outdated conflicting version, and executes the installer interactively or with automatic fallback so installation never stalls silently with an error.

**Why this priority**:
Without working Apple Mobile Device Support drivers, IPA Studio cannot detect USB-connected iPhones/iPads or install applications. An installation failure on the first screen blocks the user completely.

**Independent Test**:
On a machine with legacy iTunes 12.6.5.3 or missing AMDS:
1. The Setup screen displays the exact detected version.
2. Clicking "Установить" or "Удалить старую версию" cleanly removes the conflicting MSI and launches the modern Apple installer.
3. The drivers and iTunes status transition to `DependencyState.Ok` without requiring manual file juggling.

**Acceptance Scenarios**:
1. **Given** a machine with legacy iTunes 12.6.5.3 installed, **When** IPA Studio performs the environment check, **Then** it identifies that the installed iTunes is legacy/incompatible with modern driver bundle requirements, and offers an explicit cleanup action.
2. **Given** a user initiates automatic installation, **When** the silent install fails (e.g. exit code 1603/1618/mismatch), **Then** the application does not simply abort and delete the file; it immediately launches the installer with UI (interactive elevated wizard) so the user can complete the setup.
3. **Given** the installer is downloaded, **When** it completes, **Then** `DependencyService` verifies the `Apple Mobile Device Service` in Windows Services and transitions `DriversState` to `Ok`.

---

### User Story 2 - Complete Production Firmware (IPSW) Center (Priority: P2)

Users can manage official Apple firmware downloads in two distinct modes:
1. **Мои устройства (Auto-check)**: Select from connected or registered devices, check Apple TSS signing status, toggle automatic background update checks (`Включена` ↔ `Отключена`), and download signed firmware.
2. **Одиночное скачивание (One-off Single Download)**: Search or pick any Apple model (iPhone 16 Pro Max, iPad Pro M4, etc.) without adding it to permanent monitored devices, or paste a direct IPSW URL to download immediately.

**Why this priority**:
Provides users with official firmware management directly inside IPA Studio without needing 3rd-party websites (ipsw.me, 4PDA) or iTunes.

**Independent Test**:
Navigate to the "Прошивки" view in the app:
1. Switch between "Мои устройства" and "Одиночное скачивание".
2. Pick any model or paste a link, initiate a download, observe the bottom download dock, pause/resume, and verify the file lands in the configured folder.
3. Click the gear icon to open Detailed Settings (threads, TSS interval, SHA-256 integrity verification, auto-cleanup).

**Acceptance Scenarios**:
1. **Given** the user is in "Одиночное скачивание", **When** they select a model and click "Скачать разово", **Then** the download starts in the bottom dock with the "⚡ Одиночное" badge, remaining on the current view.
2. **Given** the user clicks the Auto-check toggle in the sidebar, **Then** the status switches smoothly between `Включена` and `Отключена` with matching visual indicator.
3. **Given** the user opens IPSW Settings, **Then** they can adjust download threads (2, 4, 8, 16), speed limits, and toggle SHA-256 verification.

---

### User Story 3 - Multi-Segment Task Indicator & Automatic Update Delivery (Priority: P3)

The top-right circular task indicator (`OpsCorner` in `MainWindow.xaml`) accurately represents all active operations:
- When 1 task is running (e.g. Firmware Download): the entire ring is one color (Orange) with `1` in the center.
- When multiple tasks are running (e.g. App Install + Firmware Download): the ring divides into equal colored segments (Blue for App, Orange for IPSW) with the total count in the center.
- Upon release (v1.8.38), the built application detects the new version via GitHub Releases API and offers 1-click update.

**Why this priority**:
Fulfills the user's explicit UX invariant and delivers the new version seamlessly via the built-in updater.

**Independent Test**:
1. Start an IPSW download -> circular indicator shows solid orange ring with `1`.
2. Start an app install concurrently -> circular indicator splits 50/50 (blue/orange) with `2`.
3. In-app updater detects v1.8.38 when published.

---

## Edge Cases

- **Existing Legacy Component Lock**: If `iTunes.exe` or `AppleMobileDeviceService.exe` is currently running during uninstall/install, the application terminates the hanging processes gracefully before executing the installer.
- **Network Interruption during IPSW download**: The download manager supports HTTP Range requests to resume downloads from the last received byte without restarting from 0.
- **Insufficient Disk Space**: Before initiating large IPSW downloads (7-8 GB), the system verifies available disk space and warns the user if free space is below the file size + 1 GB.
- **GitHub API Rate Limiting**: The in-app updater gracefully falls back to `ReleasesListApi` or opens the browser releases page if unauthenticated GitHub API limits are reached.

---

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST detect installed iTunes and AMDS versions, distinguishing between legacy (<= 12.6.5.3), classic 64-bit desktop, and Microsoft Store builds.
- **FR-002**: System MUST provide an uninstaller helper to silently or semi-silently remove conflicting older iTunes versions when required for a clean modern driver installation.
- **FR-003**: System MUST fall back to interactive elevated installation of `iTunes64Setup.exe` if silent `/quiet` installation encounters an error or conflicting setup state.
- **FR-004**: System MUST preserve the downloaded installer executable in Temp or Downloads if an installation fails, rather than immediately deleting it.
- **FR-005**: System MUST implement the complete redesigned `FirmwareView.xaml` matching the approved prototype tokens (`Palette.Dark.xaml` and `Palette.Light.xaml`).
- **FR-006**: System MUST support dual navigation between "Мои устройства" (monitored) and "Одиночное скачивание" (standalone catalog & direct URL).
- **FR-007**: System MUST provide an interactive auto-check toggle with persistent state in `SettingsService`.
- **FR-008**: System MUST provide a multi-threaded IPSW download engine supporting chunked parallel downloads with configurable threads (2, 4, 8, 16).
- **FR-009**: System MUST support SHA-256 checksum verification against official Apple release hashes.
- **FR-010**: System MUST render the `OpsCorner` indicator in `MainWindow.xaml` with dynamic segment splitting based on active task kinds (Blue for Install/Transfer, Orange for IPSW).
- **FR-011**: System MUST increment application version to `1.8.38` in `IPAStudio.App.csproj`.
- **FR-012**: System MUST pass all existing and new unit tests without regression.

---

## Success Criteria

1. **Setup Success Rate**: 100% of users with legacy or corrupted iTunes can recover drivers either automatically or via 1-click clean upgrade.
2. **Firmware Experience**: Users can find and trigger single or monitored firmware downloads in under 3 clicks.
3. **Indicator Accuracy**: Task indicator accurately displays 1 solid color for 1 task and equal multi-color split for N tasks across all operations.
4. **Clean Build**: Zero compilation warnings treated as errors, zero regressions in existing tests.
5. **Seamless Update**: Application checks GitHub Releases and downloads v1.8.38 automatically.
