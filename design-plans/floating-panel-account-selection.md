# Make the floating panel selectable and visually identical to the tray tooltip

Written against: 480035bc9b6a098c1ddff40d7a114c0e31a28419

## Evidence chain

- Surface: `src/costats.App/Services/TrayStatusPanelWindow.xaml`, `src/costats.App/Services/TrayStatusPanelWindow.xaml.cs`, and the floating-panel controls in `src/costats.App/SettingsWindow.xaml`
- Problem: The floating panel pairs two accounts on each visual line, uses provider-specific text colours, and has a separate surface treatment. With six configured accounts this reads as three grouped rows and does not match the tray tooltip. The user also needs a persistent way to choose which accounts remain visible on this always-on surface.
- Design evidence: The tray tooltip row composition in `src/costats.App/Services/TrayHost.cs` uses an 8px risk dot from `UsageBands`, a semibold account name with `TextPrimaryBrush`, muted quota text with `TextMutedBrush`, and a `PanelBgBrush` / `WindowBorderBrush` surface. `AGENTS.md` requires all quota status colours to come from canonical used percent through `UsageBands`, regardless of provider or used/remaining display mode.
- Owner: `TrayHost` owns pulse-to-row composition and the tray tooltip. `TrayStatusPanelWindow` owns the persistent floating surface. `SettingsViewModel` and `AppSettings` own live user preferences.
- Scope and affected surfaces: Desktop tray tooltip, floating panel, Settings account selection, settings persistence, relevant Core and settings tests, and README settings documentation. The main widget and web viewer are verification-only consumers.
- Uncertainty: None for the selected design. The tooltip is the accepted visual exemplar and must remain visually unchanged.

## Design decision

Render one account per row in the floating panel and use the exact same shared WPF row presenter as the tray tooltip. The row presenter must own the risk dot, typography, spacing, text brushes, empty state, and `UsageBands` colour mapping for both consumers. The floating window keeps only its persistent-window behavior, drag handling, and close control.

Add a persistent per-account selection beneath the floating-panel setting. All currently configured accounts are selected by default, newly added accounts are selected by default, and at least one account must remain selected. Selection changes apply immediately without restarting and affect only the floating panel. The tray tooltip, main widget, remote viewer, and primary-account behavior continue to include their existing account sets.

Persist hidden provider IDs rather than selected IDs. An empty hidden list means all accounts are shown and automatically gives newly added accounts the sensible visible default.

## Reuse

- `TrayAccountRow` and `TrayStatusComposer.ComposeRows` for the account name, quota text, ordering, used/remaining preference, and worst canonical used percent.
- `UsageBands` and `BandPalette.Vivid` for the 8px account risk dot.
- `PanelBgBrush`, `WindowBorderBrush`, `TextPrimaryBrush`, `TextMutedBrush`, `ButtonHoverBrush`, and `ButtonPressedBrush` from the active theme dictionaries.
- `SettingsCheckBox` for the main toggle and nested account choices.
- `ProviderRowViewModel.ProviderId` as the stable selection key for Claude, Codex, Z.AI, and Copilot rows.
- Exemplar: `TrayHost.RebuildTooltipRows` and the tooltip border created in the `TrayHost` constructor.

No existing shared row primitive exists. Extract one from the tooltip implementation because two surfaces are now explicitly required to remain visually identical. Both consumers must call that primitive rather than keep parallel row-building code.

## Changes

1. `src/costats.App/Services/TrayAccountRowsPresenter.cs` or an equivalently scoped shared WPF presenter
   - Change: Extract the current tooltip row construction into one shared presenter that populates a vertical panel from `IReadOnlyList<TrayAccountRow>`. It must render one row per account with the existing 8px risk dot, 8px dot-to-label gap, 12px semibold account name, 12px muted quota text, and 2px vertical row margins. Keep the existing no-data copy and unknown grey dot.
   - Preserve: The tray tooltip's current appearance, full account set, tooltip wording, weekly/session ordering, and used/remaining behavior.
   - Verify: Feeding six rows creates six vertical visual rows; changing only the provider name never changes the risk colour; the tooltip before and after extraction is visually unchanged.

