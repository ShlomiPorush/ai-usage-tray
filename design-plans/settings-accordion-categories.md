# Split desktop settings into focused accordion categories

Written against: 87c0f2a8a2189c2b92e71781e1f1058a6bf7d8eb

## Evidence chain

- Surface: `src/costats.App/SettingsWindow.xaml`, `src/costats.App/SettingsWindow.xaml.cs`, `src/costats.App/Services/TrayHost.cs`, and `src/costats.App/GlassWidgetWindow.xaml.cs`
- Problem: The first categorized implementation puts provider actions that deliberately create new five-hour usage windows under General, even though those controls initiate provider activity rather than configure ordinary application behavior. It also hides the manual update check inside Updates, adding an expansion step before a common maintenance action and hiding its feedback when the category is closed. The user explicitly identified both hierarchy problems after inspecting the installed build.
- Design evidence: Shesh desktop commit `5f165c81c41af23e6ea5375481dcd187b2d393c6`, `desktop/ui/index.html`, `desktop/ui/styles.css`, and `desktop/ui/app.js` define the requested accordion exemplar. The current `SettingsWindow.xaml` already separates visual cards from the footer and binds the update action, status, and progress independently, so the existing owners can express both corrections without a new component system.
- Owner: `SettingsWindow` owns settings presentation and window lifecycle. `SettingsViewModel` owns settings values and commands. `TrayHost` and `GlassWidgetWindow` own the two entry points into Settings.
- Scope and affected surfaces: Desktop Settings layout, category open and close behavior, the update-notification entry point, light and dark themes, and the existing fixed settings viewport. All persisted settings and live-save behavior are verification-only.
- Uncertainty: None. The recommended label is Automation because all three moved controls perform the same explicit background action for different providers.

## Design decision

Use eight local WPF accordion cards: General, Automation, Display, Alerts, Accounts, Remote view, Updates, and Local usage data. Move the Claude, Codex, and GLM five-hour-window controls into Automation immediately after General. This keeps ordinary app behavior separate from opt-in actions that create provider activity and keeps Accounts focused on account management.

Move the manual Check for updates action out of Updates and place it immediately beside Restart app in the fixed footer action row. Put Restart app first and Check for updates second. Place the shared update status directly below the version on the right, and show the progress bar only while an update operation is active. Updates continues to own automatic checks, release notes, and the install action. Only one card may be open at a time, and update notifications still open Updates because that entry point promises update details.

Keep the current overall window bounds stable while categories open and close. Size the accordion viewport to the eight collapsed cards instead of reserving empty space between the cards and footer. Preserve the current 360-pixel width, header, automatic saving, version display, restart action, and all control bindings.

## Reuse

- `PanelBgBrush`, `InputBgBrush`, `DividerBrush`, `WindowBorderBrush`, `AccentBrush`, `TextPrimaryBrush`, `TextSecondaryBrush`, `TextMutedBrush`, `ButtonHoverBrush`, and `ButtonPressedBrush` from the active theme dictionaries.
- `SettingsCheckBox`, `SettingsTextBox`, and `SettingsButton` from `SettingsWindow.Resources` for every existing control.
- Existing `CheckForUpdatesCommand`, `UpdateStatusText`, and update-progress bindings for the footer action and feedback area.
- The existing footer `StackPanel`; use a two-column `Grid` for the left action group and the right-aligned version plus status.
- The existing fixed `ScrollViewer` viewport pattern, reduced to the height of the eight collapsed cards.
- Existing `Path` geometry presentation conventions in `src/costats.App/UsageWindow.xaml` for small outline icons and chevrons.
- Exemplar: Shesh `desktop/ui/index.html` settings accordion and `openSettingsSection` in `desktop/ui/app.js` at commit `5f165c81c41af23e6ea5375481dcd187b2d393c6`.

No shared application-wide primitive is required. The accordion appears only in `SettingsWindow`, so its header, icon, chevron, and content styles remain local window resources.

## Changes

1. `src/costats.App/SettingsWindow.xaml`
   - Change: Keep the named accordion container, add an Automation `Expander` after General using the existing `SettingsCategoryExpander` and `SettingsCategoryIcon` styles, and reduce the fixed accordion viewport to remove the blank area beneath the eight collapsed cards. Use only dynamic theme resources.
   - Preserve: Window width, header, close button, footer styling, restart action, version text, scrollbar treatment, all existing settings controls, bindings, commands, copy, validation messages, and nested conditional content.
   - Verify: With every category collapsed, the window shows eight scannable topic cards and both footer actions. Opening any card reveals only that card and closes the previous card. The window size and position do not change.

