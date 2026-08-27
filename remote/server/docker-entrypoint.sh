#!/bin/sh
set -eu

if [ "$(id -u)" -eq 0 ]; then
  if ! chown -R 10001:10001 /data; then
    echo "Remote view cannot prepare the mapped /data directory." >&2
    exit 1
  fi

  exec su-exec 10001:10001 "$@"
fi

if [ ! -w /data ]; then
  echo "Remote view cannot write to /data as UID $(id -u). Remove the container user override." >&2
  exit 1
fi

exec "$@"
