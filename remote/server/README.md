# Remote view server

The remote view server runs as one Docker container. It serves the PWA and the protocol v2 JSON API
from the same origin, and stores snapshots in SQLite in the mapped `data` directory.

The server has no npm dependencies. It uses the SQLite module built into Node.js 24. GitHub Actions
publishes ready-to-run `linux/amd64` and `linux/arm64` images to:

```text
ghcr.io/shlomiporush/ai-usage-tray:1.3.0
ghcr.io/shlomiporush/ai-usage-tray:latest
```

The container version is maintained in `VERSION`. Each published image receives that version tag
and OCI version label. `latest` points to the image most recently published from `main`.

The production server pulls this image. It does not clone the repository or build the container.

## Run it

Create a deployment directory and download only the Compose file:

```sh
mkdir -p /opt/ai-usage-tray-remote-view
cd /opt/ai-usage-tray-remote-view
curl -fsSLo compose.yaml \
  https://raw.githubusercontent.com/ShlomiPorush/ai-usage-tray/main/remote/server/compose.yaml
mkdir -p data
docker compose pull
docker compose up -d
curl http://127.0.0.1:8080/health
```

The Compose file binds only to `127.0.0.1:8080`. Put Caddy, nginx, Traefik, or another HTTPS reverse
proxy in front of it.

Browser notifications use a persistent VAPID key pair. By default, the server creates
`data/vapid.json` on first startup and reuses it across restarts and image updates. Keep this file
private and include it in backups if browser subscriptions must survive moving to another host.

Set `VAPID_KEY_PATH` to an absolute path to keep the key somewhere else, for example a separate
mount that holds only secrets:

```yaml
    environment:
      - VAPID_KEY_PATH=/secrets/vapid.json
    volumes:
      - ./data:/data
      - ./secrets:/secrets
```

The path must be absolute and its directory must be writable by the container (the root filesystem
is read-only, so it has to be a mapped volume). Leaving `VAPID_KEY_PATH` unset keeps the existing
`data/vapid.json` location, so nothing changes for a running deployment.

The key file is created with mode `0600` and owned by UID `10001`. A backup taken as any other
non-root user skips it with `tar: Cannot open: Permission denied` and still exits successfully, so
such a backup silently contains the snapshots but not the key. Take backups as root, or as UID
`10001`, and verify that the key file is present in the archive.

To rotate the key pair, stop the container, delete the key file (or replace the `VAPID_*`
environment values), and start it again; a new pair is generated on the next startup. Rotation
invalidates every stored browser subscription, so each viewer must turn notifications off and on
again to re-subscribe. Stale subscriptions are dropped automatically when the push service rejects
them.

To supply a managed key pair instead, generate one from the repository:

```sh
npm run generate-vapid-keys --prefix remote/server
```

Put the three printed values in the deployment shell or a Compose `.env` file before starting the
container. Environment values override `data/vapid.json`. Keep `VAPID_PRIVATE_KEY` secret and back
it up. `VAPID_SUBJECT` must be a `mailto:` or `https:` contact value. A partial or invalid
environment configuration stops startup instead of silently disabling browser alerts.

The server delivers each notification by calling the browser's push endpoint, so a subscription is
only accepted when its endpoint is an `https:` URL, without credentials, without an explicit port,
and on one of the browser push services: `fcm.googleapis.com`, `*.push.services.mozilla.com`,
`*.push.apple.com`, and `*.notify.windows.com` (`*.` matches subdomains at any depth, not the bare
domain). IP addresses are never accepted. Any other endpoint is answered with
`422 {"error":"invalid_subscription"}`. Set `PUSH_ENDPOINT_ALLOWED_HOSTS` to a comma-separated host
list to replace that list, for example when running another push service or when narrowing it. The
list replaces the built-in one, so include every service the viewer's browsers use.

To update later:

```sh
docker compose pull
docker compose up -d
```

Compose uses `pull_policy: always`, so `up` also checks the registry. The explicit `pull` keeps the
operation and any registry error visible before the running container is replaced.

The image and Compose service define the same health check. Compose reports the container as
`healthy` or `unhealthy` in `docker compose ps`.

`GET /health` both reads and writes SQLite, because a full or read-only data volume leaves reads
working while every upload fails. A failing read answers `503 {"status":"unhealthy"}` and a failing
write answers `503 {"status":"degraded"}`; a healthy server answers `200 {"status":"ok"}` as before.
Either `503` marks the container `unhealthy`, so the usual restart and alerting paths see it. The
write probe result is reused for a few seconds, so polling the public endpoint cannot turn into a
flood of database transactions.