2. `src/costats.App/SettingsWindow.xaml`
   - Change: Move existing controls without changing their bindings or copy into these exact categories:
     - General: Start at login, refresh interval, and global shortcut.
     - Automation: The Claude, Codex, and GLM automatic five-hour-window actions, preserving their warnings, capability state, and default-off behavior.
     - Display: Overview reset countdowns, remaining-versus-used display, Weekly-versus-Session order, floating panel and its position/account choices, and theme.
     - Alerts: The usage-alert master switch, account choices, and per-account thresholds.
     - Accounts: The monitored-account explanation, account list, primary/edit/remove actions, Add account, and account status message.
     - Remote view: The complete remote-view toggle, endpoints, QR, share-link actions, warnings, and status message.
     - Updates: Automatic update checks, release notes, and install action.
     - Local usage data: Cache explanation, cache summary, clear action, and rescan warning.
     - Footer: Restart app followed immediately by Check for updates in one left-aligned horizontal action group. Keep the version aligned to the right, place update status directly below it, and reveal the full-width progress bar only while an update operation is active.
   - Preserve: Relative ordering inside each topic, conditional visibility, live-save timing, provider-row behavior, remote-link security wording, update progress, and usage-cache behavior.
   - Verify: Every control from the current categorized form exists exactly once after the move. A manual update check gives visible status and progress while Updates is closed. `SettingsViewModel` requires no new setting property and no persisted category state.

3. `src/costats.App/SettingsWindow.xaml.cs`
   - Change: Add Automation to the visual-only category identifier, expander enumeration, and category lookup. Keep `ShowCentered(bool returnToWidgetOnDismiss, SettingsCategory? initialCategory = null)` unchanged otherwise.
   - Preserve: Single Settings window instance, backdrop initialization, drag behavior, dismissal focus handoff, add/edit account dialogs, threshold validation, and all current event handlers.
   - Verify: Reopening Settings after Automation was open starts fully collapsed. Opening Automation then Accounts leaves only Accounts open. Keyboard Enter and Space use the native `Expander` toggle behavior.

4. `src/costats.App/Services/TrayHost.cs`
   - Change: Keep generic tray-menu Settings entry fully collapsed. Change the update-notification click action to open `SettingsCategory.Updates` explicitly.
   - Preserve: Update detection, notification text, widget restoration, notification sound and quiet-time behavior, and every non-update Settings entry point.
   - Verify: Clicking `Settings...` shows all category cards collapsed. Clicking an available-update notification opens Settings with Updates expanded and its available-version panel visible.

5. `src/costats.App/GlassWidgetWindow.xaml.cs`
   - Change: Continue opening Settings without an initial category, using the new optional argument default.
   - Preserve: Settings return-to-widget behavior and focus handoff.
   - Verify: Opening Settings from the widget starts collapsed; closing it restores the widget exactly as before.

6. Focused tests at the nearest available seam
   - Change: Add coverage for the pure category-selection rule if it is extracted from code-behind: normal open selects no category, selecting one replaces the previous selection, and the update entry point selects Updates. If the behavior remains entirely in WPF code-behind and the project has no WPF test host, keep it in manual UI verification rather than introducing a new test framework.
   - Preserve: Existing Core test boundaries and all settings persistence tests.
   - Verify: No category name or open state is written to `settings.json`.

## Scope

- Inherit: All desktop Settings entry points receive the categorized surface and stable viewport.
- Verify: Light and dark themes, normal and update-driven entry points, eight collapsed category cards, adjacent Restart app and Check for updates footer actions, six configured accounts, long account names, remote QR expansion, available-update notes, nested account lists, and the always-visible footer.
- Exclude: Changes to settings values or defaults, localization, the main widget, floating panel, tray tooltip, remote viewer, Web Push behavior, provider sources, remote payloads, server code, merge, release, and production deployment.

## Validation

- Product: Open Settings from the tray, confirm the five-hour-window controls appear only in Automation, click Check for updates beside Restart app without opening a category, and confirm its status remains visible. Do not enable or run any live five-hour-window activation during verification.
- Interface: Inspect the all-collapsed state, Automation, Updates, and the footer actions in light and dark themes. Verify the two footer buttons are adjacent and ordered Restart app then Check for updates, one-open-at-a-time behavior, keyboard toggling, stable window bounds, top-reset on reopen, scrollbar behavior, visible update feedback, long update notes, and 125 percent plus 200 percent Windows display scaling.
- System: Confirm all existing controls and bindings moved exactly once, no category state enters `AppSettings`, and the update notification is the only deep-linked category entry point.
- Repository: `"%LOCALAPPDATA%\dotnet-sdk\dotnet" build costats.sln -c Release` -> zero errors and zero warnings.
- Repository: `"%LOCALAPPDATA%\dotnet-sdk\dotnet" test costats.sln -c Release --no-build` -> all tests pass.
- Repository: Publish `src/costats.App/costats.App.csproj` for `win-x64`, deploy locally, inspect the live Settings window, restore every changed user setting, and verify the original process state is restored.

## Stop conditions

- Stop if an existing control cannot be mapped to exactly one listed category without changing its product meaning.
- Stop if the fixed content viewport exceeds the current Windows work area; clamp the viewport to the work area while keeping it stable for that Settings session.
- Stop if opening Updates from the notification would require changing update state or persistence rather than only presentation routing.
- Stop if moving the update button would leave check, download, verification, or installation feedback hidden while Updates is collapsed.
- Stop before changing remote-view behavior, running any live five-hour-window activation, merging, releasing, or deploying production services.

## Design documentation

- After acceptance and validation: no additional design documentation is required. The categorized structure and one-open-at-a-time behavior remain owned by `SettingsWindow` and this plan.