2. `src/costats.App/Services/TrayHost.cs`
   - Change: Replace `RebuildTooltipRows` and its local `UsedColor` implementation with the shared presenter. Preserve provider IDs alongside `AccountUsageStatus` while composing the pulse so the floating subset can be filtered before producing its `TrayAccountRow` list. Pass all rows to the tooltip and only selected rows to `TrayStatusPanelWindow`.
   - Preserve: Primary-account ordering, tray icon selection, visibility rules from `TrayAccountFilter`, hover retention, tooltip positioning, and all non-floating consumers.
   - Verify: With six provider readings, the tooltip receives six rows regardless of the floating selection. Selecting two provider IDs sends exactly those two rows to the floating panel in the same established order.

3. `src/costats.App/Services/TrayStatusPanelWindow.xaml` and `src/costats.App/Services/TrayStatusPanelWindow.xaml.cs`
   - Change: Accept `IReadOnlyList<TrayAccountRow>` and delegate all row rendering to the shared presenter. Remove the two-accounts-per-line composition and provider-name colour mapping. Match the tooltip surface exactly with corner radius 8, padding `12,9,12,9`, `PanelBgBrush`, and `WindowBorderBrush`. Keep the close button in the upper-right using the standard text and button-state brushes.
   - Preserve: Always-on-top behavior, no activation, drag movement, close behavior, initial taskbar-relative positioning, and live refresh.
   - Verify: Six selected accounts appear as six distinct rows; a two-account subset produces two rows; the close button disables the setting; the panel remains movable and topmost.

4. `src/costats.Core/Tray/TrayStatusComposer.cs` and `tests/costats.Core.Tests/Tray/TrayStatusComposerTests.cs`
   - Change: Remove `TrayCompactRow`, `ComposeCompactRows`, and tests that exist only for the separate abbreviated floating composition. Keep or move the weekly-before-session assertions to `ComposeRows`, because both tooltip surfaces now consume that canonical row model.
   - Preserve: `TrayAccountRow`, tooltip text, reset-time formatting, canonical used-percent severity, and remaining-percent display.
   - Verify: Existing tray composition tests still cover weekly/session ordering and used/remaining output without a second compact model.

5. `src/costats.Application/Settings/AppSettings.cs`
   - Change: Add `FloatingPanelHiddenProviderIds` as a persisted list with an empty default. Add case-insensitive helpers to query and update visibility using full pulse provider IDs such as `claude:claude-1`, `codex:codex-1`, `zai`, and `copilot`. Ignore blank IDs and normalize duplicate entries before save or mutation.
   - Preserve: `ShowFloatingStatusPanel`, the legacy `keepWidgetOpen` migration, account IDs, and all existing defaults.
   - Verify: Default settings show every account; hiding one ID affects only that ID case-insensitively; an unknown or newly added provider remains visible by default.

6. `src/costats.App/ViewModels/ProviderRowViewModel.cs` and `src/costats.App/ViewModels/SettingsViewModel.cs`
   - Change: Expose each row's floating-panel selection and whether it may be deselected. Add a command that updates `FloatingPanelHiddenProviderIds`, saves settings, rebuilds rows, and republishes the last pulse immediately. Prevent deselecting the last visible account. Remove a provider ID from the hidden list when that provider is deleted. Normalize a hand-edited all-hidden current set back to all visible so the panel never opens empty.
   - Preserve: Account editing, primary selection, provider removal, Z.AI/Copilot handling, source reload behavior, and automatic saving.
   - Verify: Toggling a checkbox updates the running panel without restart; restart preserves the subset; adding a new account makes it visible; deleting a hidden account leaves no stale user-visible state; the last selected account cannot be cleared.

7. `src/costats.App/SettingsWindow.xaml`
   - Change: Under `Show the floating status panel`, add the label `Accounts shown in the floating panel` and a nested checkbox list sourced from `ProviderRows`. Show each account's display name and provider kind. Keep the choices available while the panel is disabled so users can configure before enabling it. Update the explanatory copy to state that the panel uses the same status rows as the tray tooltip.
   - Preserve: The existing settings hierarchy, account-management section, automatic saving, and English UI tone.
   - Verify: Six configured accounts produce six choices; the selected state is unambiguous; the last selected choice is disabled from deselection; long display names trim or wrap without colliding with the settings scrollbar.

