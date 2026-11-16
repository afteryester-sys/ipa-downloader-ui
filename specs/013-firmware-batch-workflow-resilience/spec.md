# Feature Specification: Firmware Batch Workflow Resilience, Automation & Performance

## 1. Overview
Ensure that the "Прошивки" (Firmware) section in IPA Studio operates as a fully autonomous, resilient, and performant batch engine:
- The "Запуск" (Start) button initiates a comprehensive automated cycle (disk space check, broken/obsolete file cleanup, partial resume, latest signed downloads) and dynamically toggles to "Остановить" (Stop) while active.
- Interrupted or unfinished downloads (.part files) are automatically detected and resumed across application restarts and network failures.
- Obsolete unsigned firmware versions and corrupted artifacts are automatically pruned.
- An intuitive "История и ошибки" (History & Errors) interface provides categorized event logs (All / Errors / Downloads), an error badge, and auto-dismiss capabilities.
- Zero-copy direct range writing eliminates the slow 12 GB post-download file assembly phase, accelerating completion by up to 100%.
- Fluid WPF animations are added to buttons, sliders, switches, and status transitions.

## 2. Functional Requirements
1. **Dynamic Start / Stop Button**:
   - Idle state: Displays "Запуск" with play icon (`&#xE768;`) in primary/firmware purple.
   - Active state (checking, downloading, or assembling): Displays "Остановить" with stop icon (`&#xE71A;`) in danger/red accent.
   - Forceful execution: Clicking "Запуск" triggers an immediate forceful cycle across all configured/monitored devices without relying on cached data.
   - Graceful cancellation: Clicking "Остановить" gracefully cancels the current workflow and pauses active network streams without corrupting partial files.

2. **Automated Disk & Lifecycle Management**:
   - **Disk Space Pre-check**: Prior to starting any download, inspect available free space on the target drive. If free space is insufficient (`< requiredBytes + 2 GB buffer`), log an error and mark the job appropriately instead of crashing or filling the disk.
   - **Partial Download Resume**: Scan `DestinationFolder` for incomplete `.part` files and `.download.json` manifests, automatically resuming them.
   - **Corrupted File Pruning**: Automatically detect and purge 0-byte, truncated, or unresumable corrupted files.
   - **Obsolete Version Pruning**: Detect locally stored IPSW files for monitored devices whose signing window has closed on Apple TSS, and prune them according to user settings to free up tens of gigabytes of disk space.

3. **Session Persistence & Headless Automation**:
   - The selected devices, auto-check status, check intervals, sequential mode, hash verification, and target folder persist in `SettingsService`.
   - On application startup, if auto-check is enabled, the background timer begins immediately and pending downloads automatically resume without user clicks.

4. **History & Errors (Журнал) Enhancements**:
   - Filter tabs: "Все" (All), "Ошибки" (Errors only), "Загрузки" (Downloads).
   - Prominent error badge on the button showing count of unresolved failures.
   - Auto-dismiss toggle option: popup can auto-close after 5 seconds or via an explicit close [✕] button.
   - Network resilience: On network loss, automatically enters a backoff retry loop with live countdown in the status bar, auto-resuming once the connection is restored.

5. **Assembly Speed Acceleration (Zero-Copy Range Writing)**:
   - In multi-segment mode, write incoming HTTP chunks directly to pre-allocated byte ranges within a single `.part` file using random-access seek offsets.
   - Eliminates the secondary disk copy phase (which previously read and rewrote 12 GB).
   - Post-download finalization becomes an instant metadata rename (`File.Move`).

6. **Micro-interactions & Visual Polish**:
   - Smooth button transitions (scale transforms on click, hover glow).
   - Polished animations for the Auto TSS toggle switch and settings sliders.
