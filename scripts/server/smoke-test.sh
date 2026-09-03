#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$ROOT_DIR"
set -a; source .env.server; set +a
COMPOSE=(docker compose --env-file .env.server -f docker-compose.server.yml)
curl -fsS http://127.0.0.1/health/live >/dev/null
curl -fsS http://127.0.0.1/health/ready >/dev/null
curl -fsS http://127.0.0.1/ >/dev/null
"${COMPOSE[@]}" exec -T asterisk asterisk -rx "ari show apps" | grep -q ccaas
"${COMPOSE[@]}" exec -T asterisk asterisk -rx "pjsip show endpoints" | grep -q 1001
"${COMPOSE[@]}" exec -T local-ai python -c "import urllib.request; urllib.request.urlopen('http://localhost:8080/health')"
"${COMPOSE[@]}" exec -T ollama ollama list | grep -q "${OLLAMA_MODEL:-qwen3:4b}"
echo "Smoke tests passed. UI: http://${SERVER_PUBLIC_IP}  SIP: ${SERVER_PUBLIC_IP}:5060 (TCP)"
