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
| `ai-usage-tray.compose-sha256` | The sha256 of the committed `remote/server/docker-compose.yml`. |
| `ai-usage-tray.rollout-from` | `remote/server/ROLLOUT_FROM`: the oldest running release this image may run beside. |

`remote/server/rollout-labels.sh` computes both values. The Container workflow puts them on the
published image, and CI checks them on every pull request.

Any change to `remote/server/docker-compose.yml`, even a comment, changes `compose-sha256`, so the
next release is updated by hand.

### 1.11.1: a new Compose file

1.11.1 changes nothing in the server. It ships the new `remote/server/docker-compose.yml`, which
replaces the example `remote/server/compose.yaml` and is now the host's file as it is. Its
`compose-sha256` differs from 1.11.0, so 1.11.1 is updated by hand: switch the host to the new file
as described in [The host's Compose file and `.env`](#the-hosts-compose-file-and-env). Later
releases roll out beside 1.11.1 until the Compose file changes again.

### When `rollout-from` moves

1.11.0 is the first release that can run beside another one, so `ROLLOUT_FROM` is 1.11.0. Update to
1.11.0 by hand, with the update with downtime below. Later releases with the same Compose file roll
out beside it.

`ROLLOUT_FROM` stays where it is unless a release cannot run beside the releases before it. Then it
moves to that release's `VERSION`, and that release is updated by hand:

- **Any database schema change.** `remote/server/rollout-schema/<version>.json` holds the schema of
  the release named in `ROLLOUT_FROM`, and a test fails when the code creates any other schema. A
  pull request that changes the schema adds `rollout-schema/<VERSION>.json` and sets `ROLLOUT_FROM`
  to `VERSION`. `remote/server/rollout-check.sh` refuses a change to an existing snapshot, a new
  snapshot under another name, and a `ROLLOUT_FROM` that is neither the previous one nor `VERSION`.
  CI runs it on every pull request, and the Container workflow runs it before it publishes a push to
  `main`. Every release in one `ROLLOUT_FROM` range therefore uses the same schema. The push check
  compares only with the previous commit, so two direct pushes to `main` can still slip an edited
  snapshot past it; require pull requests and the "Remote View Server" check on `main`.
- **A changed meaning of stored data**, or a request that the previous release must understand.
  Nothing checks these two, so the change itself must raise `ROLLOUT_FROM`.

A rollout tool also accepts a move to an older release, because the running release is not older
than the target's `rollout-from`. Go back to a release below the running release's `ROLLOUT_FROM`
with the update with downtime.

## Two releases side by side

During a rollout the new container starts beside the old one on the same data volume, and the
reverse proxy sends requests to both until the old one stops.

### The viewer

The same container serves the viewer, and almost every release changes it. Each release publishes
its viewer files to `data/shell/<id>/` before it starts serving. The id is a hash of the files as the
release serves them, together with the page's content security policy. The page at `/` refers only to
`shell/<id>/...` files of its own release, and either container serves the files of any release in
`data/shell/`, with that release's headers. So a page never mixes two releases, whichever container
answers each request.

The service worker at `/sw.js` names the release that served it and installs that release's files
in one step, or not at all. A page that stays open across the switch keeps its release until it is
reloaded.

`/` and `/index.html` are sent with `Cache-Control: no-cache`, and `/sw.js` with `no-store`, so a new
release is visible at once. The `shell/<id>/...` files never change and may be cached for a year. If
Cloudflare or another cache is in front of the server, it must honour these headers: do not set an
edge cache time for `/`, `/index.html` or `/sw.js`.

`data/shell/` keeps every release's files, because pages and service workers of a release keep using
them after its container is gone. It grows by about 400 KB per release that changes the viewer. To
reclaim the space, delete directories of releases that are a few releases old while nothing still
shows them. A container that stopped while it published its files can leave a `.publish-*`
directory; delete it while no container is starting.

### The server

- SQLite runs in WAL mode on the shared volume. A check and the write that depends on it share one
  transaction where it matters (usage report order and storage limit).
- Under the schema rule, two releases in one range have the same schema, so the new container's
  startup changes nothing in the database.
- The hourly expiry sweep is safe to repeat in both containers.

Accepted behaviour while both containers run:

- Write limits and the usage write budget are counted per container, so they allow up to twice the
  configured budget for that time.
- If two desktop apps share one write id and their uploads reach the two containers in the same
  instant, a browser alert can be sent twice.
- If two browsers subscribe to alerts for one view through the two containers in the same instant,
  the view can hold one subscription above its limit of eight.
- When the old container's hourly sweep compacts the database during the switch (only when at least
  half of the file is free space), writes to the new container wait for it and can fail after five
  seconds. The desktop app sends again on its next refresh.

### Draining the old container

The image carries the `docker-rollout.pre-stop-hook` label. docker rollout runs that hook in the old
container after the new one is healthy and before it stops the old one. The hook creates
`/tmp/drain` and waits 30 seconds. While the marker exists, the image's health check fails, so the
container turns unhealthy within about 18 seconds and Traefik's Docker provider stops sending it
requests. The server then finishes its open requests when docker rollout stops it. A container
removes the marker when it starts.

The health check runs every 5 seconds, and every 2 seconds while the container starts, which needs
Docker Engine 25 or later.

If a rollout stops after the hook started, the old container stays unhealthy. Finish with exactly
one healthy container: when the new container serves, stop and remove the old one; to keep the old
release instead, set `VERSION` in `.env` back to the old release, run
`docker exec <old container> rm -f /tmp/drain`, check that it is healthy and receives requests, then
stop and remove the new one.

## The host's Compose file and `.env`

To switch an existing deployment, first run `docker compose down` with its old file and keep that
file aside, because the new one replaces it (see the project name below). Then copy
`remote/server/docker-compose.yml` and `remote/server/.env.example` into the deployment directory as
they are. Create `.env` readable only by its owner, because it can hold secrets:
`[ -e .env ] || install -m 600 .env.example .env`. Fill it:

- `VERSION`: the release to run, such as `1.11.1`. Keep exactly one `VERSION=` line.
- `DOMAIN`: the host name readers open. Traefik routes it to the service.
- Any optional setting the deployment uses, such as the `VAPID_*` or Cloudflare values. Leave the
  others commented out.

Do not set `VERSION` in the shell, because it overrides `.env`. Start the service with
`docker compose up -d --wait`.

The file is written for Traefik with its Docker provider on the external Docker network `traefik`,
with the entrypoint `https` and the middleware `default@file`. With another reverse proxy, adapt the
file. Keep `TRUST_PROXY=1` and the proxy rules in the README.

`name: ai-usage-tray` makes Compose treat the service as a new project when the deployment used
another name before, such as its directory name, so `docker compose down` with the new file would
not stop the old container. Do not leave an old `compose.yaml` in the directory: Compose prefers it
over `docker-compose.yml`.

## Going back to the Cloudflare Worker

The Worker serves the viewer from the root and has no `shell/` files. A browser whose service worker
came from the server keeps using its cached files until its service worker updates from the Worker,
and a file it has not cached yet fails to load until then.

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
