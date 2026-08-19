# AI Usage Tray, Windows setup

This custom build shows one system-tray icon for:

- Claude desktop subscription usage through a separate local OAuth profile
- Two independently authenticated OpenAI accounts

Hovering over the icon shows all available accounts. OpenAI currently exposes a weekly Codex allowance for these accounts, not a five-hour window. The icon colour is based on the lowest remaining percentage across every quota window actually returned by a provider:

- Green: more than 50% remaining
- Amber: 20% to 50% remaining
- Red: less than 20% remaining
- Grey: no quota data available

## 1. Install the official Codex CLI

Open PowerShell and run the official OpenAI installer:

```powershell
powershell -ExecutionPolicy ByPass -c "irm https://chatgpt.com/codex/install.ps1 | iex"
```

Close and reopen PowerShell, then verify:

```powershell
codex --version
```

## 2. Sign in to OpenAI account 1

The two accounts must use separate `CODEX_HOME` folders. File credential storage is used because a shared Windows keyring entry may not isolate two simultaneous Codex accounts. These files remain inside your Windows user profile and must be treated like passwords.

```powershell
$env:CODEX_HOME = "$HOME\.codex-openai-1"
New-Item -ItemType Directory -Force $env:CODEX_HOME | Out-Null
Set-Content "$env:CODEX_HOME\config.toml" 'cli_auth_credentials_store = "file"'
codex login
codex login status
```

Complete the browser login using the first paid ChatGPT account. Do not paste tokens into this app or into chat.

## 3. Sign in to OpenAI account 2

```powershell
$env:CODEX_HOME = "$HOME\.codex-openai-2"
New-Item -ItemType Directory -Force $env:CODEX_HOME | Out-Null
Set-Content "$env:CODEX_HOME\config.toml" 'cli_auth_credentials_store = "file"'
codex login
codex login status
Remove-Item Env:CODEX_HOME
```

Complete the browser login using the second paid ChatGPT account.

## 4. Rename the OpenAI accounts

Right-click the tray icon and choose **Settings**. Under **OpenAI accounts**, enter the two labels you want, for example:

```text
Account 1 name: PA
Account 2 name: GPT
```

Names are saved automatically. Exit and restart AI Usage Tray to apply them to the selector and tray tooltip. Renaming does not change either account login or its `CODEX_HOME` folder.

## 5. Connect the Claude desktop subscription

Claude desktop and Claude Code keep separate local sessions, but the five-hour and weekly limits belong to the Claude subscription account. AI Usage Tray uses an isolated Claude login only as an authentication bridge. You do not need to use Claude Code for conversations.

Open a new PowerShell window. Install or update the official Claude Code authentication client:

```powershell
irm https://claude.ai/install.ps1 | iex
claude --version
```

Then create the isolated profile and start its login:

```powershell
Remove-Item Env:ANTHROPIC_API_KEY -ErrorAction SilentlyContinue
$env:CLAUDE_CONFIG_DIR = "$HOME\.claude-ai-usage-tray"
New-Item -ItemType Directory -Force $env:CLAUDE_CONFIG_DIR | Out-Null
claude
```

In Claude Code, use `/login` if it does not open the browser automatically. Sign in with the same Claude Pro account used by the desktop app, then run `/usage` to confirm that the current-session and weekly values match the desktop app. Exit Claude Code and clear the temporary environment variable:

```powershell
Remove-Item Env:CLAUDE_CONFIG_DIR -ErrorAction SilentlyContinue
```

Restart AI Usage Tray. It reads the account-wide subscription percentages from this isolated profile and does not read Claude desktop cookies or conversation data.

## 6. Run the tray app

Place all files from the ZIP in one folder, then run:

```powershell
.\AIUsageTray.exe
```

Windows may show a SmartScreen warning because this private build is not code-signed. Check the SHA-256 value supplied with the build before running it.

The app starts directly in the system tray. If the icon is hidden, open the tray overflow using the `^` button and drag the icon onto the taskbar. Hover for the compact three-account summary, click for the full panel, or right-click for refresh/settings/exit.

## Data and security

- OpenAI quota data comes from the official local `codex app-server` method `account/rateLimits/read`.
- The app starts one short-lived Codex process per configured OpenAI account and does not read account tokens itself.
- Claude percentages come from Anthropic's private OAuth usage endpoint using the isolated profile's local token. This is not a documented public API and may change.
- The Claude token remains in the isolated profile folder. The app does not read Claude desktop cookies or conversation content and does not transmit the token anywhere except Anthropic's API.
- No telemetry is added.
- Automatic updates from the upstream costats project are disabled because they would overwrite this custom multi-account build.

## Upstream attribution

This build is a fork of the MIT-licensed [`fmdz387/costats`](https://github.com/fmdz387/costats) project by fmdz. The repository keeps the upstream copyright notice in [LICENSE](../LICENSE); see the README for a summary of what was changed.
