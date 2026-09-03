#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$ROOT_DIR"
[[ -f .env.server ]] || { echo "Missing .env.server. Run scripts/server/generate-secrets.sh" >&2; exit 1; }
set -a; source .env.server; set +a
for cmd in docker python3 curl openssl; do command -v "$cmd" >/dev/null || { echo "Missing command: $cmd" >&2; exit 1; }; done
docker compose version >/dev/null
[[ "${SERVER_PUBLIC_IP:-}" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]] || { echo "SERVER_PUBLIC_IP must be IPv4" >&2; exit 1; }
if grep -q 'CHANGE_ME' .env.server; then echo "Replace every CHANGE_ME in .env.server" >&2; exit 1; fi
available_kb=$(awk '/MemAvailable/ {print $2}' /proc/meminfo)
[[ "$available_kb" -ge 7000000 ]] || echo "WARNING: less than 7 GB currently available; first AI model load may fail."
df -Pk . | awk 'NR==2 {if ($4 < 30000000) {print "WARNING: less than 30 GB disk free."}}'
docker compose --env-file .env.server -f docker-compose.server.yml config -q
echo "Preflight passed for $SERVER_PUBLIC_IP"
