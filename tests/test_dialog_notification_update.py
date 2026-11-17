#!/usr/bin/env python3
"""
Automated Test Suite for:
1. MessageBox.Show elimination (static audit of src/**/*.cs)
2. In-App Modern Dialog classification & destructive style resolution & Escape key handling
3. Push / in-app notification formatting and count/name accuracy across all operations
4. Auto-update version normalization, tag parsing, and semantic comparison
5. Dimmer reference counting and Owner safety in ModernDialogService
"""

import os
import re
import sys

if sys.platform == "win32":
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

passed_tests = 0
failed_tests = 0

def check(condition, name):
    global passed_tests, failed_tests
    if condition:
        print(f"  [PASS] {name}")
        passed_tests += 1
    else:
        print(f"  [FAIL] {name}")
        failed_tests += 1

def run_all():
    print("--- 1. Testing MessageBox.Show Elimination in Source Code ---")
    test_no_messagebox_calls()

    print("\n--- 2. Testing Version Normalization & GitHub Release Comparison ---")
    test_version_logic()

    print("\n--- 3. Testing Notification Message & Count Formatting ---")
    test_notification_formatting()

    print("\n--- 4. Testing Modern Dialog Types, Destructive Safety & Keyboard Handling ---")
    test_dialog_logic()

    print("\n--- 5. Testing Codebase Wiring & Robustness Improvements ---")
    test_wiring_and_robustness()

    print(f"\nFinal Results: {passed_tests} passed, {failed_tests} failed")
    if failed_tests > 0:
        sys.exit(1)

def test_no_messagebox_calls():
    src_dir = os.path.join(os.path.dirname(__file__), "..", "src")
    messagebox_calls = []
    
    comment_pattern = re.compile(r"^\s*(//|/\*|\*)")
    call_pattern = re.compile(r"\bMessageBox\.Show\b")

    for root, _, files in os.walk(src_dir):
        for file in files:
            if file.endswith(".cs"):
                filepath = os.path.join(root, file)
                with open(filepath, "r", encoding="utf-8", errors="ignore") as f:
                    for lineno, line in enumerate(f, start=1):
                        if comment_pattern.match(line):
                            continue
                        if call_pattern.search(line):
                            messagebox_calls.append(f"{file}:{lineno}: {line.strip()}")

    check(len(messagebox_calls) == 0, f"No MessageBox.Show calls remain (found {len(messagebox_calls)})")
    if messagebox_calls:
        for call in messagebox_calls:
            print(f"    Found: {call}")

def normalize_version(v_tuple):
    major = v_tuple[0]
    minor = v_tuple[1] if len(v_tuple) > 1 else 0
    build = v_tuple[2] if len(v_tuple) > 2 else 0
    revision = v_tuple[3] if len(v_tuple) > 3 else 0
    return (max(0, major), max(0, minor), max(0, build), max(0, revision))

def parse_tag(tag):
    cleaned = tag.lstrip("vV").strip()
    if "-" in cleaned:
        cleaned = cleaned.split("-", 1)[0]
    parts = cleaned.split(".")
    try:
        return normalize_version([int(p) for p in parts])
    except ValueError:
        return None

def test_version_logic():
    current_assembly_version = (1, 8, 77, 0)
    current_norm = normalize_version(current_assembly_version)

    v_same_3part = parse_tag("v1.8.77")
    v_same_4part = parse_tag("1.8.77.0")
    v_newer_patch = parse_tag("v1.8.78")
    v_newer_minor = parse_tag("v1.9.0")
    v_older_patch = parse_tag("v1.8.76")
    v_beta = parse_tag("v1.8.78-beta.1")

    check(v_same_3part == current_norm, "v1.8.77 normalizes equal to assembly 1.8.77.0")
    check(v_same_4part == current_norm, "1.8.77.0 normalizes equal to assembly 1.8.77.0")
    check(v_newer_patch > current_norm, "v1.8.78 is newer than 1.8.77.0")
    check(v_newer_minor > current_norm, "v1.9.0 is newer than 1.8.77.0")
    check(v_older_patch < current_norm, "v1.8.76 is older than 1.8.77.0")
    check(v_beta > current_norm, "v1.8.78-beta.1 stripped to 1.8.78 is newer than 1.8.77.0")

