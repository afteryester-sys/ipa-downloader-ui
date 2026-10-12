# Spec 003: Direct Install to Device & Queue Stability

## Overview
This specification addresses two core requirements:
1. **Direct Install to Device in Direct Download ("Скачать IPA")**:
   Enable users to immediately install downloaded apps onto connected iOS devices from the "Скачать IPA" screen, featuring a stylish device selector (cards/thumbnails with device name, model, and status) matching the application's design system.
2. **Queue Stability & Retry Resilience ("Выбрать" / Batch Install)**:
   Fix the issue where batch installing multiple apps from the catalog ("Выбрать") intermittently gets cancelled or fails with unrecoverable errors on retry.

---

## 1. Direct Install to Device ("Скачать IPA")

### User Scenarios
- **Scenario 1.1**: The user searches for an app by Bundle ID, App Store ID, or App Store link, and downloads it. Once the download finishes (`SavedPath != null`), a primary button «Установить на устройство» appears (or becomes enabled). Clicking it installs the `.ipa` onto the chosen device with live progress (Copying, Installing, Complete).
- **Scenario 1.2**: Multiple devices are connected to the PC. The user sees device chips/thumbnails displaying the device name, model, iOS version, and connection type (USB/Wi-Fi). Clicking a chip selects that device as the target.
- **Scenario 1.3**: No devices are connected. The device selector gracefully displays a muted hint («Подключите iPhone или iPad по USB для установки»). Once plugged in, the device is automatically detected and selected.

### UI & Design Guidelines
- Follow the existing design language of `DirectDownloadView.xaml` (Apple-inspired cards, `Brush.Surface`, `Brush.SurfaceHover`, `Brush.Accent`, `BorderCornerRadius="12"`, typography from `Text.Body` and `Text.Caption`).
- Device selector rendered as horizontal chips / cards with:
  - Device icon (phone/tablet glyph).
  - Device short name and model.
  - Connection indicator (USB / Wi-Fi badge).
  - Selected ring / highlight using `Brush.Accent`.
- Action row:
  - Button «Установить на устройство» styled with `Button.Primary` when ready to install.
  - Slim progress bar and status detail showing installation steps.

---

## 2. Queue Stability & Retry Resilience

### Problems Identified
1. **False Global Cancellation**: When downloading multiple apps in parallel via `Parallel.ForEachAsync`, if a single app encounters an internal socket drop, HTTP timeout, or child CTS expiration (e.g. `ResolveExternalVersionIdAsync` 6s timeout), it throws `OperationCanceledException`. The loop worker re-throws it, causing `Parallel.ForEachAsync` to cancel the entire loop and mark all pending/running apps as `Cancelled`.
2. **Stale Cancellation Token in Retry**: In `QueueService.RetryAsync`, `var token = _cts?.Token ?? standalone!.Token`. If `_cts` had been cancelled, its token remains permanently cancelled. Clicking «Повторить» immediately fails again.
3. **Hard Account Mismatch Blocker in InstallService**: `InstallService.InstallAsync` strictly refuses install when `license.AppleId != device.AppleId`, returning `WrongAccount` even if the user deliberately chose to install the app onto a client's device.
4. **Internal Timeout Leaks in DownloadService**: Inner `cts.CancelAfter(...)` in `ResolveExternalVersionIdAsync` throws `OperationCanceledException` without checking if the parent `ct` was actually cancelled.

### Functional Requirements
- **FR-2.1**: `QueueService` MUST distinguish between user-initiated cancellation (`ct.IsCancellationRequested == true`) and individual task timeouts/network drops (`ct.IsCancellationRequested == false`). Internal timeouts must only fail the individual item, leaving the rest of the batch running.
- **FR-2.2**: `QueueService.RetryAsync` MUST never reuse a cancelled `CancellationToken`. It must always use a fresh token.
- **FR-2.3**: `DownloadService` MUST guard internal timeouts so they do not escape as unhandled `OperationCanceledException`.
- **FR-2.4**: `InstallService` MUST allow installations when the user confirmed proceeding across accounts or when account mismatch is permitted.