8. `src/costats.App/Themes/ThemeLight.xaml` and `src/costats.App/Themes/ThemeDark.xaml`
   - Change: Remove `FloatingPanelBgBrush`, `FloatingPanelBorderBrush`, `FloatingPanelCloseHoverBrush`, `FloatingPanelClosePressedBrush`, `FloatingPanelClaudeBrush`, `FloatingPanelCodexBrush`, and `FloatingPanelZaiBrush` after all references move to the shared theme tokens.
   - Preserve: Every existing shared token and the locked `UsageBands` palette.
   - Verify: `rg "FloatingPanel.*Brush" src tests` returns no obsolete brush references and both themes render the panel with the tooltip surface colours.

9. `tests/costats.Core.Tests/Settings/JsonSettingsStoreTests.cs` and focused settings/filter tests at the existing pure seam
   - Change: Cover default-all behavior, JSON round-trip of hidden provider IDs, case-insensitive matching, duplicate/blank normalization, new-account visibility, last-selection normalization, and legacy `keepWidgetOpen` compatibility. Add a pure selection test proving a six-account input can produce a chosen subset without changing the full tooltip rows.
   - Preserve: Existing atomic settings and credential-redaction tests.
   - Verify: The new tests fail against the current behavior and pass after the implementation.

10. `README.md`
   - Change: Document that the floating panel reuses the tray-tooltip design, supports a persistent account subset, defaults to all accounts, and automatically includes newly added accounts. Add the new persisted hidden-provider-ID setting to the settings table.
   - Preserve: Existing Used/Remaining and activation documentation.
   - Verify: Documentation names match the final setting and UI copy exactly.

## Scope

- Inherit: Tray tooltip and floating panel inherit the shared row presenter and locked risk colours.
- Verify: Main widget, tray icon, remote viewer, primary ordering, settings account management, and both theme dictionaries.
- Exclude: Web UI changes, remote payload filtering, provider-source changes, floating-panel position persistence, account reordering, live session activation, merge, release, and production deployment.

## Validation

- Product: Configure six accounts, enable the panel, select all six, then select a two-account subset. Expect six and two distinct rows respectively while the hover tooltip continues to show all six.
- Interface: Inspect light and dark themes, Used and Remaining modes, one and six selected accounts, 24-character account names, unavailable quota data, a primary account, panel close/reopen, drag behavior, and restart persistence. The panel rows must be visually identical to the hover tooltip rows apart from the panel's close control.
- System: Confirm both tooltip consumers call the same shared presenter, no provider-name colour branch remains, new accounts default visible, and floating selection does not alter the main widget, tray icon, remote view, or provider refreshes.
- Repository: `"%LOCALAPPDATA%\dotnet-sdk\dotnet" build costats.sln -c Release` -> zero errors and zero warnings.
- Repository: `"%LOCALAPPDATA%\dotnet-sdk\dotnet" test costats.sln -c Release --no-build` -> all tests pass.
- Repository: Publish `src/costats.App/costats.App.csproj` for `win-x64`, deploy locally with all auto-start activation settings forced off for the verification profile, inspect the live floating panel, restore the original settings, and verify the original process state is restored.
- Repository: No `web/` or `remote/worker/` file changes are expected, so bundle regeneration is out of scope.

## Stop conditions

- Stop if extracting the shared presenter changes the existing tooltip appearance or wording; preserving that exemplar is a hard requirement.
- Stop if a configured row cannot be mapped to a stable pulse provider ID; do not persist display names as selection keys.
- Stop if the implementation would require filtering provider refreshes, remote payloads, or the main widget; the selection is floating-panel-only.
- Stop before any live five-hour-window activation, merge, release, or production action.

## Design documentation

- After acceptance and validation: update `README.md` with the floating-panel account-selection behavior and persisted setting. No additional design document exists or is required.
