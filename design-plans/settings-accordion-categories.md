# Split desktop settings into focused accordion categories

Written against: 3983cbb5a7d2c71c0ae0eadf730bc66c10ad755d

## Evidence chain

- Surface: `src/costats.App/SettingsWindow.xaml`, `src/costats.App/SettingsWindow.xaml.cs`, `src/costats.App/Services/TrayHost.cs`, and `src/costats.App/GlassWidgetWindow.xaml.cs`
- Problem: The settings window currently renders seven topic areas as one continuous 558-line scrolling form. The addition of per-account floating-panel choices and per-account alert thresholds makes the initial view dense and requires users to scan unrelated controls to reach a specific task. The user explicitly requested the categorized settings pattern used by the Shesh desktop application.
- Design evidence: Shesh desktop commit `5f165c81c41af23e6ea5375481dcd187b2d393c6`, `desktop/ui/index.html`, `desktop/ui/styles.css`, and `desktop/ui/app.js` define the requested exemplar: topic cards with an icon, title, chevron, one open section at a time, and every section collapsed on a normal visit. AI Usage Tray already owns the required panel, border, text, accent, hover, and input brushes in its active WPF theme dictionaries.
- Owner: `SettingsWindow` owns settings presentation and window lifecycle. `SettingsViewModel` owns settings values and commands. `TrayHost` and `GlassWidgetWindow` own the two entry points into Settings.
- Scope and affected surfaces: Desktop Settings layout, category open and close behavior, the update-notification entry point, light and dark themes, and the existing fixed settings viewport. All persisted settings and live-save behavior are verification-only.
- Uncertainty: None for the category model. The user selected the Shesh accordion behavior explicitly.

## Design decision

Replace the continuous settings form with seven local WPF accordion cards: General, Display, Alerts, Accounts, Remote view, Updates, and Local usage data. Only one card may be open at a time. A normal Settings visit starts with every card collapsed and the scroll position at the top. The update notification opens Settings with Updates expanded because that entry point promises update details.

Keep the settings viewport at the existing 640-pixel content height so expanding short and long categories never resizes or repositions the window. Preserve the current 360-pixel width, header, footer, automatic saving, version display, restart action, and all control bindings.

## Reuse

- `PanelBgBrush`, `InputBgBrush`, `DividerBrush`, `WindowBorderBrush`, `AccentBrush`, `TextPrimaryBrush`, `TextSecondaryBrush`, `TextMutedBrush`, `ButtonHoverBrush`, and `ButtonPressedBrush` from the active theme dictionaries.
- `SettingsCheckBox`, `SettingsTextBox`, and `SettingsButton` from `SettingsWindow.Resources` for every existing control.
- The existing 640-pixel `ScrollViewer.MaxHeight` as the stable accordion viewport height.
- Existing `Path` geometry presentation conventions in `src/costats.App/UsageWindow.xaml` for small outline icons and chevrons.
- Exemplar: Shesh `desktop/ui/index.html` settings accordion and `openSettingsSection` in `desktop/ui/app.js` at commit `5f165c81c41af23e6ea5375481dcd187b2d393c6`.

No shared application-wide primitive is required. The accordion appears only in `SettingsWindow`, so its header, icon, chevron, and content styles remain local window resources.

## Changes

1. `src/costats.App/SettingsWindow.xaml`
   - Change: Name the settings `ScrollViewer`, keep a stable 640-pixel viewport, and replace the continuous root `StackPanel` with a named accordion container of seven `Expander` cards. Add a local `SettingsCategoryExpander` template with an `InputBgBrush` surface, `DividerBrush` border, 17-pixel outline icon, title, chevron, hover state, and an expanded content divider. Use only dynamic theme resources.
   - Preserve: Window width, header, close button, footer, restart action, version text, scrollbar treatment, all existing settings controls, bindings, commands, copy, validation messages, and nested conditional content.
   - Verify: With every category collapsed, the window shows seven scannable topic cards without any setting controls. Opening any card reveals only that card and closes the previous card. The window size and position do not change.