## Write throttling

`PUT /u/{writeId}` is unauthenticated by design: any 32-hex id creates a row that lives for the
seven-day TTL. A measured flood of 20,000 invented ids produced about 395 MB of stored rows, so the
relay throttles writes per client address.

**What actually protects the relay is the per-address limit.** Requests may carry a signature, and a
valid one buys a much larger budget, but that is a tiering hint, not authentication. See the honest
limits below before relying on it.

### Request signing

Two optional headers on `PUT /u/{writeId}` and `DELETE /u/{writeId}`:

| Header | Value |
| --- | --- |
| `X-Costats-Timestamp` | Unix time in whole seconds. |
| `X-Costats-Signature` | Lowercase hex HMAC-SHA256 over the canonical string, using the signing key. |

The canonical string is five newline-separated fields:

```text
v1\n<timestamp>\n<METHOD>\n<path>\n<sha256hex(body)>
```

`METHOD` is uppercase, `path` is the request path without the query string, and the digest is taken
over the raw request body exactly as sent (a `DELETE` has an empty body, so its digest is the
SHA-256 of the empty string). A request counts as signed when the signature matches and the
timestamp is within 300 seconds of the relay clock. Anything else, including a missing header, a
malformed value, a stale timestamp, or a signature made with another key, is simply treated as
unsigned. Nothing is rejected for being unsigned.

Known-answer vector, asserted by both the relay tests and the desktop client tests:

```text
key       = ai-usage-tray-public-default-key-v1
timestamp = 1767225600
method    = PUT
path      = /u/0123456789abcdef0123456789abcdef
body      = {"version":2,"generatedAt":"2026-08-27T12:00:00Z","accounts":[]}
sha256hex(body)
          = f7d294d301e5c845b8ff9f6d4da1888a94e90bde065fbd3d4ab33b6c74eead9d
signature = 550e42d03d30c657c7a483a2a7b7c91e63e2f0aeb49e7a8bf1feb9366b915cb0
```

`SNAPSHOT_SIGNING_KEY` sets the key. When it is unset the relay uses the built-in default shown
above, which is also the default compiled into the desktop app.

