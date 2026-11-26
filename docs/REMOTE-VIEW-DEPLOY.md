# Deploying remote view server releases

This page is for operators who run the remote view server (`remote/server`) from the published image
and update it with a tool that does a zero-downtime rollout with `docker rollout`. The basic setup
is in [remote/server/README.md](../remote/server/README.md).

## Image tags

The Container workflow publishes `ghcr.io/shlomiporush/ai-usage-tray` with these tags:

| Tag | When it is pushed |
|---|---|
| `X.Y.Z` | A push to `main` that changes the image, after the server tests passed. The image's start and health check are tested by CI on the pull request, not again before the push. `X.Y.Z` is `remote/server/VERSION`. The workflow refuses to push a version that is already in the registry, so the tag never moves to another image. |
| `latest` | Every run on `main`, including a manual run. |
| `sha-<commit>` | Every run. |

A manual run of the workflow never pushes `X.Y.Z`. To publish a new version, raise
`remote/server/VERSION` in the change.

The check that refuses a published version needs `latest` to be readable without signing in. If
`latest` is missing, run the workflow by hand on `main` first, make sure the package is public,
then run the refused run again.

Runs from before this check, including their re-runs, and manual runs on a branch that still has
the older workflow, push `X.Y.Z` without it. Do not re-run them.

The server versions are not git tags. `vX.Y.Z` tags in this repository are desktop app releases.

## Rollout labels

Each image carries two labels. A rollout tool compares them between the running image and the new
one, and refuses the rollout when they do not allow it.

| Label | Value |
|---|---|
| `ai-usage-tray.compose-sha256` | The sha256 of the committed `remote/server/compose.yaml`. |
| `ai-usage-tray.rollout-from` | The oldest running release this image may run beside. |

`remote/server/rollout-labels.sh` computes both values. The Container workflow puts them on the
published image, and CI checks them on every pull request.

Any change to `remote/server/compose.yaml`, even a comment, changes `compose-sha256`, so the next
release is updated by hand.

### Why every release is updated by hand

`rollout-from` is the version being published, so no release rolls out beside an older one. A
rollout tool still accepts a move to an older release, because the running one is not older than
`rollout-from`. Go back to an older release with the update with downtime below.

The server itself can run as two containers on one data directory: SQLite runs in WAL mode, every
check and the write that depends on it share one transaction, schema changes only add, and the
only background work is an hourly expiry sweep that is safe to repeat. Write limits are counted
per container, so they allow up to twice the budget while both run. The viewer files are the
problem. The same container serves the viewer, and every recent server release changed it. While
two releases run side by side, a browser can load some viewer files from the old container and
some from the new one, and the Cloudflare purge, when it is configured, runs at startup while the
old container still serves.
A service worker that installs in that time can keep a mix of old and new files until the next
viewer release.

A release can roll out once the viewer refuses a mixed set of files and the purge runs after the
old container is gone. `rollout-from` then becomes the oldest release the new one can run beside.

## The host's Compose file and `.env`

A rollout runs two containers of the service at once, so the host's own `docker-compose.yml` differs
from the example `remote/server/compose.yaml`:

- `name: ai-usage-tray` at the top, the Compose project name.
- `image: ghcr.io/shlomiporush/ai-usage-tray:${VERSION}`, not `latest`.
- No `ports:`. The reverse proxy reaches the service over a shared Docker network, for example by
  Docker labels, instead of the example's `127.0.0.1:8080`. Keep `TRUST_PROXY=1` and the proxy rules
  in the README.
- No `container_name`, no `build`, no `network_mode: host`, one replica.
- No `pull_policy: always`, so the rollout does not pull again half way.

Keep the rest of the example: the data volume, the read-only root file system, the capabilities,
the health check and the environment.

The `.env` file next to it has exactly one `VERSION=X.Y.Z` line. Its other lines, such as the
`VAPID_*` or Cloudflare values, stay as they are. Do not set `VERSION` in the shell, because it
overrides `.env`.

`name: ai-usage-tray` makes Compose treat the service as a new project when the deployment used
another name before, such as its directory name. When you switch an existing deployment, run
`docker compose down` with the old file first. Then rename `compose.yaml` to `docker-compose.yml`
instead of keeping both: Compose prefers `compose.yaml` when both are in the directory.

The image has a health check on `/health`, which reads and writes the database. It runs every 30
seconds, so a new container needs about 30 seconds to become healthy.

## Update with downtime

Use this for a release that the rollout refuses. In the directory with `docker-compose.yml` and
`.env`:

```sh
NEW=X.Y.Z
docker pull "ghcr.io/shlomiporush/ai-usage-tray:$NEW" &&
  sed -i "s/^VERSION=.*/VERSION=$NEW/" .env &&
  docker compose stop remote-view &&
  docker compose up -d --wait --wait-timeout 300 remote-view
```

Replace `remote-view` if the service has another name. The relay is down for a few seconds. The
desktop apps upload again on their next refresh.

## A release that was pushed only in part

If a Container run pushed `X.Y.Z` but failed before every tag was in place, running it again stops
at the published version. Compare the digests of `X.Y.Z`, `latest` and `sha-<commit>` with
`docker buildx imagetools inspect`, and copy only a tag that is missing or still points to an older
image. Copy `latest` only when `X.Y.Z` is the newest version:

```sh
docker buildx imagetools create \
  -t ghcr.io/shlomiporush/ai-usage-tray:latest \
  ghcr.io/shlomiporush/ai-usage-tray:X.Y.Z
```

A run that failed before the push can be run again.