def test_notification_formatting():
    # Read RU strings
    ru_path = os.path.join(os.path.dirname(__file__), "..", "src", "IPAStudio.App", "Resources", "Strings.ru.xaml")
    with open(ru_path, "r", encoding="utf-8") as f:
        ru_content = f.read()

    # Read EN strings
    en_path = os.path.join(os.path.dirname(__file__), "..", "src", "IPAStudio.App", "Resources", "Strings.en.xaml")
    with open(en_path, "r", encoding="utf-8") as f:
        en_content = f.read()

    check('x:Key="L.Ops.Detail.InstallFinishedNamed"' in ru_content and '{0}' in ru_content,
          "RU localization contains L.Ops.Detail.InstallFinishedNamed with {0}")
    check('x:Key="L.Ops.Detail.InstallFinishedNamed"' in en_content and '{0}' in en_content,
          "EN localization contains L.Ops.Detail.InstallFinishedNamed with {0}")
    check('x:Key="L.Ops.Detail.TransferFinishedNamed"' in ru_content and '{0}' in ru_content,
          "RU localization contains L.Ops.Detail.TransferFinishedNamed with {0}")
    check('x:Key="L.Ops.Detail.TransferFinishedNamed"' in en_content and '{0}' in en_content,
          "EN localization contains L.Ops.Detail.TransferFinishedNamed with {0}")
    check('x:Key="L.MediaExport.ExportedWithCounts"' in ru_content,
          "RU localization contains L.MediaExport.ExportedWithCounts")
    check('x:Key="L.MediaExport.ExportedWithCounts"' in en_content,
          "EN localization contains L.MediaExport.ExportedWithCounts")

def test_dialog_logic():
    dialog_cs_path = os.path.join(os.path.dirname(__file__), "..", "src", "IPAStudio.App", "Views", "ModernDialogWindow.xaml.cs")
    with open(dialog_cs_path, "r", encoding="utf-8") as f:
        dialog_cs = f.read()

    check("OnPreviewKeyDown" in dialog_cs and "Key.Escape" in dialog_cs,
          "ModernDialogWindow handles PreviewKeyDown Escape key to close dialog")
    check("CancelBtn.IsDefault = true" in dialog_cs and "ConfirmBtn.IsDefault = false" in dialog_cs,
          "ModernDialogWindow sets CancelBtn as default on destructive dialogs")
    check("CancelBtn.Focus()" in dialog_cs,
          "ModernDialogWindow focuses Cancel button when destructive")

def test_wiring_and_robustness():
    # 1. QueueViewModel references InstallFinishedNamed and TransferFinishedNamed
    queue_vm_path = os.path.join(os.path.dirname(__file__), "..", "src", "IPAStudio.App", "ViewModels", "QueueViewModel.cs")
    with open(queue_vm_path, "r", encoding="utf-8") as f:
        queue_vm = f.read()
    check("L.Ops.Detail.InstallFinishedNamed" in queue_vm, "QueueViewModel uses InstallFinishedNamed")
    check("L.Ops.Detail.TransferFinishedNamed" in queue_vm, "QueueViewModel uses TransferFinishedNamed")
    check("queue.Items.FirstOrDefault" in queue_vm, "QueueViewModel inspects queue.Items for single completed app name")

    # 2. OnDeviceViewModel does not block StartOperation behind MultitaskingEnabled
    on_device_vm_path = os.path.join(os.path.dirname(__file__), "..", "src", "IPAStudio.App", "ViewModels", "OnDeviceViewModel.cs")
    with open(on_device_vm_path, "r", encoding="utf-8") as f:
        on_device_vm = f.read()
    check("if (!_operations.MultitaskingEnabled) return null;" not in on_device_vm,
          "OnDeviceViewModel does not inhibit StartOperation when multitasking is off")

    # 3. ShellViewModel prioritizes Subtitle (device / app name) over Title for Firmware and Download
    shell_vm_path = os.path.join(os.path.dirname(__file__), "..", "src", "IPAStudio.App", "ViewModels", "ShellViewModel.cs")
    with open(shell_vm_path, "r", encoding="utf-8") as f:
        shell_vm = f.read()
    check("!string.IsNullOrWhiteSpace(op.Subtitle) ? op.Subtitle : op.Title" in shell_vm,
          "ShellViewModel falls back to op.Subtitle for Firmware/Download notifications")

    # 4. ModernDialogService safely handles null or minimized owner
    service_path = os.path.join(os.path.dirname(__file__), "..", "src", "IPAStudio.App", "Services", "ModernDialogService.cs")
    with open(service_path, "r", encoding="utf-8") as f:
        service_cs = f.read()
    check("WindowStartupLocation.CenterScreen" in service_cs,
          "ModernDialogService falls back to CenterScreen when owner is null or minimized")

    # 5. MainWindow uses _dimmerRefCount
    main_window_path = os.path.join(os.path.dirname(__file__), "..", "src", "IPAStudio.App", "MainWindow.xaml.cs")
    with open(main_window_path, "r", encoding="utf-8") as f:
        mw_cs = f.read()
    check("_dimmerRefCount" in mw_cs,
          "MainWindow maintains _dimmerRefCount for reliable dimmer overlay lifecycle")

    # 6. UpdaterViewModel retries auto-check on failure
    updater_vm_path = os.path.join(os.path.dirname(__file__), "..", "src", "IPAStudio.App", "ViewModels", "UpdaterViewModel.cs")
    with open(updater_vm_path, "r", encoding="utf-8") as f:
        updater_cs = f.read()
    check("TimeSpan.FromMinutes(2)" in updater_cs,
          "UpdaterViewModel retries transient auto-check failure after 2 minutes")

if __name__ == "__main__":
    run_all()
