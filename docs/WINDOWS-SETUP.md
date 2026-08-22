# AI Usage Tray, Windows setup

This guide covers app version 2.0.0.

AI Usage Tray shows one system-tray icon for any number of accounts: Claude subscriptions, OpenAI/ChatGPT Codex accounts, and optionally Z.AI / GLM and GitHub Copilot. Each Claude or Codex account points at its own local profile folder, so several accounts of the same provider can be monitored side by side.

On a fresh install the app monitors the standard `~/.claude` (Claude Code login) and `~/.codex` (Codex CLI login) profiles. If you already use both tools, it shows data with no setup at all. Everything else is done in Settings (right-click the tray icon and choose **Settings...**).

Hovering the tray icon shows a popup anchored to the icon that lists every account, one line each, with a coloured dot and the quota windows the provider returned. The icon itself shows the used percentage as a number. Every surface in the app, and the web view, uses the same four levels, based on the highest used percentage across the quota windows involved:

- Green: below 50% used
- Yellow: 50% to 74% used
- Orange: 75% to 89% used
- Red: 90% used and above
- Grey: no quota data available

Left-click the icon (or press `Ctrl+Alt+U`) to open the widget; right-click for **Refresh Now**, **Usage stats**, **Settings...** and **Exit**.

## 1. Install and run the app

Follow the install steps in the [README](../README.md): the one-line PowerShell installer, or download the ZIP from Releases, verify the `.sha256` and extract it. Builds are self-contained and are not code-signed, so Windows may show a SmartScreen warning.

Run `AIUsageTray.exe`. The app starts directly in the system tray. If the icon is hidden, open the tray overflow using the `^` button and drag the icon onto the taskbar.

## 2. Install the Codex CLI and sign in

Open PowerShell and run the official OpenAI installer:

```powershell
powershell -ExecutionPolicy ByPass -c "irm https://chatgpt.com/codex/install.ps1 | iex"
```

Close and reopen PowerShell, then sign in with your paid ChatGPT account:

```powershell
codex --version
codex login
codex login status
```

This writes the login into the default `~/.codex` folder, which the app already monitors. Complete the browser login; do not paste tokens into this app or into chat.

## 3. Install Claude Code and sign in

Claude desktop and Claude Code keep separate local sessions, but the 5-hour and weekly limits belong to the Claude subscription account. AI Usage Tray uses the Claude Code login only as an authentication bridge; you do not need to use Claude Code for conversations.

```powershell
irm https://claude.ai/install.ps1 | iex
claude --version
claude
```

In Claude Code, use `/login` if the browser does not open automatically, and sign in with the same Claude account used by the desktop app. Run `/usage` to confirm the values match. This writes the login into the default `~/.claude` folder, which the app already monitors.

If `ANTHROPIC_API_KEY` is set in your environment, clear it first so Claude Code performs a subscription login rather than an API-key login:

```powershell
Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue
```

## 4. Manage accounts in Settings

Right-click the tray icon and choose **Settings...**. The **ACCOUNTS** section is a table with one row per monitored provider, showing its kind, display name and profile folder (or "API key configured" / "Token in Windows Credential Manager" for Z.AI and Copilot). Each row has three buttons:

- **Star**: set this account as primary. The tray icon then shows this account's status, and the account is pinned to the top of the widget overview. Click the star again to clear it and go back to "worst window across all accounts".
- **Edit**: reopens the add/edit dialog prefilled for that row, so you can change the display name, the profile folder, or replace a stored key/token.
- **✕**: removes the account.

**+ Add account** opens the dialog. Pick the provider first (**Claude**, **OpenAI Codex**, **Z.AI / GLM** or **GitHub Copilot**); only the fields that provider needs are shown:

- Claude / Codex: a display name (up to 24 characters) and the profile folder, with a **...** button that opens a folder picker. The folder defaults to `~/.claude` or `~/.codex`.
- Z.AI / GLM: a display name and the coding-plan API key from `z.ai/manage-apikey`.
- GitHub Copilot: a classic personal access token with the `copilot` and `read:user` scopes.

All changes are saved to `settings.json` and applied immediately, with a refresh triggered right away. No restart is needed. A **Restart app** button is available at the bottom of Settings if you ever want one anyway.

## 5. Optional: additional isolated accounts

To monitor more than one account of the same provider, give each extra account its own profile folder and sign in inside it with the official CLI. Then add it in Settings as described above, pointing the profile folder at that directory.

### Extra Codex account

Separate `CODEX_HOME` folders keep the logins apart. File credential storage is used because a shared Windows keyring entry may not isolate two simultaneous Codex accounts. These files stay inside your Windows user profile and must be treated like passwords.

