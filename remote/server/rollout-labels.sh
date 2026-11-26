#!/usr/bin/env bash
set -euo pipefail

repository_root="$(git rev-parse --show-toplevel)"
version="$(< "$repository_root/remote/server/VERSION")"
if ! [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "remote/server/VERSION must use major.minor.patch. Received: $version" >&2
  exit 1
fi

compose_blob="$(git -C "$repository_root" cat-file blob HEAD:remote/server/compose.yaml | sha256sum)"
compose_sha256="${compose_blob%% *}"
if ! [[ "$compose_sha256" =~ ^[0-9a-f]{64}$ ]]; then
  echo "Could not hash the committed remote/server/compose.yaml" >&2
  exit 1
fi

rollout_from="$version"

echo "ai-usage-tray.compose-sha256=$compose_sha256"
echo "ai-usage-tray.rollout-from=$rollout_from"
