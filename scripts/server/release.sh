#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$ROOT_DIR"
NEW_VERSION=${1:?Usage: release.sh vX.Y.Z}
[[ "$NEW_VERSION" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([.-][A-Za-z0-9.-]+)?$ ]] || { echo "Invalid release tag: $NEW_VERSION" >&2; exit 1; }
[[ -f .env.server && -f .env.release ]] || { echo "Missing .env.server or .env.release" >&2; exit 1; }

set -a; source .env.server; source .env.release; set +a
PREVIOUS_VERSION=${CCAAS_VERSION:-}
COMPOSE=(docker compose --env-file .env.server --env-file .env.release -f docker-compose.server.yml -f docker-compose.release.yml)

set_version() {
  python3 - "$1" <<'PY'
from pathlib import Path
import re, sys
p=Path('.env.release'); text=p.read_text()
text=re.sub(r'^CCAAS_VERSION=.*$', 'CCAAS_VERSION='+sys.argv[1], text, flags=re.M)
p.write_text(text)
PY
  export CCAAS_VERSION="$1"
}

rollback() {
  trap - ERR
  if [[ -n "$PREVIOUS_VERSION" && "$PREVIOUS_VERSION" != "$NEW_VERSION" ]]; then
    echo "Release failed; rolling back to $PREVIOUS_VERSION" >&2
    set_version "$PREVIOUS_VERSION"
    "${COMPOSE[@]}" up -d --no-build
  fi
}
trap rollback ERR

bash scripts/server/backup.sh
set_version "$NEW_VERSION"
"${COMPOSE[@]}" pull
"${COMPOSE[@]}" --profile migration run --rm migrator
"${COMPOSE[@]}" up -d --no-build --remove-orphans

for attempt in $(seq 1 60); do
  if curl -fsS http://127.0.0.1/health/ready >/dev/null 2>&1; then
    bash scripts/server/smoke-test-release.sh
    trap - ERR
    echo "Release $NEW_VERSION deployed successfully."
    exit 0
  fi
  sleep 5
done
echo "Release did not become healthy." >&2
exit 1
