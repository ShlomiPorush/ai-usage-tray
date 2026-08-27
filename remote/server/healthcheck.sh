#!/bin/sh
set -eu

exec node -e "fetch('http://127.0.0.1:${PORT:-8080}/health').then(r=>{if(!r.ok)process.exit(1)}).catch(()=>process.exit(1))"
