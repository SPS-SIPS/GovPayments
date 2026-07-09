#!/bin/bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SECURITY_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"
KEYS_DIR="$SECURITY_DIR/keys"
BAO_ADDR="${BAO_ADDR:-http://127.0.0.1:8200}"
SERVICE_NAME="${SERVICE_NAME:-sps-p2g-openbao}"

mkdir -p "$KEYS_DIR"
chmod 700 "$KEYS_DIR"

if ! command -v jq >/dev/null 2>&1; then
    echo "Error: jq is required but not installed."
    exit 1
fi

STATUS_JSON="$(docker exec -e BAO_ADDR="$BAO_ADDR" "$SERVICE_NAME" bao status -format=json 2>/dev/null || true)"
if echo "$STATUS_JSON" | jq -e '.initialized == true' >/dev/null 2>&1; then
    if [ ! -f "$KEYS_DIR/unseal_key" ] || [ ! -f "$KEYS_DIR/root_token" ]; then
        echo "Error: OpenBao is already initialized, but key files are missing under $KEYS_DIR."
        echo "Restore the original unseal/root token files before restarting OpenBao or reinitializing a clean vault."
        exit 1
    fi

    echo "OpenBao is already initialized."
    echo "Existing key files are kept under $KEYS_DIR."
    exit 0
fi

echo "Initializing OpenBao..."
docker exec -e BAO_ADDR="$BAO_ADDR" "$SERVICE_NAME" bao operator init -key-shares=1 -key-threshold=1 -format=json > "$KEYS_DIR/init_output.json"

echo "Initialization complete. Keys saved to $KEYS_DIR/init_output.json"
echo "WARNING: Protect this file. It contains the unseal key and root token."

jq -r ".unseal_keys_b64[0]" "$KEYS_DIR/init_output.json" > "$KEYS_DIR/unseal_key"
jq -r ".root_token" "$KEYS_DIR/init_output.json" > "$KEYS_DIR/root_token"
chmod 600 "$KEYS_DIR/init_output.json" "$KEYS_DIR/unseal_key" "$KEYS_DIR/root_token"

echo "Unseal Key: $KEYS_DIR/unseal_key"
echo "Root Token: $KEYS_DIR/root_token"
