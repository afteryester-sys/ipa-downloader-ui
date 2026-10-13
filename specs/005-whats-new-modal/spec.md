# Spec 005: Smooth "What's New" Changelog Modal on First Launch After Update

## 1. Problem Statement & Motivation
When IPA Studio receives updates, users often do not know what was added, improved, or fixed without manually checking the GitHub releases page.
The user requested:
> "можешь подготовить в следующей обнове чтоб после каждой твоей обновы и обновлении программы - при первом запуске с плавной анимациекй появлялось окошко на один раз где будет написано кратко что поменялось и что обновилось."

## 2. Requirements & User Scenarios

### Scenario 1: First Launch After App Update
- Given the application has been updated to a new version (e.g. `v1.8.48`),
- When the user launches IPA Studio for the first time on this version,
- Then a modal overlay ("Что нового в IPA Studio") appears automatically with a fluid entrance animation (smooth backdrop fade-in and subtle card scale/translate pop).
- The modal displays the version number, status badge, and human-readable categorized highlights (Новое, Улучшение, Исправление) in the current UI language.
- When the user dismisses the modal (clicking "Отлично, продолжить", the close `✕` button, clicking outside the card, or pressing Esc), the modal smoothly animates out and closes.
- The running version is recorded in persistent settings (`LastSeenReleaseNotesVersion`), so the modal **never appears again** on subsequent launches of this version.

### Scenario 2: Subsequent Launches of the Same Version
- Given the user has already seen or dismissed the "What's New" modal for `v1.8.48`,
- When the user restarts the application,
- Then no modal is shown and the application starts directly into its normal view.

### Scenario 3: Future Updates
- When a future version is deployed (e.g. `v1.8.49`),
- On first launch of `v1.8.49`, `LastSeenReleaseNotesVersion` ("1.8.48") does not match "1.8.49",
- Therefore the modal is shown once for `v1.8.49` with the new changes, and then suppressed until the next update.

### Scenario 4: Manual Revisit from Settings / Menu
- Users can review the changelog at any time by clicking "Что нового в этой версии" in Settings or from the main Menu gear popup.

## 3. Success Criteria
1. Modal appears exactly once per updated version upon initial startup.
2. Fluid, non-jarring 60fps entrance and exit animations following Apple/WPF motion guidelines.
3. Clean, readable layout with category tags, version badge, and scrollable content.
4. Strict RU and EN localization parity for all UI text and changelog strings.
5. All tests and builds compile with zero errors.
