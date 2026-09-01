# Remote view worker

> Legacy rollback option. The preferred replacement is the SQLite-backed Docker server documented
> in [`../server/README.md`](../server/README.md). Keep this Worker until the origin cutover is
> verified.

Host your own remote view: one Cloudflare Worker that serves **both** the viewer page and the tiny
JSON API behind it, from a single URL.

**You probably do not need this.** The app ships with a default remote-view endpoint already baked
in, so "Remote view" works out of the box. Self-hosting is for people who would rather keep their
snapshots on infrastructure they control. Cloudflare's free plan is enough.

Everything you paste lives in one file: **`worker.bundle.js`**.

## What it does

| Method  | Path                                | Behaviour                                                |
| ------- | ----------------------------------- | -------------------------------------------------------- |
| GET     | `/`, `/index.html`                  | The viewer page.                                          |
| GET     | `/styles.css`, `/app.js`, `/config.js` | Page assets (cached 5 minutes).                        |
| GET     | `/version`                           | Returns the remote-view version shown in the viewer.      |
| PUT     | `/u/{writeId}`                      | Stores the snapshot the app uploads. `204` on success.    |
| DELETE  | `/u/{writeId}`                      | Removes the snapshot. Always `204`, present or not.       |
| GET     | `/u/{readId}`                       | Returns the stored JSON, or `404 {"error":"not_found"}`.  |
| GET     | `/push/vapid-public-key`            | Returns the Web Push public key when configured.             |
| POST    | `/u/{readId}/push-subscription`     | Registers browser notifications for that shared view.        |
| DELETE  | `/u/{readId}/push-subscription`     | Removes one browser notification subscription.               |
| GET     | `/u/demo`                           | A built-in sample payload, generated per request.         |
| OPTIONS | any                                 | CORS preflight.                                           |

Bodies must be `application/json` and at most 16 KB.

`demo` is the one reserved id: `GET /u/demo` always answers with sample accounts whose timestamps are
computed at request time, so `/?id=demo` is a safe public link. It is never stored, `PUT` and `DELETE`
on `/u/demo` are rejected with `405`, and the page does not remember `demo` as the last id it saw.

Stored entries carry a 7-day TTL. Every upload refreshes it, so a snapshot from an app that was
stopped or uninstalled disappears on its own and the page then shows "not found".

---

## Two ids: write id and read id (protocol v2)

Earlier versions used one id for reading and writing alike, so anyone holding a share link could
overwrite or wipe the snapshot. Protocol v2 splits it in two:

- **write id** - a random 128-bit value written as 32 lowercase hex characters (`^[a-f0-9]{32}$`).
  The app generates it, stores it locally and never puts it in a link. It authorises `PUT` and
  `DELETE`.
- **read id** - derived from the write id, also 32 lowercase hex characters. It is what the share
  link carries, and it can only read.

### Derivation

```
readId = lowercase_hex( SHA-256( UTF8( writeId ) ) )[0 .. 32)
```

Precisely:

1. Take the write id as a **string** of 32 lowercase hex characters, e.g.
   `0123456789abcdef0123456789abcdef`.
2. Encode that string as UTF-8, giving **32 bytes of ASCII**. Do **not** hex-decode it into 16 bytes
   first: the digest is over the characters, not over the value they encode.
3. SHA-256 those 32 bytes, giving a 32-byte digest.
4. Take the **first 16 bytes** of the digest and write them as lowercase hex: 32 characters. That is
   the read id.

Worked example - any client implementation must reproduce this exactly:

```
writeId = 0123456789abcdef0123456789abcdef
sha256  = 3eb1bd439947eb762998e566ccc2e099c791118b2f40579cc4f7da2b5061b7f9
readId  = 3eb1bd439947eb762998e566ccc2e099
```

You can also check against the worker itself: `PUT /u/{writeId}` answers
`204` with an `X-Read-Id` response header carrying the read id it derived. A client that computes a
different value has a bug.

The KV key is always the read id. Because the derivation is one-way, a read id in a share link
cannot be turned back into the write id. `GET /u/{writeId}` simply misses and returns `404`. The
read id is still a capability though: anyone with the link sees the usage numbers, so keep links
unlisted.

`DELETE /u/{writeId}` answers `204` whether or not anything was stored, so a probe cannot learn
whether a given id is in use.

