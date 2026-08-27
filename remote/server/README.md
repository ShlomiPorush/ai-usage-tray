# Remote view server

The remote view server runs as one Docker container. It serves the PWA and the protocol v2 JSON API
from the same origin, and stores snapshots in SQLite in the mapped `data` directory.

The server has no npm dependencies. It uses the SQLite module built into Node.js 24. GitHub Actions
publishes ready-to-run `linux/amd64` and `linux/arm64` images to:

```text
ghcr.io/shlomiporush/ai-usage-tray:1.0.4
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

To update later:

```sh
docker compose pull
docker compose up -d
```

Compose uses `pull_policy: always`, so `up` also checks the registry. The explicit `pull` keeps the
operation and any registry error visible before the running container is replaced.

The image and Compose service define the same health check. Compose reports the container as
`healthy` or `unhealthy` in `docker compose ps`.

The first successful Container workflow creates the GitHub package. Confirm once in the package
settings that its visibility is **Public**. Public GHCR images can be pulled anonymously. If it is
kept private, authenticate the server with `docker login ghcr.io` before `docker compose pull`.

The `./data` host directory is mapped to `/data`; no Docker volume is created. On startup, the
entrypoint prepares the mapped directory (mkdir, chown to UID and GID `10001`, mode fixes, each as
far as the granted capabilities allow) and verifies with a real write probe that the database files
can be created and modified. It then picks the most restricted user that works: with
`CAP_SETUID`/`CAP_SETGID` available it drops to the dedicated non-root `remoteview` account; under
`cap_drop: ALL` privilege dropping is impossible, so the process keeps running as the started user
with an empty capability set, a read-only root filesystem, `no-new-privileges`, and write access
only to `/data`. A Compose `user` override is also supported when the host `data` directory and any
existing database files are writable by that UID; otherwise the container exits with an error that
names the exact `chown` to run on the host.

Example Caddy configuration:

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
```

The JSON is stored unchanged. The server does not extract account data into columns.

## API compatibility

| Method | Path | Behaviour |
| --- | --- | --- |
| `GET` | `/`, viewer assets | Serves the installable viewer. |
| `GET` | `/health` | Checks that the process and SQLite connection are healthy. |
| `PUT` | `/u/{writeId}` | Validates and stores a snapshot. Returns `204` and `X-Read-Id`. |
| `DELETE` | `/u/{writeId}` | Deletes a snapshot. Always returns `204` for a valid ID. |
| `GET` | `/u/{readId}` | Returns unexpired JSON or `404 {"error":"not_found"}`. |
| `GET` | `/u/demo` | Returns the generated read-only demo snapshot. |
| `OPTIONS` | any path | Handles CORS preflight. |

Bodies must use `application/json` and must not exceed 16 KB. Payload validation, security headers,
ID derivation, the seven-day TTL, and error bodies match the Cloudflare Worker implementation.

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
files into `data`, then start Compose again; the image restores ownership for UID and GID `10001`.

Snapshots are disposable seven-day data, so backup is optional. The write credentials remain on the
desktop applications and are not stored in this database.

## Development

Run the server tests with Node.js 24:

```sh
npm test --prefix remote/server
```