```powershell
$env:CODEX_HOME = "$HOME\.codex-work"
New-Item -ItemType Directory -Force $env:CODEX_HOME | Out-Null
Set-Content "$env:CODEX_HOME\config.toml" 'cli_auth_credentials_store = "file"'
codex login
codex login status
Remove-Item Env:CODEX_HOME
```

Complete the browser login with the second ChatGPT account, then add an **OpenAI Codex** account in Settings whose profile folder is `%USERPROFILE%\.codex-work`.

### Extra Claude account

```powershell
Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue
$env:CLAUDE_CONFIG_DIR = "$HOME\.claude-work"
New-Item -ItemType Directory -Force $env:CLAUDE_CONFIG_DIR | Out-Null
claude
```

Use `/login` inside Claude Code if the browser does not open, sign in with the second Claude account, then exit Claude Code and clear the variable:

```powershell
Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue
```

Add a **Claude** account in Settings whose profile folder is `%USERPROFILE%\.claude-work`.

A dedicated `CLAUDE_CONFIG_DIR` folder is only needed for these additional accounts. Your first Claude account can keep using the normal `~/.claude` login.

## 6. What the widget shows

Click the tray icon or press `Ctrl+Alt+U`. The widget opens on an overview of all accounts, sized to fit them, with the primary account first if one is set. Click a card for the details of a single account.

- Quota windows as reported by each provider: 5-hour and weekly for Claude and Codex.
- Model-scoped limits reported by Claude, for example the Fable weekly limit, listed per account.
- Plan chips: Claude plans including the Max multiplier (`Max 5x`, `Max 20x`), and Codex plans (`Plus`, `Pro Lite`, ...).
- Redeemable Codex usage-limit reset credits, when Codex reports any, with the time they expire. Redeem them in the Codex CLI with `/usage`.
- Daily and 30-day cost estimates where the provider supplies them.

The buttons in the widget header open the usage dashboard, the remote view link (when remote view is enabled), Settings and a manual refresh.

## 7. Usage stats

Right-click the tray icon and choose **Usage stats**, or press the chart button in the widget, to open the usage window. It reports how many tokens you used and what they would have cost at the published OpenAI and Anthropic API list prices, which is the honest way to compare a subscription with pay-as-you-go.

The numbers come from the Claude Code and Codex session logs already on the machine (`projects` under each Claude profile, `sessions` and `archived_sessions` under each Codex profile). Nothing is uploaded and no provider API is called. Results are cached incrementally, so the first open of a large history takes a moment and later opens are quick.

- Range: last 7, 30 or 90 days.
- Account filter: all accounts, or one account.
- A per-provider split, a chart of the range, and a breakdown table you can switch between **Model** and **Day**.
- Models the built-in price table does not cover are still counted in tokens and listed as unpriced. To price them yourself, create `%LOCALAPPDATA%\costats\pricing.json` with a flat map of model id to USD per million tokens, for example `{ "some-new-model": { "input": 0.2, "output": 1.2 } }`.

## 8. Other settings

- **Start at login**: registers the app in the `Run` key and writes a Startup-folder shortcut.
- **Show reset times in overview**: adds each window's reset countdown to the overview cards. Off by default to keep the overview compact.
- **Theme**: **Follow system** (tracks the Windows apps theme), **Light** or **Dark**.
- **Refresh interval**: how often usage is polled in the background, from 1 to 15 minutes; the default is 5.
- **Check for updates** under **UPDATES**: checks this repository's releases, stages the update and restarts to apply it. The app also checks automatically in the background.

Settings are stored at `%LOCALAPPDATA%\costats\settings.json` (the path is kept from upstream so existing installs keep working). Logs go to `%LOCALAPPDATA%\costats\logs`.

## Data and security

- OpenAI quota data comes from the official local `codex app-server` method `account/rateLimits/read`.
- The app starts one short-lived Codex process per configured OpenAI account and does not read account tokens itself.
- Claude percentages come from Anthropic's private OAuth usage endpoint using the local token in each account's profile folder. This is not a documented public API and may change.
- Claude tokens stay in their profile folders. The app does not read Claude desktop cookies or conversation content and does not transmit the token anywhere except Anthropic's API.
- The Z.AI key is stored in `settings.json`. The Copilot token is stored in Windows Credential Manager, not in `settings.json`.
- No telemetry is added.
- Automatic updates are enabled and point at this fork's repository, `ShlomiPorush/ai-usage-tray`. They will not pull builds from the upstream costats project.

## Upstream attribution

This build is a fork of the MIT-licensed [`fmdz387/costats`](https://github.com/fmdz387/costats) project by fmdz. The repository keeps the upstream copyright notice in [LICENSE](../LICENSE); see the README for a summary of what was changed.
