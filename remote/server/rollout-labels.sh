#!/usr/bin/env bash
set -euo pipefail

repository_root="$(git rev-parse --show-toplevel)"
version="$(< "$repository_root/remote/server/VERSION")"
compose_sha256="$(git -C "$repository_root" cat-file blob HEAD:remote/server/compose.yaml | sha256sum)"

echo "ai-usage-tray.compose-sha256=${compose_sha256%% *}"
echo "ai-usage-tray.rollout-from=$version"
