# Feature Specification: Quick Transfer (iMazing-style File & Media Transfer)

**Feature Branch**: `001-quick-transfer`
**Created**: 2026-10-01
**Status**: Draft
**Input**: User description: "Так же нужно проработать кнопку 'быстрый перенос' она должна работать как в imazing - я туда закидываю файлик, программа определяет что за файлик и предлагает в нужное приложение на айфоне внедрить его . Это нужно сделать как в аймейзинг . Прежде чем внедрять - нужно тест сделать обязательно ."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Drag-and-Drop File Analysis & Smart App Suggestion (Priority: P1)

As an iPhone owner using IPA Studio, I want to drop any file (video, book, document, audio, archive, or IPA) into the Quick Transfer window so that the software automatically detects the file category and presents a curated list of compatible apps installed on my device.

**Why this priority**: Core value of the iMazing Quick Transfer paradigm. The user does not need to guess which app accepts `.mkv`, `.epub`, or `.pdf`—the program identifies the file and recommends the best matched app installed on the device.

**Independent Test**: Dropping a `.mkv` video file onto Quick Transfer immediately identifies it as "Video", scans the connected device, and highlights video-capable apps (e.g., VLC, Infuse) as suggested destinations.

**Acceptance Scenarios**:
1. **Given** a connected iOS device with VLC and Apple Books installed, **When** the user drags a video file (`.mkv`, `.mp4`, `.mov`) into the Quick Transfer drop zone, **Then** the UI displays the file name, size, type "Video", and auto-selects or recommends VLC among the target destinations.
2. **Given** a connected iOS device, **When** the user drags an e-book or document (`.epub`, `.pdf`) into the drop zone, **Then** the UI recognizes it as "Book / Document" and recommends reading apps (e.g., Apple Books, Documents by Readdle, Acrobat).
3. **Given** an iOS app package (`.ipa`), **When** dropped into Quick Transfer, **Then** the system identifies it as an application package and prepares it for direct installation on the device without requiring a third-party container.

---

### User Story 2 - Automated Pre-Implementation Test Suite Verification (Priority: P1)

As a developer and user, I want a comprehensive automated test suite executed and passing before any code is deployed to devices or released, ensuring file classification, target app matching, and transfer protocols are verified.

**Why this priority**: Explicit non-negotiable user requirement: "Прежде чем внедрять - нужно тест сделать обязательно." Ensures zero regressions to existing features and verified functionality.

**Independent Test**: Running test execution verifies 100% passing tests for file category detection, app capability resolution, and simulated file upload pipelines.

**Acceptance Scenarios**:
1. **Given** a set of test file descriptors across all supported extensions (video, audio, books, documents, archives, ipas), **When** evaluated by the classifier tests, **Then** all files are correctly mapped to their canonical categories.
2. **Given** a device with known installed bundle IDs, **When** matching candidates for specific file categories, **Then** the matching engine correctly ranks specialized apps above generic file containers.

---

### User Story 3 - Visual Target App Picker & 1-Click Destination Selection (Priority: P2)

As a user with multiple file-sharing apps (e.g., both VLC and Documents), I want a clear visual selector showing candidate apps with their icons and display names so I can easily choose where the file should be placed.

**Why this priority**: Allows user control when multiple compatible apps exist, avoiding unintended destination choices while keeping the flow simple.

**Independent Test**: Dropping an audio file displays all installed audio-capable apps as clickable tiles or list items with the recommended one pre-selected.

**Acceptance Scenarios**:
1. **Given** multiple compatible apps installed on the device, **When** files are analyzed, **Then** the user sees candidate cards with app name, icon, and compatibility badge, and can switch the target app with a single click.
2. **Given** no specialized app is installed for an unusual file type, **When** dropped, **Then** the system presents all available general File Sharing apps (or Files container) with a clear explanation.

---

### User Story 4 - Seamless Real-Time Transfer & Verification (Priority: P2)

As a user transferring a large file (e.g. 2 GB movie), I want real-time transfer progress, speed, and integrity verification so I know the file is safely stored on the device.

