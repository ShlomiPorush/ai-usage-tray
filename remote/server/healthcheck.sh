#!/bin/sh
set -eu

if [ -e /tmp/drain ]; then
  exit 1
fi

exec node -e "fetch('http://127.0.0.1:${PORT:-8080}/health').then(r=>{if(!r.ok)process.exit(1)}).catch(()=>process.exit(1))"
