# Feature Specification: Firmware Settings Persistence, Queue Deduplication & UI Polish

**Feature Branch/Folder**: `specs/011-firmware-settings-dedup-and-ui-polish`  
**Status**: Completed  
**Target Release**: v1.8.64  

## 1. Problem Statement & Motivation
Users identified several critical bugs and styling friction points in the Firmware module:
1. **Settings Persistence Failure**: Changing the TSS check interval to minutes (e.g., 1 minute) or modifying thread settings did not persist across application restarts. On restart, the interval was reset back to 60 minutes due to deserialization overwrite of `FirmwareCheckIntervalHours`.
2. **Duplicate Jobs in Queue**: When auto-check runs periodically (e.g., every 1 minute) while a sequential download is in progress, the second absent firmware was repeatedly re-enqueued every minute, creating multiple duplicate rows in the download queue.
3. **Missing Device Download Dates**: Device cards did not indicate when firmware was last downloaded, making it unclear whether a device had downloaded its signed firmware. The top TSS check timestamp also remained static instead of updating dynamically with every periodic check.
4. **Queue Header Layout Collision & Windows Styling Tells**: The bottom queue summary text collided with action buttons at high DPI scaling (125%/150%). Queue buttons lacked fluid hover/press animations and displayed dotted Windows focus rectangles.

## 2. Requirements & User Scenarios
- **R1 (Settings Persistence)**: The TSS check interval, minute/hour mode, download threads, sequential queue mode, and auto-start on launch must persist accurately across application restarts.
- **R2 (Queue Deduplication Guard)**: While a firmware is downloading or waiting in the queue, subsequent periodic checks must never enqueue duplicate jobs for the same device and release.
- **R3 (Device Download Timestamps)**:
  - Each device card must display the date and time of its most recent successful firmware download (`• Загрузка: ДД.ММ.ГГГГ ЧЧ:ММ` with green status dot), or `• Не загружалась` (with muted dot) if none exists.
  - The header timestamp beside the refresh icon must display `Актуально на HH:mm` and dynamically refresh with every TSS check.
- **R4 (Adaptive Queue Layout & Apple Liquid Glass Craft)**:
  - Queue header must use a responsive grid with text clipping to guarantee zero text collision with action buttons at any scale.
  - Queue buttons must follow Apple Liquid Glass design with smooth scale hover (1.025) and press (0.96) transitions and no Windows dotted focus rectangles (`FocusVisualStyle="{x:Null}"`).
  - Action buttons must be contextual (Pause visible when downloads are active, Resume visible when downloads are paused, Clear Finished visible when completed downloads exist).

## 3. Success Metrics
- 0 build errors, 0 warnings.
- 0 duplicate jobs in download queue over repeated 1-minute intervals.
- Correct serialization and retention of 1-minute interval across app relaunch.
- Fluid, collision-free UI conforming to Apple Liquid Glass guidelines and `/unslop` language standards.
