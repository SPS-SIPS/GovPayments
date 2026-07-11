#!/bin/bash
set -Eeuo pipefail

TOKEN_ENV_FILE="${TOKEN_ENV_FILE:-/home/ubuntu/sps-p2g/security/production.env}"
BAO_ADDR="${BAO_ADDR:-http://127.0.0.1:8200}"
SERVICE_NAME="${SERVICE_NAME:-sps-p2g-openbao}"
RENEW_INCREMENT="${RENEW_INCREMENT:-720h}"

if ! command -v jq >/dev/null 2>&1; then
    echo "Error: jq is required but not installed."
    exit 1
fi

if [ ! -f "$TOKEN_ENV_FILE" ]; then
    echo "Error: token env file not found at $TOKEN_ENV_FILE"
    exit 1
fi

TOKEN="$(grep -E '^OpenBao__Token=' "$TOKEN_ENV_FILE" | tail -n 1 | cut -d= -f2-)"
if [ -z "$TOKEN" ]; then
    TOKEN="$(grep -E '^SPS_P2G_OPENBAO_TOKEN=' "$TOKEN_ENV_FILE" | tail -n 1 | cut -d= -f2-)"
fi

TOKEN_FILE="${TOKEN_FILE:-$(grep -E '^OpenBao__TokenFile=' "$TOKEN_ENV_FILE" | tail -n 1 | cut -d= -f2-)}"
if [ -z "$TOKEN_FILE" ]; then
    TOKEN_FILE="$(grep -E '^SPS_P2G_OPENBAO_TOKEN_FILE=' "$TOKEN_ENV_FILE" | tail -n 1 | cut -d= -f2-)"
fi

if [ -z "$TOKEN" ] && [ -n "$TOKEN_FILE" ] && [ -f "$TOKEN_FILE" ]; then
    TOKEN="$(tr -d '\r\n' < "$TOKEN_FILE")"
fi

if [ -z "$TOKEN" ]; then
    echo "Error: no OpenBao token found in $TOKEN_ENV_FILE. Set OpenBao__Token, OpenBao__TokenFile, or TOKEN_FILE."
    exit 1
fi

STATUS_JSON="$(docker exec -e BAO_ADDR="$BAO_ADDR" "$SERVICE_NAME" bao status -format=json 2>/dev/null || true)"
if ! echo "$STATUS_JSON" | jq -e '.sealed == false' >/dev/null 2>&1; then
    echo "OpenBao is sealed or unavailable; skipping token renewal."
    exit 0
fi

docker exec \
    -e BAO_ADDR="$BAO_ADDR" \
    -e BAO_TOKEN="$TOKEN" \
    "$SERVICE_NAME" \
    bao token renew -increment="$RENEW_INCREMENT" >/dev/null

TTL_SECONDS="$(docker exec \
    -e BAO_ADDR="$BAO_ADDR" \
    -e BAO_TOKEN="$TOKEN" \
    "$SERVICE_NAME" \
    bao token lookup -format=json | jq -r '.data.ttl')"

echo "OpenBao token renewed. ttl_seconds=$TTL_SECONDS"
