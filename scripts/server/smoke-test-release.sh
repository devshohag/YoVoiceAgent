#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$ROOT_DIR"; set -a; source .env.server; source .env.release; set +a
COMPOSE=(docker compose --env-file .env.server --env-file .env.release -f docker-compose.server.yml -f docker-compose.release.yml)
curl -fsS http://127.0.0.1/health/live >/dev/null
curl -fsS http://127.0.0.1/health/ready >/dev/null
curl -fsS http://127.0.0.1/ | grep -qi '<app-root\|<!doctype html'
"${COMPOSE[@]}" exec -T asterisk asterisk -rx "ari show apps" | grep -q ccaas
"${COMPOSE[@]}" exec -T asterisk asterisk -rx "pjsip show endpoints" | grep -q 1001
"${COMPOSE[@]}" ps --status exited | grep -Ev '^NAME|migrator' | grep -q . && { echo "A core container exited" >&2; exit 1; } || true
echo "Release smoke tests passed."
