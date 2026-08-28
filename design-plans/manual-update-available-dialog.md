# Show a clear update dialog after a manual check

Written against: 2d5f1ae15c0699a06c2831f721c7ac89b01afd14

## Evidence chain

- Surface: `src/costats.App/SettingsWindow.xaml`, `src/costats.App/SettingsWindow.xaml.cs`, and `src/costats.App/ViewModels/SettingsViewModel.cs`
- Problem: The user-provided installed-build screenshot shows that a manual update check changes the compact footer status to `Version 2.6.0 is available`, but that text is not actionable. The available-version details and `Update now` action exist only inside the Updates accordion card, so a user who checked from the always-visible footer must discover and open a separate category to continue.
- Design evidence: The same rendered Settings task already presents the complete available-update card, and `AddAccountWindow` establishes the product's owned modal pattern with a centered, themed, non-taskbar dialog. The user explicitly selected a popup containing release details and an update button.
- Owner: `SettingsViewModel` owns manual update checks, the available update, installation state, status, and progress. `SettingsWindow` owns the initiating surface and modal ownership.
- Scope and affected surfaces: Manual update checks started from the Settings footer, the existing Updates category fallback, update download feedback, light and dark themes, and Settings focus restoration.
- Uncertainty: None. The popup applies only to a completed manual check that returns `UpdateAvailable`; background checks retain the existing Windows notification flow.

## Design decision

After a manual `Check for updates` action returns an available update and the check has fully left its busy state, open one owner-centered modal dialog. Show the available version, release notes, current update status and progress, a primary `Update now` action, and a secondary `Later` action. Keep the existing available-update card inside Updates so a dismissed dialog does not remove the user's fallback path.

Do not open this dialog while Settings initializes, when cached availability is refreshed, or after a background check. Background discovery continues to use the existing Windows notification, whose click opens Updates. Prevent duplicate dialogs if the manual command is triggered again while one is open.

## Reuse

- `SettingsViewModel.AvailableUpdateVersion`, `AvailableUpdateNotes`, `UpdateStatusText`, `UpdateProgressPercent`, `IsUpdateProgressVisible`, `IsUpdateProgressIndeterminate`, `IsUpdateBusy`, and `InstallUpdateCommand`.
- `PanelBgBrush`, `InputBgBrush`, `WindowBorderBrush`, `DividerBrush`, `TextPrimaryBrush`, `TextSecondaryBrush`, `TextFaintBrush`, `AccentBrush`, `AccentTextBrush`, `ButtonBgBrush`, `ButtonHoverBrush`, and `ButtonPressedBrush` from the active theme.
- `AddAccountWindow.xaml` as the centered owned-dialog exemplar, including transparent rounded window chrome and local button styling.
- Exemplar: The existing available-update card in `SettingsWindow.xaml` for content order and release-note presentation.

No application-wide dialog primitive is required. The new dialog is update-specific and the existing modal styles are window-local.

## Changes

1. `src/costats.App/ViewModels/SettingsViewModel.cs`
   - Change: Add a presentation event for a freshly completed manual check that found an available update. Retain the manual `UpdateCheckResult`, apply it, clear `IsCheckingForUpdates` and check progress in `finally`, then raise the event only after the busy state is false. Do not raise it from `ApplyBackgroundUpdateResult`, `RefreshUpdateAvailability`, cached Settings initialization, failed checks, or up-to-date results.
   - Preserve: The force-check request, 90-second timeout, status copy, cached availability, `_availableUpdate`, download and staging behavior, restart behavior, and background notification path.
   - Verify: One successful manual result raises one prompt request with `HasAvailableUpdate == true` and `IsUpdateBusy == false`. Up-to-date, failed, canceled, background, and refresh paths raise none.

