#!/usr/bin/env bash
set -euo pipefail

repository_root="$(git rev-parse --show-toplevel)"
version="$(< "$repository_root/remote/server/VERSION")"
rollout_from="$(< "$repository_root/remote/server/ROLLOUT_FROM")"
if ! [[ "$rollout_from" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "remote/server/ROLLOUT_FROM must use major.minor.patch. Received: $rollout_from" >&2
  exit 1
fi
if [ "$(printf '%s\n%s\n' "$rollout_from" "$version" | sort -V | tail -n 1)" != "$version" ]; then
  echo "remote/server/ROLLOUT_FROM ($rollout_from) must not be above VERSION ($version)" >&2
  exit 1
fi
compose_sha256="$(git -C "$repository_root" cat-file blob HEAD:remote/server/docker-compose.yml | sha256sum)"

echo "ai-usage-tray.compose-sha256=${compose_sha256%% *}"
echo "ai-usage-tray.rollout-from=$rollout_from"