2. `src/costats.App/SettingsWindow.xaml`
   - Change: Move existing controls without changing their bindings or copy into these exact categories:
     - General: Start at login, refresh interval, global shortcut, and the Claude, Codex, and GLM automatic five-hour-window actions.
     - Display: Overview reset countdowns, remaining-versus-used display, Weekly-versus-Session order, floating panel and its position/account choices, and theme.
     - Alerts: The usage-alert master switch, account choices, and per-account thresholds.
     - Accounts: The monitored-account explanation, account list, primary/edit/remove actions, Add account, and account status message.
     - Remote view: The complete remote-view toggle, endpoints, QR, share-link actions, warnings, and status message.
     - Updates: Automatic update checks, manual check, progress, release notes, and install action.
     - Local usage data: Cache explanation, cache summary, clear action, and rescan warning.
   - Preserve: Relative ordering inside each topic, conditional visibility, live-save timing, provider-row behavior, remote-link security wording, update progress, and usage-cache behavior.
   - Verify: Every control from the current continuous form exists exactly once after the move. `SettingsViewModel` requires no new setting property and no persisted category state.

3. `src/costats.App/SettingsWindow.xaml.cs`
   - Change: Add one visual-only category identifier and handlers that collapse sibling expanders when a category opens. Add `ShowCentered(bool returnToWidgetOnDismiss, SettingsCategory? initialCategory = null)`. On every call, collapse all categories and scroll to the top, then expand and reveal `initialCategory` only when supplied.
   - Preserve: Single Settings window instance, backdrop initialization, drag behavior, dismissal focus handoff, add/edit account dialogs, threshold validation, and all current event handlers.
   - Verify: Reopening Settings after Display was open starts fully collapsed. Opening Alerts then Accounts leaves only Accounts open. Keyboard Enter and Space use the native `Expander` toggle behavior.

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
- Verify: Light and dark themes, normal and update-driven entry points, six configured accounts, long account names, remote QR expansion, available-update notes, nested account lists, and the always-visible footer.
- Exclude: Changes to settings values or defaults, localization, the main widget, floating panel, tray tooltip, remote viewer, Web Push behavior, provider sources, remote payloads, server code, merge, release, and production deployment.

## Validation

- Product: Open Settings from the tray, expand every category in sequence, modify one reversible control in General, Display, Alerts, and Remote view, and confirm each change still applies and saves immediately. Restore the original values.
- Interface: Inspect all-collapsed state and every expanded category in light and dark themes. Verify one-open-at-a-time behavior, keyboard toggling, stable window bounds, top-reset on reopen, scrollbar behavior, six-account lists, a generated remote QR, long update notes, and 125 percent plus 200 percent Windows display scaling.
- System: Confirm all existing controls and bindings moved exactly once, no category state enters `AppSettings`, and the update notification is the only deep-linked category entry point.
- Repository: `"%LOCALAPPDATA%\dotnet-sdk\dotnet" build costats.sln -c Release` -> zero errors and zero warnings.
- Repository: `"%LOCALAPPDATA%\dotnet-sdk\dotnet" test costats.sln -c Release --no-build` -> all tests pass.
- Repository: Publish `src/costats.App/costats.App.csproj` for `win-x64`, deploy locally, inspect the live Settings window, restore every changed user setting, and verify the original process state is restored.

## Stop conditions

- Stop if an existing control cannot be mapped to exactly one listed category without changing its product meaning.
- Stop if a fixed 640-pixel content viewport exceeds the current Windows work area; clamp the viewport to the work area while keeping it stable for that Settings session.
- Stop if opening Updates from the notification would require changing update state or persistence rather than only presentation routing.
- Stop before changing remote-view behavior, running any live five-hour-window activation, merging, releasing, or deploying production services.

## Design documentation

- After acceptance and validation: no additional design documentation is required. The categorized structure and one-open-at-a-time behavior remain owned by `SettingsWindow` and this plan.
