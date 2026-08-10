#!/usr/bin/env bash
# CHORUS gateway launcher (real engines) — resolves the Hermes API key from
# the Hermes env file at startup (systemd can't expand ${VAR} across lines).
set -euo pipefail
cd /opt/chorus
export CHORUS_MOCK="${CHORUS_MOCK:-0}"
export CHORUS_PORT="${CHORUS_PORT:-8765}"
export KIMI_BASE_URL="${KIMI_BASE_URL:-http://127.0.0.1:8642/v1}"
export KIMI_MODEL="${KIMI_MODEL:-deepseek-v4-flash}"
if [ -z "${KIMI_API_KEY:-}" ] && [ -f /root/.hermes/.env ]; then
  export KIMI_API_KEY="$(grep -oP '(?<=^API_SERVER_KEY=).*' /root/.hermes/.env | head -1)"
fi
exec ./venv/bin/python -m core.gateway
