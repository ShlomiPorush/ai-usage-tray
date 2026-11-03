# Rebuild desktop Settings as a two-pane window with a navigation rail

Written against: 82bce1d (main). Supersedes `settings-accordion-categories.md`.

## Problem

The owner rejected the current Settings window after daily use: text is too small (labels 12 px, descriptions 10 to 11 px) and the window is too small (360 px wide, a 424 px scroll viewport inside a one-open accordion). A throwaway HTML prototype with three structurally different layouts was reviewed, and the owner chose variant B: a left navigation rail with one category per page, Windows Settings style. The prototype was kept outside the repository.

## Design decision

- Window: 920 x 660 DIP, fixed size, no resize, `SizeToContent="Manual"` (today it is `Height`, which would ignore an explicit height). On a smaller work area (for example 1920 x 1080 at 200 to 250 percent scaling), clamp `Width` and `Height` to the work area so the window always fits. Nothing may become unreachable when clamped: the page content scrolls, the rail's navigation list sits in its own `ScrollViewer` (auto) above the fixed rail footer, and every card uses a two-column grid (`*` text that wraps, `Auto` control) so narrowing squeezes and wraps the text instead of clipping the control.
- Left rail, 248 wide, `WidgetBgBrush` background, right divider: brand block (app name, "Settings"), then eight navigation items (icon + label, 14.5 px), selected item gets `ButtonHoverBrush` background, an `AccentBrush` 3 px indicator bar and accent icon. Navigation items are `RadioButton`s with `AutomationProperties.Name` set to the category name and a visible keyboard-focus border; the list uses `KeyboardNavigation.TabNavigation="Once"` and `DirectionalNavigation="Contained"` (arrows stop at the first and last item; no wrap-around, which a scrolled rail cannot do reliably), so Tab enters the list, arrows move between items and Space selects. A count of accounts beside Accounts. Rail footer, always visible: version, update status text, update progress bar (only while active), Check for updates, Restart app.
- Content pane: page title 28 px semibold, one-line page description 13.5 px, then a scrolling page. Close button at the top right of the pane. The whole window remains draggable from empty areas as today.
- Each setting is its own card (`CardBgBrush` fill, `DividerBrush` border, radius 8, padding 18 x 16, 4 px gap): label 14 px, description 12.5 px, control on the right. Toggles keep the label inside the `CheckBox`: a new `SettingsToggle` template lays the content (label and description) out on the left and draws the 44 x 24 switch on the right, so the whole row is the click target and keyboard focus target. Each toggle also gets `AutomationProperties.Name` set to its label text, because a `StackPanel` content does not give a screen reader a name. Options that depend on a toggle appear inside the same card under a divider. Group headings (14 px semibold) separate clusters within a page. The small check boxes inside account lists (floating panel, alerts) keep a square check-box template, resized.
- Inputs and buttons: 14 px and 13.5 px; ComboBox width about 210.
- Category order unchanged: General, Automation, Display, Alerts, Accounts, Remote view, Updates, Local usage data.
- Display page is grouped as Appearance (Theme, Dark style), Numbers (remaining vs used, resets countdown, Weekly before Session), Floating panel (toggle, position, accounts shown). This moves Theme to the top; no other reordering across pages.
- Only existing theme brushes through `DynamicResource`. No new accent-filled primary button, so no theme dictionary changes.

## Behaviour that must not change

- Every binding, command, event handler, copy string, conditional visibility rule, validation message, live-save timing, and `x:Name` used by code-behind (`ImportConfirmationPanel`, `HistoryDeleteConfirmationPanel`, `SettingsTransferResult`).
- `SettingsViewModel` and `AppSettings` get no new persisted state. The selected page is view state only.
- `SettingsCategory` enum and `ShowCentered(bool, SettingsCategory?)` signature. Entry points: tray menu and widget open General; update notification and widget update link open Updates; account notification opens Accounts; `--settings-screenshot --settings-category X` opens X.
- `BringIntoView` of the import confirmation, transfer result and history-delete confirmation when they appear. Export and import are asynchronous, so the user can switch pages before the result or confirmation appears; that already happens with the accordion (closed expander). Keep the existing handler and do not force a page switch; when the user returns to General the result is there. Verify that switching pages during an export does not throw or jump.
- Update status and progress are visible on every page (rail footer).
- Dismiss and focus handoff to the widget, single window instance, update-available dialog.

