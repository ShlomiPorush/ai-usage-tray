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

# Running the server as container root is a silent downgrade, so say exactly
# what is missing and how to fix it. RELAY_REQUIRE_NONROOT=1 turns it into a
# startup failure; unset keeps older deployments running.
report_root_fallback() {
  cat >&2 <<EOF

================================================================================
$1: the remote view could not drop to the unprivileged remoteview user (UID 10001),
so the server would run as root inside the container.

Switching users needs CAP_SETUID and CAP_SETGID, plus CAP_CHOWN and
CAP_DAC_OVERRIDE to prepare $DATA_DIR. Under "cap_drop: ALL" alone none of them
are granted.

Fix it in the Compose service (the server itself still runs with no capabilities):

    cap_drop:
      - ALL
    cap_add:
      - CHOWN
      - DAC_OVERRIDE
      - SETGID
      - SETUID

Or keep the capabilities dropped and prepare the directory on the host instead:

    chown -R 10001:10001 <host directory mapped to $DATA_DIR>

and add 'user: "10001:10001"' to the service.

$2
================================================================================

EOF
}

# Prints the fallback banner, then stops when the operator demanded non-root.
handle_root_fallback() {
  if [ "${RELAY_REQUIRE_NONROOT:-}" = "1" ]; then
    report_root_fallback "ERROR" \
      "RELAY_REQUIRE_NONROOT=1 is set, so the remote view will not start."
    exit 1
  fi
  report_root_fallback "WARNING" \
    "Set RELAY_REQUIRE_NONROOT=1 to refuse to start instead of running as root."
}

if [ "$DB_PATH" = ":memory:" ]; then
  if can_drop; then
    exec su-exec 10001:10001 "$@"
  fi
  if [ "$(id -u)" -eq 0 ]; then
    handle_root_fallback
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
    handle_root_fallback
    exec "$@"
  fi

  # Typical cause: the mapped directory belongs to UID 10001 while the container
  # has neither CAP_DAC_OVERRIDE to write through it nor CAP_SETUID to become
  # that user.
  echo "Remote view cannot write database files in $DATA_DIR." >&2
  echo "Add 'cap_add: [CHOWN, DAC_OVERRIDE, SETGID, SETUID]' to the service so the" >&2
  echo "entrypoint can prepare $DATA_DIR and run as UID 10001, or make the mapped" >&2
  echo "directory writable on the host, then restart the container." >&2
  exit 1
fi

if ! sh "$0" --write-probe 2>/dev/null; then
  echo "Remote view cannot write database files in $DATA_DIR as UID $(id -u)." >&2
  echo "Remove the container 'user:' override so the image can prepare $DATA_DIR itself," >&2
  echo "or run on the host: chown -R $(id -u):$(id -g) <mapped data directory>." >&2
  exit 1
fi

exec "$@"