**That default key is published in this repository, so it is public knowledge.** A signature made
with it proves only that the sender implemented this format. It stops naive scripted floods and lets
a real client be told apart from one, and that is all it is for. It is worth setting a private
`SNAPSHOT_SIGNING_KEY` only on a relay whose desktop clients are all configured with the same value
(`Costats:RemoteView:SigningKey` in the app's `appsettings.json`); on that relay every other client
falls to the strict budget. On the shared public relay the key separates nothing.

### Per-address limits

Writes are counted per client address in a fixed one-minute window, with one counter per address
whatever the tier. The limit applied is the one for the current request, so the most any single
address can push through in a minute is the larger of the two limits, not their sum. Only accepted
writes are counted, so a client stuck on the strict budget cannot lock out a correctly signed write
from the same address. Over the limit the relay answers:

```text
429 Retry-After: <seconds>
{"error":"rate_limited","retryAfterSeconds":<seconds>}
```

The desktop app uploads at most once a minute, so the strict default of ten is far above any real
single user and existing unsigned clients keep working unchanged. The counters live in memory only:
they are per process, they reset on restart, and the map is pruned every request and capped at
20,000 live entries.

### Client address and reverse proxies

By default the counter keys on the socket peer address, the one value a client cannot choose. The
container is published on loopback and normally runs behind a reverse proxy, which would make every
request share the proxy's address, so the shipped Compose file sets `TRUST_PROXY=1`; the relay then
keys on the **last** `X-Forwarded-For` entry, the one appended by the proxy directly in front of the
container. Earlier entries are whatever the caller sent and are ignored, so a forged header cannot
mint a fresh address per request. This is correct for exactly one trusted proxy, whether it appends
to or replaces the incoming header (Caddy, nginx and Traefik append by default; no proxy
configuration change is needed). Set `TRUST_PROXY=0` when nothing is in front of the container, and
keep it `0` if there are ever two chained proxies, since then the last entry is the inner proxy's
address, not the client's.

Keeping an edge rate limit for `/u/` at Cloudflare or in the proxy is still worthwhile. The relay
limit is a floor that survives a misconfigured edge, not a replacement for one.

### Environment variables

| Variable | Default | Purpose |
| --- | --- | --- |
| `PORT` | `8080` | Listening port inside the container. |
| `DATABASE_PATH` | `/data/usage.db` | SQLite file. Its directory must be writable. |
| `SNAPSHOT_TTL_SECONDS` | `604800` | Snapshot lifetime. |
| `CLEANUP_INTERVAL_SECONDS` | `3600` | Expiry sweep, log truncation and guarded `VACUUM`. |
| `RELAY_REQUIRE_NONROOT` | unset | `1` refuses to start when the server would run as container root. |
| `VAPID_KEY_PATH` | `<data dir>/vapid.json` | Absolute path of the generated push signing key. |
| `VAPID_PUBLIC_KEY`, `VAPID_PRIVATE_KEY`, `VAPID_SUBJECT` | unset | Managed key pair; overrides the key file. |
| `PUSH_ENDPOINT_ALLOWED_HOSTS` | built-in list | Replaces the accepted push-service host list. |
| `SNAPSHOT_SIGNING_KEY` | public default key | HMAC key for write signatures. Public unless every client is reconfigured. |
| `UNSIGNED_PUT_PER_MINUTE` | `10` | Writes per minute per address without a valid signature. |
| `SIGNED_PUT_PER_MINUTE` | `120` | Writes per minute per address with a valid signature. |
| `TRUST_PROXY` | `0` (`1` in the shipped Compose file) | `1` keys the limit on the last `X-Forwarded-For` entry (the one the proxy appended). |

The first successful Container workflow creates the GitHub package. Confirm once in the package
settings that its visibility is **Public**. Public GHCR images can be pulled anonymously. If it is
kept private, authenticate the server with `docker login ghcr.io` before `docker compose pull`.

### Data directory, user and capabilities

The `./data` host directory is mapped to `/data`; no Docker volume is created. On startup, the
entrypoint prepares the mapped directory (mkdir, chown to UID and GID `10001`, mode fixes, each as
far as the granted capabilities allow), verifies with a real write probe that the database files can
be created and modified, and then switches to the dedicated non-root `remoteview` account.

Switching users needs capabilities that `cap_drop: ALL` removes, so the Compose file grants exactly
four back:

```yaml
    cap_drop:
      - ALL
    cap_add:
      - CHOWN
      - DAC_OVERRIDE
      - SETGID
      - SETUID
```

`CHOWN` and `DAC_OVERRIDE` let the entrypoint prepare `/data` whoever owns it today, and
`SETGID`/`SETUID` let it become UID `10001`. Only the entrypoint uses them: the server process it
executes ends up with an empty effective and permitted capability set, a read-only root filesystem,
`no-new-privileges`, and write access only to `/data` and `/tmp`. Verify it on a running container:

```sh
docker compose exec -u 0 remote-view awk '/^Uid:|^CapEff:/' /proc/1/status
```

The expected result is `Uid: 10001 10001 10001 10001` and `CapEff: 0000000000000000`.

Without those capabilities the entrypoint cannot drop privileges. It then prints a multi-line
warning and, unless `RELAY_REQUIRE_NONROOT=1` is set, keeps the server running as container root so
an older deployment does not break on an image update. The shipped Compose file sets
`RELAY_REQUIRE_NONROOT=1`, which turns that fallback into a startup failure instead.

A Compose `user` override is also supported when the host `data` directory and any existing database
files are writable by that UID; otherwise the container exits with an error that names the exact
`chown` to run on the host.

Migration for an existing deployment: replace the local `compose.yaml` with the current one and run
`docker compose up -d`. No host change is needed, whoever owns `data` today, because the entrypoint
now has `CHOWN`. Only a deployment that pins `user:` in its own Compose file has to keep the data
directory writable by that UID (`chown -R <uid>:<gid> data`).

Example Caddy configuration. Caddy's default forwarding is fine: the relay reads the last
`X-Forwarded-For` entry, which is the one Caddy appends.

```caddyfile
ai.yaaps.net {
    reverse_proxy 127.0.0.1:8080
}
```

Keep request body limits at 16 KB or slightly above. If Cloudflare remains the proxied DNS provider,
keep rate limiting for `/u/` enabled there and restrict direct access to the origin where practical.
Cloudflare proxy traffic does not use Workers or KV.

If the reverse proxy also runs in Docker, attach `remote-view` to the proxy's shared network and
route to `remote-view:8080` instead of publishing the loopback port.

The server enables SQLite WAL mode. The schema is intentionally small:

```sql
CREATE TABLE snapshots (
    read_id TEXT PRIMARY KEY,
    payload TEXT NOT NULL,
    expires_at INTEGER NOT NULL
) STRICT;

CREATE TABLE push_subscriptions (
    endpoint TEXT PRIMARY KEY,
    read_id TEXT NOT NULL,
    subscription TEXT NOT NULL
) STRICT;

CREATE TABLE health_probe (
    id INTEGER PRIMARY KEY,
    checked_at INTEGER NOT NULL
) STRICT;
```

The JSON is stored unchanged. The server does not extract account data into columns. `health_probe`
holds a single row that `/health` rewrites to prove the database is still writable.

Deleted rows leave free pages behind, so the database keeps its high-water mark. The periodic
cleanup truncates the write-ahead log after each expiry sweep, and rewrites the file with `VACUUM`
only when more than half of its pages are free and it is larger than 32 MB. `journal_size_limit` is
8 MB, so a large sweep no longer leaves a permanently oversized `usage.db-wal`.

## API compatibility

| Method | Path | Behaviour |
| --- | --- | --- |
| `GET` | `/`, viewer assets | Serves the installable viewer. |
| `GET` | `/health` | Reads and writes SQLite. `200 {"status":"ok"}`, or `503` when degraded. |
| `GET` | `/version` | Returns the deployed remote-view version shown in the viewer. |
| `GET` | `/push/vapid-public-key` | Returns the public VAPID key used for browser subscriptions. |
| `PUT` | `/u/{writeId}` | Validates and stores a snapshot. Returns `204` and `X-Read-Id`, or `429` over the write limit. |
| `DELETE` | `/u/{writeId}` | Deletes a snapshot. Returns `204` for a valid ID, or `429` over the write limit. |
| `GET` | `/u/{readId}` | Returns unexpired JSON or `404 {"error":"not_found"}`. |
| `POST` | `/u/{readId}/push-subscription` | Registers this browser for the shared view. |
| `DELETE` | `/u/{readId}/push-subscription` | Removes this browser subscription. |
| `GET` | `/u/demo` | Returns the generated read-only demo snapshot. |
| `OPTIONS` | any path | Handles CORS preflight. |

Bodies must use `application/json` and must not exceed 16 KB. Payload validation, security headers,
ID derivation, the seven-day TTL, and error bodies match the Cloudflare Worker implementation.

Write signing and the per-address write limit exist only here. The Worker ignores the two signing
headers, which is compatible in both directions because neither implementation ever requires them:
a client that signs is accepted by the Worker as an ordinary unsigned request.

The write ID remains a lowercase 32-character hex secret. The public read ID is:

```text
lowercase_hex(SHA-256(UTF8(writeId)))[0..32)
```

The authoritative test vector is:

```text
writeId = 0123456789abcdef0123456789abcdef
readId  = 3eb1bd439947eb762998e566ccc2e099
```

## Move `ai.yaaps.net` from the Worker

1. Wait for the Container workflow to publish the image from `main`.
2. Start the container and verify `GET /health`, `GET /u/demo`, and the protocol test below through
   the reverse proxy's temporary address.
3. Remove the Worker custom-domain or route assignment for `ai.yaaps.net`.
4. Create or update the proxied DNS record for `ai.yaaps.net` so it reaches the HTTPS reverse proxy.
5. Verify `https://ai.yaaps.net/?id=demo` and an upload from the desktop app.
6. Keep the Worker available for rollback until the new origin has remained healthy.

There is no KV migration. Existing write IDs stay on each desktop and existing share links retain
their read IDs. After the DNS cutover, each running app repopulates SQLite on its next refresh. A
viewer can briefly show `No data` until that upload happens.

Protocol smoke test:

```sh
BASE=https://ai.yaaps.net
WRITE_ID=0123456789abcdef0123456789abcdef
READ_ID=3eb1bd439947eb762998e566ccc2e099

curl -i -X PUT "$BASE/u/$WRITE_ID" \
  -H 'Content-Type: application/json' \
  -d '{"version":2,"generatedAt":"2026-08-27T12:00:00Z","accounts":[]}'
curl -i "$BASE/u/$READ_ID"
curl -i -X DELETE "$BASE/u/$WRITE_ID"
```

## Backup and restore

SQLite uses `data/usage.db`, `data/usage.db-wal`, and `data/usage.db-shm` while the service is running.
For a simple consistent backup, stop the container before copying the `data` directory. Restore the
files into `data`, then start Compose again; with the capabilities above the entrypoint chowns the
restored files to UID and GID `10001` itself, so the restore works whatever the archive contained.

Back up as root or as UID `10001`. `data/vapid.json` is mode `0600` and owned by UID `10001`, so a
backup taken as another non-root user silently omits it while reporting success.

Snapshots are disposable seven-day data, so backup is optional. The write credentials remain on the
desktop applications and are not stored in this database.

## Development

Run the server tests with Node.js 24:

```sh
npm test --prefix remote/server
```
