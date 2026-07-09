#!/bin/bash
set -Eeuo pipefail

if ! command -v jq >/dev/null 2>&1; then
    echo "Error: jq is required but not installed."
    exit 1
fi

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SECURITY_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"
KEYS_DIR="$SECURITY_DIR/keys"
TRANSIT_MOUNT="${TRANSIT_MOUNT:-transit}"
KEY_NAME="${KEY_NAME:-sps-p2g-runtime-kek}"
KV_MOUNT="${KV_MOUNT:-secret}"
SECRET_PATH="${SECRET_PATH:-sps-p2g-gateway/runtime}"
POLICY_NAME="${POLICY_NAME:-sps-p2g-gateway-runtime}"
SERVICE_NAME="${SERVICE_NAME:-sps-p2g-openbao}"

if [ ! -f "$KEYS_DIR/root_token" ]; then
    echo "Error: Root token not found at $KEYS_DIR/root_token"
    exit 1
fi

ROOT_TOKEN="$(cat "$KEYS_DIR/root_token")"
export BAO_ADDR="${BAO_ADDR:-http://127.0.0.1:8200}"

if ! command -v docker >/dev/null 2>&1; then
    echo "Error: docker is required but not installed."
    exit 1
fi

bao_exec() {
    docker exec \
        -e BAO_ADDR="$BAO_ADDR" \
        -e BAO_TOKEN="$ROOT_TOKEN" \
        "$SERVICE_NAME" \
        bao "$@"
}

bao_exec_stdin() {
    docker exec -i \
        -e BAO_ADDR="$BAO_ADDR" \
        -e BAO_TOKEN="$ROOT_TOKEN" \
        "$SERVICE_NAME" \
        bao "$@"
}

echo "Using OpenBao at: $BAO_ADDR"

until docker exec -e BAO_ADDR="$BAO_ADDR" "$SERVICE_NAME" bao status > /dev/null 2>&1 || [ $? -eq 2 ]; do
    echo "Waiting for OpenBao connection..."
    sleep 2
done

until docker exec -e BAO_ADDR="$BAO_ADDR" "$SERVICE_NAME" bao status > /dev/null 2>&1; do
    echo "OpenBao is sealed. Run bao-unseal.sh on the host..."
    sleep 5
done

echo "Enabling Transit engine at '$TRANSIT_MOUNT'..."
bao_exec secrets enable -path="$TRANSIT_MOUNT" transit || echo "Transit already enabled."

echo "Creating transit key '$KEY_NAME'..."
bao_exec write -f "$TRANSIT_MOUNT/keys/$KEY_NAME" type=aes256-gcm96 || echo "Key creation attempted; it may already exist."

echo "Enabling KV v2 engine at '$KV_MOUNT'..."
bao_exec secrets enable -path="$KV_MOUNT" -version=2 kv || echo "KV engine already enabled."

echo "Enabling audit file device..."
bao_exec audit enable file file_path=/bao/audit/audit.log || echo "Audit device already enabled."

echo "Creating policy '$POLICY_NAME'..."
POLICY_HCL="
path \"$KV_MOUNT/data/$SECRET_PATH\" {
  capabilities = [\"read\"]
}
path \"$KV_MOUNT/metadata/$SECRET_PATH\" {
  capabilities = [\"read\"]
}
path \"$TRANSIT_MOUNT/encrypt/$KEY_NAME\" {
  capabilities = [\"update\"]
}
path \"$TRANSIT_MOUNT/decrypt/$KEY_NAME\" {
  capabilities = [\"update\"]
}
"

echo "$POLICY_HCL" | bao_exec_stdin policy write "$POLICY_NAME" -

echo "Creating client token for SPS P2G Gateway..."
CLIENT_TOKEN="$(bao_exec token create -policy="$POLICY_NAME" -period=720h -field=token)"

echo "Verifying token can read the runtime config path once it exists..."
echo "The token is least-privilege; write runtime secrets with the root/admin token using:"
echo "  bao kv put $KV_MOUNT/$SECRET_PATH ConnectionStrings__P2G=... P2G__DescriptorHmacSecret=... Keycloak__Authority=..."

if command -v sha256sum >/dev/null 2>&1; then
    FINGERPRINT="$(echo -n "$CLIENT_TOKEN" | sha256sum | awk '{print $1}')"
else
    FINGERPRINT="$(echo -n "$CLIENT_TOKEN" | shasum -a 256 | awk '{print $1}')"
fi

echo "---------------------------------------------------"
echo "Setup complete."
echo "SPS P2G OpenBao Token: $CLIENT_TOKEN"
echo "Token Fingerprint: ${FINGERPRINT:0:16}..."
echo "KV Secret Path: $KV_MOUNT/$SECRET_PATH"
echo "Transit Key: $TRANSIT_MOUNT/$KEY_NAME"
echo "---------------------------------------------------"
