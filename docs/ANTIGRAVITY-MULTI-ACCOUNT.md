# Antigravity: more than one account (deferred design)

Status: not implemented. The app supports one Antigravity account. This note keeps the findings and the proposed design in case a second account is needed later. It was tested against `agy` 1.4.10 on Windows on 2026-10-31.

## Why only one account works today

`agy` keeps the Google sign-in of a Windows account in Windows Credential Manager, under the single target `gemini:antigravity`. Every `agy` run of that Windows account uses the same entry.

A separate profile folder alone does not help. With `USERPROFILE` and `HOME` pointing to an empty folder, `agy` builds a new `.gemini` tree there but still signs in with the account stored in Credential Manager.

## What makes a second account possible

`agy` has a file-based token store. It uses that store instead of Credential Manager when it detects an SSH session. With a separate profile folder and the SSH variables set, `agy` logs `Using file-based token storage because SSH session detected` and starts signed out:

| Variable | Value used in the test |
|---|---|
| `USERPROFILE`, `HOME` | the isolated profile folder |
| `SSH_CONNECTION` | `127.0.0.1 50000 127.0.0.1 22` |
| `SSH_CLIENT` | `127.0.0.1 50000 22` |

After a second account signed in interactively with the same variables, the probe returned that account's quota (a different account identity and different Gemini usage). The main account and its Credential Manager entry were not affected.

In that mode, `agy` writes the sign-in to `<profile>\.gemini\antigravity-cli\antigravity-oauth-token`. The file is plain JSON and contains a refresh token. Windows does not encrypt it.

## Risks

- **Undocumented behaviour.** The SSH detection is not a documented switch. A future `agy` release can remove it. Then an extra profile would silently fall back to the main account.
- **Token at rest.** The extra account's refresh token is a plain file. This is weaker than Credential Manager, but similar to the Claude and Codex credential files in the user profile.
- **More activity.** Each extra account adds one short `agy` run per interval.

## Proposed design

- The main account stays exactly as it is today: no extra variables, and Credential Manager.
- Each extra account gets its own profile folder under `%LOCALAPPDATA%\AIUsageTray\antigravity-accounts\<id>\`. That folder inherits user-only permissions from the profile. Build the path from `AppDataPaths`.
- **Sign in** opens `agy` in a visible console with that account's variables. The user signs in there and closes it. The app never reads the token file.
- Probes reuse the existing runner, with the same lock, job object, deadline and schedule, and one schedule entry per account. Only one `agy` runs at a time.
- **Fallback guard.** Keep a salted hash of the account email in memory and settings, never the email itself. If an extra account returns the same hash as the main account, or as another extra account, show an error instead of duplicate data. This catches the silent fallback if `agy` drops the SSH behaviour.
- **Remove account** deletes that account's profile folder, after a confirmation that names the folder.
- Each account follows the same remote view rules as the main account: quota and plan only, never the email.

This touches tokens on disk, so it needs a full plan review and a result review before implementation.
