# Remote view worker

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
| PUT     | `/u/{id}`                           | Stores the snapshot the app uploads. `204` on success.    |
| GET     | `/u/{id}`                           | Returns the stored JSON, or `404 {"error":"not_found"}`.  |
| GET     | `/u/demo`                           | A built-in sample payload, generated per request.         |
| OPTIONS | any                                 | CORS preflight.                                           |

`{id}` is a random 128-bit value written as 32 lowercase hex characters (`^[a-f0-9]{32}$`). It is the
only credential, for reading and writing alike, so treat the full link as a secret. Bodies must be
`application/json` and at most 16 KB.

`demo` is the one reserved id: `GET /u/demo` always answers with sample accounts whose timestamps are
computed at request time, so `/?id=demo` is a safe public link. It is never stored, `PUT /u/demo` is
rejected with `405`, and the page does not remember `demo` as the last id it saw.

Stored entries carry a 7-day TTL. Every upload refreshes it, so a snapshot from an app that was
stopped or uninstalled disappears on its own and the page then shows "not found".

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

### 5. Check it works

Open `https://ai-usage-tray-view.<your-subdomain>.workers.dev/` in a browser. You should see the
"AI usage" page saying no id was provided - that means the page, the code and the storage are all
wired up correctly.

Write that URL down; it is the only thing you need.

### 6. Point the app at it

In AI Usage Tray: **Settings -> Remote view**, and enter that same base URL (no trailing slash).
The app uploads there, and the share link it generates is that URL plus `?id=...`.

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
ID=0123456789abcdef0123456789abcdef

curl -i -X PUT "$BASE/u/$ID" \
  -H 'Content-Type: application/json' \
  -d '{"version":1,"generatedAt":"2026-08-20T18:00:00Z","primary":"claude:claude-1","accounts":[{"id":"claude:claude-1","provider":"claude","name":"Claude Work","plan":"Max 20x","windows":[{"label":"Session","usedPercent":42,"resetsAt":"2026-08-20T21:00:00Z"},{"label":"Weekly","usedPercent":88,"resetsAt":"2026-08-21T12:00:00Z"}]}]}'

curl -i "$BASE/u/$ID"
```

The PUT should answer `204 No Content` and the GET should echo the same JSON back. Then open
`$BASE/?id=$ID` in a browser to see it rendered.

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

That reads `worker.js` plus `web/index.html`, `web/styles.css` and `web/app.js`, embeds them and
rewrites `worker.bundle.js`. No dependencies, no build tools - plain Node.
