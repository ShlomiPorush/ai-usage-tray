# Remote view worker

Cloudflare Worker that stores one small JSON usage snapshot per random id and serves it back to the
static web page. Single file, no dependencies.

## Endpoints

| Method  | Path      | Behaviour                                                          |
| ------- | --------- | ------------------------------------------------------------------ |
| PUT     | `/u/{id}` | Stores the request body. `204` on success.                          |
| GET     | `/u/{id}` | Returns the stored JSON, or `404 {"error":"not_found"}`.            |
| OPTIONS | any       | CORS preflight.                                                     |

`{id}` is a random 128-bit value written as 32 lowercase hex characters (`^[a-f0-9]{32}$`). It is the
only credential, for both reading and writing, so treat the URL as a secret. Bodies must be
`application/json` and at most 16 KB.

Stored entries carry a 7-day TTL. Every upload refreshes it, so a snapshot from an app that was
stopped or uninstalled disappears on its own and the page then returns `404`.

## Deploy

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

3. Deploy:

   ```sh
   wrangler deploy
   ```

Wrangler prints the deployed URL, e.g. `https://ai-usage-tray-view.<subdomain>.workers.dev`.

Optionally attach a custom domain or route in the Cloudflare dashboard under
**Workers & Pages -> ai-usage-tray-view -> Settings -> Domains & Routes**.

## Verify

```sh
BASE=https://ai-usage-tray-view.<subdomain>.workers.dev
ID=0123456789abcdef0123456789abcdef

curl -i -X PUT "$BASE/u/$ID" \
  -H 'Content-Type: application/json' \
  -d '{"version":1,"generatedAt":"2026-08-20T18:00:00Z","primary":"claude:claude-1","accounts":[{"id":"claude:claude-1","provider":"claude","name":"Claude Work","plan":"Max 20x","windows":[{"label":"Session","usedPercent":42,"resetsAt":"2026-08-20T21:00:00Z"},{"label":"Weekly","usedPercent":88,"resetsAt":"2026-08-21T12:00:00Z"},{"label":"Fable","usedPercent":100,"resetsAt":"2026-08-21T12:00:00Z"}]}]}'

curl -i "$BASE/u/$ID"
```

The PUT should answer `204 No Content` and the GET should echo the same JSON back.

## Wiring it up

- Paste the base URL into the app's **Settings -> Remote view** section.
- Put the same base URL into `web/config.js` so the static page knows which endpoint to read.