### Payload validation

`PUT` bodies are checked beyond "is it JSON", and a body that fails answers
`422 {"error":"invalid_payload","reason":"..."}`. The rules are deliberately loose so a newer app
keeps working against an older worker; unknown fields are ignored.

| Rule | `reason` |
| --- | --- |
| Root must be a JSON object | `not_an_object` |
| `version` must be a finite number | `bad_version` |
| `accounts` must be an array | `bad_accounts` |
| At most 32 accounts | `too_many_accounts` |
| Each account must be an object | `bad_account` |
| `windows`, when present, must be an array of at most 32 | `bad_windows`, `too_many_windows` |
| No string (or key) anywhere longer than 256 characters, nesting at most 8 deep | `field_too_long` |

### Breaking change and deployment order

This is a wire-protocol break. **Redeploy the worker together with the app release that speaks v2.**

- A v2 app uploading to a v1 worker writes under the write id, and the v2 share link (a read id)
  finds nothing: the page shows "No data".
- A v1 app uploading to a v2 worker still works for that app, but the link it prints uses the write
  id and will 404 for readers.
- Existing v1 share links stop resolving as soon as their app uploads with v2. Nothing has to be
  migrated: old KV entries fall off on their own within the 7-day TTL.

---

## Setup (Cloudflare dashboard, no tools required)

About ten minutes, all in the browser. You need the file `remote/worker/worker.bundle.js` from this
repository open in a text editor (Notepad is fine) so you can copy its contents.

### 1. Create the worker

1. Go to <https://dash.cloudflare.com> and sign in, or create a free account (email + password, then
   confirm the email).
2. In the left sidebar, open **Compute (Workers)**.
3. Click **Create**, then choose **Start with Hello World** (or "Hello World" worker).
4. Name it `ai-usage-tray-view` and click **Deploy**.

You now have a working, empty worker at `https://ai-usage-tray-view.<your-subdomain>.workers.dev`.

### 2. Paste in the code

1. On the worker's page click **Edit code** (top right) to open the online editor.
2. Select everything in `worker.js` in that editor and delete it.
3. Copy the **entire** contents of `worker.bundle.js` from this repository and paste it in.
4. Click **Deploy** (top right) and confirm.

Opening the URL now shows the viewer page with an error about missing storage - that is expected
until the next step.

### 3. Create the storage namespace

1. Back in the left sidebar, open **Storage & Databases** -> **KV**.
2. Click **Create instance** / **Create namespace**.
3. Give it any name, for example `ai-usage-tray`, and click **Add**.

### 4. Connect the storage to the worker

1. Go back to **Compute (Workers)** and open `ai-usage-tray-view`.
2. Open **Settings** -> **Bindings**.
3. Click **Add** -> **KV Namespace**.
4. Variable name: **`USAGE`** - exactly that, uppercase, no spaces. The code looks for this name.
5. KV namespace: pick the one you just created.
6. Click **Deploy** / **Save**. If Cloudflare offers to re-deploy the worker, accept.

### 5. Configure browser notifications

Generate a persistent VAPID key pair from the repository:

```sh
npm run generate-vapid-keys --prefix remote/server
```

In the Worker settings, add `VAPID_PUBLIC_KEY` and `VAPID_SUBJECT` as text variables. Add
`VAPID_PRIVATE_KEY` as an encrypted secret. Keep the private value secret and backed up. If these
bindings are absent, the remote viewer still works but Web Push stays unavailable.

### 6. Check it works

Open `https://ai-usage-tray-view.<your-subdomain>.workers.dev/` in a browser. You should see the
"AI usage" page saying no id was provided - that means the page, the code and the storage are all
wired up correctly.

Write that URL down; it is the only thing you need.

### 7. Point the app at it

In AI Usage Tray: **Settings -> Remote view**, and enter that same base URL (no trailing slash).
The app uploads there with its private write id, and the share link it generates is that URL plus
`?id=<read id>`.

### 8. Add rate limiting (recommended)

The worker itself cannot rate limit: that lives in the Cloudflare dashboard, not in this repository.
Without it, anyone who learns a write id can hammer `PUT` on your account's request budget, and
anyone at all can spray guesses at `/u/`.

In the dashboard: **Security** -> **WAF** -> **Rate limiting rules** -> **Create rule**.