2. `src/costats.App/SettingsWindow.xaml.cs`
   - Change: Subscribe once to the manual-update prompt event. Open `UpdateAvailableWindow` with `Owner = this` and the same `SettingsViewModel` as its data context. Track the current dialog so a second request activates the existing instance instead of opening another. Clear the reference when it closes and return focus to Settings.
   - Preserve: The single Settings window, accordion behavior, update-notification deep link, dismissal behavior, account dialogs, and widget focus handoff.
   - Verify: A manual update result opens one centered modal over Settings. Closing with `Later`, the close button, or Escape returns to Settings. Rechecking can open a fresh dialog, but never two at once.

3. `src/costats.App/UpdateAvailableWindow.xaml`
   - Change: Add a 380-pixel owner-centered themed dialog. Present `Update available`, `Version {AvailableUpdateVersion}`, a scrollable read-only `AvailableUpdateNotes` area, visible `UpdateStatusText`, conditional progress, a secondary `Later` button, and a primary `Update now` button bound to `InstallUpdateCommand`. Use `Update now` as the initial keyboard focus and default action; use `Later` as the cancel action.
   - Preserve: Existing English update copy, theme identity, rounded window shape, current release-note text, update progress semantics, and button disabled state while `IsUpdateBusy` is true.
   - Verify: Short, empty, and long release notes fit without growing beyond the work area. Light and dark themes use only dynamic resources. Clicking `Update now` leaves the dialog visible for download, verification, error, or restart feedback; it does not create a second installation path.

4. `src/costats.App/UpdateAvailableWindow.xaml.cs`
   - Change: Implement only window lifecycle behavior: close button, `Later`, Escape, and drag from the dialog surface. Keep installation in `SettingsViewModel`.
   - Preserve: The owner's topmost behavior and application shutdown during a successful install.
   - Verify: Dismissing the prompt does not clear `HasAvailableUpdate`; Updates still shows its existing card and action.

5. Focused verification at the nearest available seam
   - Change: Add a unit-testable manual-prompt decision seam only if it can remain free of WPF. Cover available versus up-to-date, manual versus background, and one event after busy-state cleanup. Do not introduce a new WPF test framework solely for this dialog.
   - Preserve: Existing updater flow tests and no real installation during automated tests.
   - Verify: Automated checks never download or install a live release.

## Scope

- Inherit: Every manual update check started from the Settings footer receives the dialog.
- Verify: Manual check with update available, up to date, network failure, timeout, long notes, download progress, install failure, repeated manual checks, Settings close and reopen, light theme, dark theme, 125 percent and 200 percent scaling.
- Exclude: Changing GitHub release parsing, automatic-check cadence, Windows notification copy, background notification routing, installer security, release publishing, localization, and provider activity.

## Validation

- Product: From a locally published build whose version is lower than the public release, click `Check for updates`. Confirm the dialog opens after the check completes, shows the public release details, and leaves Updates as a fallback after `Later`. Do not click `Update now` during a visual-only downgrade test.
- Interface: Inspect the dialog in light and dark themes with short and long release notes. Verify default focus, Enter, Escape, close, modal ownership, no duplicate prompt, visible error and progress states, and Settings focus restoration.
- System: Confirm only manual discovery raises the prompt and the existing `InstallUpdateCommand` remains the sole install owner.
- Repository: `"%LOCALAPPDATA%\dotnet-sdk\dotnet" build costats.sln -c Release` -> zero errors and zero warnings.
- Repository: `"%LOCALAPPDATA%\dotnet-sdk\dotnet" test costats.sln -c Release --no-build` -> all tests pass.

## Stop conditions

- Stop if the prompt would open before `IsCheckingForUpdates` becomes false, because the primary action would remain disabled behind a modal command continuation.
- Stop if a background or cached update result would also open the modal.
- Stop if implementing the dialog requires a second updater or installer path instead of reusing `InstallUpdateCommand`.
- Stop before running a live update installation during automated or visual verification.

## Design documentation

- After acceptance and validation: no additional design documentation is required. The manual-versus-background prompt rule remains owned by the updater presentation flow and this plan.
