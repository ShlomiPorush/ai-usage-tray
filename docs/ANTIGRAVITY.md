# Google Antigravity

AI Usage Tray can show the quota of a Google Antigravity account. It reads the quota from the official Antigravity CLI, `agy`, and never touches your Antigravity sign-in.

## Set up

1. Install `agy` from [antigravity.google/cli](https://antigravity.google/cli) and sign in by running `agy` once.
2. In AI Usage Tray, open Settings > Accounts > **+ Add account** and pick **Google Antigravity**.
3. Choose how often the app may check, from 5 to 240 minutes (15 by default), and press **Add**.

The dialog says whether it found `agy`. The app looks for `%LOCALAPPDATA%\agy\bin\agy.exe` first, then for `agy.exe` in the folders on your `PATH`.

## What it does

- On the first refresh after at least the chosen number of minutes since the last check, the app starts `agy` in a hidden console, waits for the first status update that carries quota (usually 2 to 4 seconds, at most 15), and then closes `agy` and every process it started.
- Every other refresh shows the result of the last check. The widget says when it was updated, or when the next check is due.
- The schedule is shared by every AI Usage Tray window of your Windows account and survives a restart of the app. After a restart of the computer, the first check waits one full interval. The app recognises a restart by the Windows boot counter (`BootId` under `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters`, read only), so even a restart right after the previous check is caught. If that value cannot be read, the first check after every start of AI Usage Tray waits one full interval, because the app cannot tell whether the computer restarted; later checks in the same session compare the time since the computer started. A change of the computer clock also makes the next check wait one full interval.
- After three failed checks in a row, the app waits at least an hour (or the interval, if that is longer) before it tries again. The count of failed checks is kept in the shared schedule, so it survives a restart of the app.

## What it reads

- The quota buckets and the plan name from the status update that `agy` sends to its statusline command. No token is read.
- Gemini buckets (`Gemini weekly`, and `Gemini 5-hour` on plans that have one) are the account's main windows. The shared "Claude & GPT" buckets are shown as extra rows.
- The status update also contains the account email. The app does not keep it, show it or log it.

## What it never does

- It never reads, copies or stores Antigravity tokens. `agy` keeps its own sign-in.
- It never changes the `agy` settings file. The statusline command is passed only for that one run, on the command line.
- It never lets the check update `agy`: auto-update is turned off for the run (`AGY_CLI_DISABLE_AUTO_UPDATE=true`).
- It never sends the account email anywhere, including the remote view.

## Usage dashboard

While Antigravity is enabled, the Usage dashboard also counts the tokens Antigravity used, next to Claude Code and Codex, and prices them at the published Gemini API rates.

- **Where it reads**: the conversation databases of `agy` itself (`~\.gemini\antigravity-cli\conversations\*.db`) and of every profile T3 Code runs `agy` under (`~\.t3\userdata\providers\antigravity\<profile>\antigravity-acp\conversations\*.db`). All of them are shown as one account, **Antigravity (all sources)**, because a conversation file does not say which Google account ran it. The Antigravity desktop app keeps its conversations in another format and is not counted.
- **What it reads**: only the `gen_metadata` table, one record per model call. From each record it takes the model id, the time of the call, a hash of the request id (so a call is counted once), and the input, cached input, output and thinking token counts. The same records also hold prompt text and tool output; those parts are skipped without being decoded, and nothing from them is stored, cached or logged.
- **How it opens the files**: read-only, one at a time per file, without keeping them open. SQLite may create the small `-shm` file next to a database, as any reader does. A database that cannot be opened, or that has more than 200,000 model calls, is reported as unreadable instead of being counted in part.
- **The field layout is inferred.** Antigravity does not document this format. The mapping was worked out from real records, and it holds on every record checked (output always equals thinking plus response, and every request id is unique), but no second source confirms it. An update of `agy` can change the format; records that no longer fit are skipped and counted, never guessed at.
- **Prices**: a model id such as `gemini-3.8-flash-medium` carries the thinking level at the end. Google prices a model the same at every thinking level, so it is priced as `gemini-3.8-flash`. A model the price list does not cover is shown as unpriced.
- **History**: Antigravity usage is kept in the usage history like Claude and Codex usage, so it stays after `agy` deletes a conversation. Deleting the history and the retention setting apply to it the same way. An older version of AI Usage Tray does not know Antigravity: if you install one, it drops the Antigravity rows from every month of history it saves. When you return to a current version, it counts again what the conversation databases still hold; usage whose database was deleted in the meantime is lost.
- **Shared usage**: the shared usage report includes Antigravity only when the relay is version 1.9.0 or later. An older relay gets the report without it, and the app asks again every 6 hours.

## Remote view

When remote view is on, Antigravity is published like the other accounts: its name, plan, the Gemini windows and the "Claude & GPT" rows, with their usage and reset times. When you remove Antigravity or turn it off, the app uploads a snapshot without it right away. The viewer stops showing it after that upload succeeds. If uploads keep failing, the last stored snapshot stays on the relay until it expires after 7 days without updates, or until you turn remote view off, which deletes it.

## Files

Everything lives under `%LOCALAPPDATA%\AIUsageTray\antigravity-probe\`:

- `probe.lock` makes sure only one check runs at a time.
- `schedule.json` records when the last check ran, the time since the computer started and the Windows boot counter at that moment, how many checks failed in a row, and any back-off.
- `last-quota.json` keeps the quota and plan of the last successful check, never the email. When the app starts again before the next check is due, it shows this quota with the time of that check. A window whose reset has passed is left out, a window without a reset time is shown for at most a day, and nothing is shown from it after a failed check.
- Each check uses its own subfolder, which holds the status update and the `agy` log of that run. The folder is deleted when the check ends.

If the app crashes during a check, or the folder cannot be deleted, that subfolder can stay behind. It contains the account email. After AI Usage Tray starts again, the first Antigravity check removes it, which happens only while the provider is enabled. Turning the provider off does not remove a leftover folder. You can safely delete `%LOCALAPPDATA%\AIUsageTray\antigravity-probe` yourself while the tray app is closed.

## Limitations

- The app cannot tell whether `agy` is signed in. When `agy` is signed out, the widget shows "agy returned no quota. If agy is not signed in, run agy once." instead of a sign-in prompt.
- The headline colour of the account reflects the Gemini windows only. The "Claude & GPT" rows have their own colours.
- Only one Antigravity account is supported: the one `agy` is signed in to for your Windows account. A possible design for more accounts is kept in [ANTIGRAVITY-MULTI-ACCOUNT.md](ANTIGRAVITY-MULTI-ACCOUNT.md).
- Each check also refreshes a few of `agy`'s own state files, such as `cache\onboarding.json` and an empty crash log under `~\.gemini\antigravity-cli`. These files do not grow, and `agy` adds no logs or conversations, so the checks themselves never appear in the Usage dashboard.
- Each check starts `agy` for a few seconds, so it uses a little CPU and network every interval.
