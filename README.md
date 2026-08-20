# AI Usage Tray <img src="src/costats.App/Resources/tray-icon.ico" width="20" height="20" alt="icon" />

A lightweight Windows tray app that shows, behind **one icon**, the live quota of several AI coding subscriptions:

- **Any number of Claude subscriptions** (5-hour and weekly windows), each via its own local OAuth profile
- **Any number of OpenAI / ChatGPT Codex accounts**, each with its own `CODEX_HOME`
- **Z.AI / GLM** coding plan (optional, API key)
- **GitHub Copilot** (optional, experimental, personal access token)

> **This is a fork of [fmdz387/costats](https://github.com/fmdz387/costats)** (MIT).
> It keeps costats' architecture and UI but changes what is monitored and how.
> See [What's different from upstream](#whats-different-from-upstream) and [Credits & license](#credits--license).

<p align="center">
  <img src="assets/dark-mode.png" alt="widget, dark mode" width="330" />
  <img src="assets/light-mode.png" alt="widget, light mode" width="330" />
</p>

## Features
- Tray icon shows the remaining percentage as a number, coloured green/amber/red; hover for a tooltip listing every account.
- The widget (click the icon or `Ctrl+Alt+U`) opens on an overview of all accounts, sized to fit them; click a card for details.
- Model-scoped limits reported by Claude (e.g. the Fable weekly limit) shown per account.
- Plan chips (Max, Plus, Pro Lite...) for Claude and Codex accounts.
- Optional primary account (star in Settings): drives the tray icon and is pinned to the top of the overview.
- Light/dark theme, following the Windows theme by default.
- Accounts managed in Settings via an add/edit dialog per provider; changes apply without restarting.
- Optional reset countdowns on the overview cards; daily / 30-day cost estimates where available.
- Self-update from this repository's releases.

## Install / set up
**One-step PowerShell** (downloads the latest [release](https://github.com/ShlomiPorush/ai-usage-tray/releases/latest), installs per-user to `%LOCALAPPDATA%\AIUsageTray\app`, adds a Start Menu shortcut):
```powershell
iwr -useb https://raw.githubusercontent.com/ShlomiPorush/ai-usage-tray/main/scripts/install.ps1 | iex
```

**Manual:** download `ai-usage-tray-win-x64-vX.Y.Z.zip` (or `arm64`) from [Releases](https://github.com/ShlomiPorush/ai-usage-tray/releases), verify the `.sha256`, extract and run `AIUsageTray.exe`. Builds are self-contained (no .NET runtime needed) and not code-signed, so SmartScreen may warn.

**From source:** see **Build** below.

Then follow **[docs/WINDOWS-SETUP.md](docs/WINDOWS-SETUP.md)** for the step-by-step account setup:

1. Out of the box the app monitors the standard `~/.claude` (Claude Code login) and `~/.codex` (Codex CLI login) profiles — if you use both tools, it shows data immediately.
2. To monitor more accounts, open Settings → Accounts → "+ Claude account" / "+ Codex account", point each at its own profile folder (a folder-picker is available), and sign in with the official CLI inside that folder (`CLAUDE_CONFIG_DIR=<dir> claude` / `CODEX_HOME=<dir> codex login`). Changes apply immediately — no restart needed.
3. Optionally add a Z.AI key (`ZAiCodingApiKey` / `ZAiApiKey`) or a Copilot token in Settings.

## Configuration
Settings are stored at `%LOCALAPPDATA%\costats\settings.json` (path kept from upstream so existing installs keep working).

| Setting | Default | Meaning |
|---|---|---|
| `RefreshMinutes` | `5` | Background polling interval |
| `Hotkey` | `Ctrl+Alt+U` | Toggle the widget |
| `StartAtLogin` | `false` | Registers `AiUsageTray` in the Run key |
| `Accounts[]` | one Claude (`~/.claude`) + one Codex (`~/.codex`) | Any mix of accounts: `Id`, `Type` (`claude`/`codex`), `DisplayName`, `ConfigDir`. Editable in Settings (add/remove). Legacy `OpenAiAccounts`/`ClaudeConfigDir` settings are migrated automatically. |
| `ZAiCodingApiKey` / `ZAiApiKey` | empty | Z.AI coding-plan / pay-as-you-go keys |
| `ShowClockPanel` | `false` | Always-on-top status text next to the clock |
| `CopilotEnabled` | `false` | Enable the Copilot provider |
| `Theme` | `system` | `system` / `light` / `dark` |
| `PrimaryAccountId` | empty | Provider id whose status drives the tray icon |
| `ShowOverviewResetTimes` | `false` | Reset countdowns on overview cards |

`appsettings.json` (`Costats:Update`) controls the self-updater. It is **disabled** in this fork and points at `ShlomiPorush/ai-usage-tray`; enable it only once this repository publishes releases.

## Data sources & privacy
- **OpenAI / Codex**: the official local `codex app-server` JSON-RPC method `account/rateLimits/read`, one short-lived process per account. The app never reads or copies account tokens — Codex owns authentication and refresh.
- **Claude**: Anthropic's OAuth usage endpoint using the isolated profile's token. This is not a documented public API and may change.
- **Z.AI**: `api.z.ai` usage endpoints with a Bearer token.
- **Copilot**: unofficial GitHub endpoint; token stored in Windows Credential Manager.
- No telemetry. Requests go only to the provider APIs above. Local credential files and the profile folders are git-ignored.

## What's different from upstream
Compared with `fmdz387/costats` v1.4.6 (the fork point):

| Area | costats (upstream) | AI Usage Tray (this fork) |
|---|---|---|
| OpenAI / Codex | One account, reads `~/.codex/auth.json` + OAuth endpoint, log-based estimates | **Any number of accounts** via `codex app-server`, separate `CODEX_HOME`s, account selector, editable in Settings |
| Claude | Claude Code usage from local logs; multiple profiles only through the external `multicc` tool | **Any number of Claude subscriptions** through per-account OAuth profiles (`ClaudeSubscriptionSource`); stacked panel when more than one |
| Z.AI / GLM | — | New provider (`ZaiUsageSource`) |
| Tray icon | Static icon | Dynamic icon: colour by severity + remaining % number; `TrayStatusComposer`; optional clock-side panel |
| Tests | none | `tests/costats.Core.Tests` (xunit) covering the new sources, parsers and tray composer |
| Self-update | enabled, from `fmdz387/costats` | disabled; repository changed to this fork; updater/installer expect `AIUsageTray.exe` and `ai-usage-tray-win-*` assets |
| Branding | `costats.App.exe`, product "costats" | `AIUsageTray.exe`, product "AI Usage Tray", own version line (1.2.x) |

Upstream code paths that are no longer wired up (e.g. `CodexLogSource`, `CodexOAuthUsageFetcher`, `MulticcClaudeLogSource`) are kept in the tree to ease merging future upstream changes.

## Build
Requires a .NET SDK that supports `net10.0-windows`. Version is centralised in `src/Directory.Build.props` (`VersionPrefix`).

```powershell
dotnet build .\costats.sln -c Release
dotnet test  .\costats.sln -c Release
.\scripts\publish.ps1          # portable single-file x64 + arm64 ZIPs with .sha256
```

## Insights Card CLI
`tools/insights-cli` is inherited unchanged from upstream (`npx costats ccinsights`, renders a Claude Code Insights card). See [tools/insights-cli/README.md](tools/insights-cli/README.md).

## Credits & license
- Original project: **[costats](https://github.com/fmdz387/costats)** by **fmdz** — architecture, UI, updater, packaging and the insights CLI all originate there.
- Fork modifications (multi-account Codex, Claude subscription source, Z.AI, dynamic tray icon, tests): Shlomi Porush.
- Licensed under the **MIT License** — see [LICENSE](LICENSE), which retains the upstream copyright notice.
- macOS/Linux alternative: [CodexBar](https://github.com/steipete/CodexBar).
