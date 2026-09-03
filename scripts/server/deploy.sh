#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$ROOT_DIR"
scripts/server/preflight.sh
scripts/server/render-asterisk-config.sh
docker compose --env-file .env.server -f docker-compose.server.yml up -d --build
echo "Waiting for API readiness (first model download can take several minutes)..."
for attempt in $(seq 1 90); do
  if curl -fsS http://127.0.0.1/health/ready >/dev/null 2>&1; then
    scripts/server/smoke-test.sh
    exit 0
  fi
  sleep 5
done
docker compose --env-file .env.server -f docker-compose.server.yml ps
docker compose --env-file .env.server -f docker-compose.server.yml logs --tail 120 ccaas-api telephony-worker local-ai ollama asterisk
echo "Deployment did not become ready within 7.5 minutes." >&2
exit 1