- Expression: `http.request.method eq "PUT" and starts_with(http.request.uri.path, "/u/")`
- Characteristics: **IP**
- Rate: something like **60 requests per minute** (the app uploads a few times an hour, so this is
  generous)
- Action: **Block**, for 1 minute

A second rule with the same expression but `http.request.method in {"GET"}` and a higher threshold
limits read-id guessing too. Free plans allow one rate limiting rule, so start with the `PUT` one.

---

## Advanced

### Custom domain

You can attach your own domain at any time - **Compute (Workers)** -> `ai-usage-tray-view` ->
**Settings** -> **Domains & Routes** -> **Add**. Nothing else changes: the page and the API move
together because they are the same worker, and the app just needs the new base URL.

### Deploying with the wrangler CLI instead

Prerequisites: Node.js 18+ and the Cloudflare CLI.

```sh
npm i -g wrangler
wrangler login
```

1. Create the KV namespace:

   ```sh
   wrangler kv namespace create USAGE
   ```

2. Copy the printed `id` into the `[[kv_namespaces]]` block in `wrangler.toml`, replacing the
   placeholder.

3. `wrangler.toml` already points at the bundle (`main = "worker.bundle.js"`); change it to
   `worker.js` only if you want the API-only variant.

4. Deploy:

   ```sh
   wrangler deploy
   ```

Wrangler prints the deployed URL.

### Verifying from the command line

```sh
BASE=https://ai-usage-tray-view.<subdomain>.workers.dev
WRITE_ID=0123456789abcdef0123456789abcdef
READ_ID=$(printf %s "$WRITE_ID" | sha256sum | cut -c1-32)

curl -i -X PUT "$BASE/u/$WRITE_ID" \
  -H 'Content-Type: application/json' \
  -d '{"version":2,"generatedAt":"2026-08-20T18:00:00Z","primary":"claude:claude-1","displayMode":"remaining","accounts":[{"id":"claude:claude-1","provider":"claude","name":"Claude Work","plan":"Max 20x","windows":[{"label":"Session","usedPercent":42,"resetsAt":"2026-08-20T21:00:00Z"},{"label":"Weekly","usedPercent":88,"resetsAt":"2026-08-21T12:00:00Z"}]}]}'

curl -i "$BASE/u/$READ_ID"
curl -i -X DELETE "$BASE/u/$WRITE_ID"
```

Note `printf %s` rather than `echo`: a trailing newline would change the digest.

The PUT should answer `204 No Content` with an `X-Read-Id` header equal to `$READ_ID`, and the GET
should echo the same JSON back. `GET $BASE/u/$WRITE_ID` should answer `404`. Then open
`$BASE/?id=$READ_ID` in a browser to see it rendered.

### Response headers

Every response carries `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`. The
viewer page adds, on top of those:

| Header | Value |
| --- | --- |
| `Content-Security-Policy` | `default-src 'none'` plus `'self'` for scripts, styles, images, `fetch`, the manifest and the service worker; `frame-ancestors 'none'`; `base-uri 'none'`; `form-action 'none'` |
| `X-Frame-Options` | `DENY` |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=()` |

`index.html` contains one inline `<script>` (the pre-paint theme guard). `bundle.mjs` hashes it at
build time and emits `'sha256-...'` in `script-src`, so the policy never needs `'unsafe-inline'`.
Editing that inline script without re-running `node bundle.mjs` will make the browser block it.

### API only, page hosted elsewhere

`worker.js` is the same API without the embedded page - use it if you already host `web/` on your own
server or on Cloudflare Pages. In that case set `apiBase` in `web/config.js` to the worker URL. The
bundled worker serves its own `config.js` with an empty `apiBase`, which means "same origin".

### Regenerating the bundle

`worker.bundle.js` is generated. After changing anything in `web/` or in `worker.js`:

```sh
cd remote/worker
node bundle.mjs
```

That reads `worker.js` plus every file in `web/` (page, styles, script, manifest, service worker and
the two icons), embeds them, recomputes the inline-script hash for the page's `Content-Security-Policy`
and rewrites `worker.bundle.js`. No dependencies, no build tools - plain Node, and the output is
byte-for-byte reproducible.

`web/sw.js` caches the shell responses **including their headers**, so bump `CACHE` in `web/sw.js`
whenever the shell or the headers it is served with change.