**Why this priority**: Large media transfers require feedback and verification so users know when it is safe to disconnect their device.

**Independent Test**: Initiating transfer of a test payload displays moving percentage, current transfer speed (MB/s), and success confirmation with verified byte size.

**Acceptance Scenarios**:
1. **Given** an approved file and destination app, **When** transfer begins, **Then** the UI shows active progress (bytes uploaded / total bytes, speed, ETA) without freezing.
2. **Given** a completed upload, **When** transfer ends, **Then** the software verifies the file exists in the destination container and displays a success badge.
3. **Given** a device disconnection or storage full condition during transfer, **When** interrupted, **Then** the software catches the error, leaves no corrupted lock, and provides a clear localized error message.

---

### Edge Cases

- **No File Sharing Apps on Device**: If the connected device has no third-party apps exposing document sharing, the system clearly advises installing a compatible app (e.g., VLC, Documents, or Books) with a helpful link/tip.
- **Mixed File Types in a Single Drop**: If the user drops both a video (`.mkv`) and an IPA (`.ipa`), the system separates them: IPAs are queued for installation, and media files are routed to the selected media app.
- **Special Characters and Non-ASCII Filenames**: Files with Cyrillic, spaces, or Unicode characters in their names are safely preserved when copied to the app's Documents sandbox.
- **Zero-Byte or Locked Files**: Dropped files that are locked by another Windows process or 0 bytes are validated before beginning transfer, notifying the user immediately.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST provide a "Быстрый перенос" (Quick Transfer) entry point accessible from the connected device panel.
- **FR-002**: System MUST accept dropped files, dropped folders (recursively scanned), and file selection via a standard Windows Browse dialog.
- **FR-003**: System MUST inspect file extensions and MIME signatures to classify files into defined categories: Video, Audio, Books/E-Books, Documents/Office, Photos/Images, Archives, and Applications (IPA).
- **FR-004**: System MUST query the connected device for installed applications supporting File Sharing (`UIFileSharingEnabled` or `LSSupportsOpeningDocumentsInPlace`).
- **FR-005**: System MUST match dropped file categories against known application capabilities (e.g., VLC/Infuse/PlayerXtreme for video; Books/KyBook for books; Pages/Word/Documents for office files) and rank the best match as the default destination.
- **FR-006**: System MUST present a clean visual destination picker displaying candidate apps with icons, display names, and matching rationale.
- **FR-007**: System MUST support 1-click execution to transfer documents into the target app's Documents sandbox via Apple File Conduit / house_arrest.
- **FR-008**: System MUST stream live upload progress including percentage (0-100%), bytes transferred, total size, and current speed.
- **FR-009**: System MUST verify the uploaded file size on the device after transfer completes.
- **FR-010**: System MUST include an automated test suite verifying classification, app matching, and transfer pipeline logic before feature release.

### Key Entities

- **TransferPayload**: Represents a file to be transferred, including source path, filename, byte size, detected category, and compatibility hints.
- **TargetApp**: An application installed on the connected iOS device, including bundle ID, display name, icon/glyph, and supported file categories.
- **QuickTransferSession**: The active transfer session managing device state, file payload list, selected target app, cancellation tokens, and progress stream.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Dropped files are analyzed and classified in under 100 milliseconds.
- **SC-002**: 100% of standard media extensions (`.mkv`, `.mp4`, `.mp3`, `.flac`, `.epub`, `.pdf`, `.ipa`) correctly map to their expected app categories.
- **SC-003**: All unit and contract tests pass with 0 failures prior to deployment.
- **SC-004**: Transfers achieve native USB transfer throughput (typically 25–35 MB/s on USB 2.0 Lightning, 50+ MB/s on USB-C).
- **SC-005**: User can complete a quick transfer in 2 clicks: drop file -> click "Transfer".

## Assumptions

- Connected device is paired and trusted with the host PC.
- Third-party apps supporting file sharing have standard accessible Documents sandboxes via Apple's standard `house_arrest` protocol.
- Windows host system has sufficient disk space for temporary file handles if staging is required.
