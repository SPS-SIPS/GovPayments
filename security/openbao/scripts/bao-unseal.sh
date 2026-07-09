#!/bin/bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SECURITY_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"
KEYS_DIR="$SECURITY_DIR/keys"
BAO_ADDR="${BAO_ADDR:-http://127.0.0.1:8200}"
SERVICE_NAME="${SERVICE_NAME:-sps-p2g-openbao}"

if ! command -v jq >/dev/null 2>&1; then
    echo "Error: jq is required but not installed."
    exit 1
fi

if [ ! -f "$KEYS_DIR/unseal_key" ]; then
    echo "Error: Unseal key not found at $KEYS_DIR/unseal_key"
    exit 1
fi

STATUS_JSON="$(docker exec -e BAO_ADDR="$BAO_ADDR" "$SERVICE_NAME" bao status -format=json 2>/dev/null || true)"
if echo "$STATUS_JSON" | jq -e '.initialized == false' >/dev/null 2>&1; then
    echo "Error: OpenBao is not initialized. Run bao-init.sh first."
    exit 1
fi

if echo "$STATUS_JSON" | jq -e '.sealed == false' >/dev/null 2>&1; then
    echo "OpenBao is already unsealed."
    exit 0
fi

UNSEAL_KEY="$(cat "$KEYS_DIR/unseal_key")"

echo "Unsealing OpenBao..."
docker exec -e BAO_ADDR="$BAO_ADDR" "$SERVICE_NAME" bao operator unseal "$UNSEAL_KEY"
echo "Unseal command sent."
