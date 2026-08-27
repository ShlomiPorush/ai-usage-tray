#!/bin/sh
set -eu

DB_PATH="${DATABASE_PATH:-/data/usage.db}"
DATA_DIR="$(dirname "$DB_PATH")"

# Permission bits lie on fuse/NFS/Windows mounts, so probe with real writes:
# create a file in the data directory and open every existing database file
# for append.
if [ "${1:-}" = "--write-probe" ]; then
  probe_file="$DATA_DIR/.write-probe"
  : >> "$probe_file" || exit 1
  rm -f "$probe_file" || exit 1
  for file in "$DB_PATH" "$DB_PATH-wal" "$DB_PATH-shm"; do
    if [ -e "$file" ]; then
      : >> "$file" || exit 1
    fi
  done
  exit 0
fi

# Dropping to the remoteview user needs CAP_SETUID/CAP_SETGID. Under
# cap_drop: ALL those are gone and su-exec fails; then we keep running as
# the started user, which still owns the files it created.
can_drop() {
  [ "$(id -u)" -eq 0 ] && su-exec 10001:10001 true 2>/dev/null
}

if [ "$DB_PATH" = ":memory:" ]; then
  if can_drop; then
    exec su-exec 10001:10001 "$@"
  fi
  exec "$@"
fi

if [ "$(id -u)" -eq 0 ]; then
  mkdir -p "$DATA_DIR" 2>/dev/null || true
  chown -R 10001:10001 "$DATA_DIR" 2>/dev/null || true
  chmod -R u+rwX "$DATA_DIR" 2>/dev/null || true

  if can_drop && su-exec 10001:10001 sh "$0" --write-probe 2>/dev/null; then
    exec su-exec 10001:10001 "$@"
  fi

  if sh "$0" --write-probe 2>/dev/null; then
    exec "$@"
  fi

  echo "Remote view cannot write database files in $DATA_DIR." >&2
  echo "Make the mapped directory writable on the host, then restart the container." >&2
  exit 1
fi

if ! sh "$0" --write-probe 2>/dev/null; then
  echo "Remote view cannot write database files in $DATA_DIR as UID $(id -u)." >&2
  echo "Remove the container 'user:' override so the image can prepare $DATA_DIR itself," >&2
  echo "or run on the host: chown -R $(id -u):$(id -g) <mapped data directory>." >&2
  exit 1
fi

exec "$@"