## Changes

1. `src/costats.App/SettingsWindow.xaml`
   - Replace the header, accordion `ScrollViewer`/`Expander` stack and footer with a two-column `Grid` (rail, content).
   - Rail: eight `RadioButton`s in one `GroupName`, styled as nav items, `Tag` holding the `SettingsCategory` name; rail footer with the existing update and restart bindings.
   - Content: one `ScrollViewer` (`SettingsScrollViewer`) hosting eight page `StackPanel`s (`GeneralPage` ... `LocalUsageDataPage`), only the selected one visible. Page title and description are set from code-behind on selection.
   - Restyle every control to the card layout; replace `SettingsCategoryExpander`/`SettingsCategoryHeader` styles with `SettingsNavItem`, `SettingsCard`, `SettingsToggle` (switch template for `CheckBox`), and group-heading styles. Small check boxes inside lists (floating-panel accounts, alert accounts) keep a square check-box style at the new size.
2. `src/costats.App/SettingsWindow.xaml.cs`
   - Replace expander enumeration with `PageOf(SettingsCategory)` and `NavItemOf(SettingsCategory)`; `SelectCategory(SettingsCategory)` shows that page, checks its nav item, sets the title and description, and scrolls to top.
   - `ShowCentered` selects `initialCategory ?? General`, clamps size to the work area, centres, activates.
   - Nav `Checked` handler calls `SelectCategory`.
3. `AddAccountWindow.xaml` and `UpdateAvailableWindow.xaml`, the dialogs Settings opens: same type scale (22 px bold title, 14 px labels and inputs, 12.5 to 13.5 px hints, 13.5 px buttons), wider windows (500 and 520), and the accent primary button for Add/Save, as the update dialog already had. Names, events and copy unchanged.
4. `design-plans/settings-accordion-categories.md`: add a "Superseded by settings-sidebar-layout.md" line at the top.

## Validation

- `"%LOCALAPPDATA%\dotnet-sdk\dotnet" build costats.sln -c Release`: zero errors, zero warnings.
- `"%LOCALAPPDATA%\dotnet-sdk\dotnet" test costats.sln -c Release --no-build`: all pass.
- `--settings-screenshot "<path>" --settings-category <X>` for all eight categories in dark, plus General, Display and Accounts in light. Inspect each PNG: no clipped text, no overflow, nothing hidden that was visible before.
- States beyond defaults, by screenshot or live check: import confirmation, history-delete confirmation (open it, then Cancel), floating panel on (position and account list), remote view on (link, QR), update check in progress and its status in the rail, all three themes (Charcoal, Blue steel, Light).
- Keyboard: Tab into the rail, arrows between categories, Space selects; Tab into a page and Space toggles a switch (then toggle it back). Screen-reader names checked with the UI Automation tree (Inspect or a short UIA query), not assumed.
- Small work area: confirm the clamp path by reasoning on the layout plus one run with the window forced to 768 x 500 DIP (a temporary local tweak, not committed), checking that every category and footer action is reachable.
- Local deploy, open from tray (General), open from widget, page switching, update check from a non-Updates page shows status in the rail.
- Live provider activity: never enable or run Automation, and never toggle Keep active. `ApplyPreviewSafety` suppresses Automation and agy in screenshot runs but not Keep active, so any run of the app (screenshot or local deploy) performs the same Keep-active refreshes the installed app performs at every start. No setting is changed to run these checks.
- Grep the diff for U+2014 and for hard-coded colours other than the ones already present.

## Out of scope

`OnboardingWindow`, the widget, theme dictionaries, localisation, any ViewModel or settings logic, release, commit and push.
