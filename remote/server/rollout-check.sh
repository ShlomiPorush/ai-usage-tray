#!/usr/bin/env bash
set -euo pipefail

base="$1"
version="$(< remote/server/VERSION)"
rollout_from="$(< remote/server/ROLLOUT_FROM)"
base_rollout_from=""
if git cat-file -e "$base:remote/server/ROLLOUT_FROM" 2>/dev/null; then
  base_rollout_from="$(git show "$base:remote/server/ROLLOUT_FROM")"
fi
if [ -n "$base_rollout_from" ] && [ "$rollout_from" != "$base_rollout_from" ] && [ "$rollout_from" != "$version" ]; then
  echo "remote/server/ROLLOUT_FROM may only stay at $base_rollout_from or move to VERSION ($version)" >&2
  exit 1
fi
changes="$(git diff --no-renames --name-status "$base" HEAD -- remote/server/rollout-schema/)"
while IFS=$'\t' read -r status path; do
  [ -n "$status" ] || continue
  if [ "$status" != A ] || [ "$path" != "remote/server/rollout-schema/$version.json" ]; then
    echo "$path: a schema snapshot is never changed or removed, and a new one is named after VERSION ($version.json)" >&2
    exit 1
  fi
done <<< "$changes"
