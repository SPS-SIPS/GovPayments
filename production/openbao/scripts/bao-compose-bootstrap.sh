#!/bin/sh
set -eu

BAO_ADDR="${BAO_ADDR:-http://openbao:8200}"
SECURITY_DIR="${SECURITY_DIR:-/security/openbao}"
KEYS_DIR="$SECURITY_DIR/keys"
RUNTIME_DIR="$SECURITY_DIR/runtime"
TRANSIT_MOUNT="${TRANSIT_MOUNT:-transit}"
KEY_NAME="${KEY_NAME:-sps-p2g-runtime-kek}"
KV_MOUNT="${KV_MOUNT:-secret}"
SECRET_PATH="${SECRET_PATH:-sps-p2g-gateway/runtime}"
POLICY_NAME="${POLICY_NAME:-sps-p2g-gateway-runtime}"
TOKEN_FILE="$RUNTIME_DIR/gateway_token"

mkdir -p "$KEYS_DIR" "$RUNTIME_DIR"
chmod 700 "$KEYS_DIR" "$RUNTIME_DIR" || true

export BAO_ADDR

echo "Waiting for OpenBao at $BAO_ADDR..."
until bao status >/tmp/bao-status.txt 2>/dev/null || [ "$?" -eq 2 ]; do
    sleep 2
done

STATUS_JSON="$(bao status -format=json 2>/dev/null || true)"
if ! printf '%s' "$STATUS_JSON" | grep -q '"initialized":[[:space:]]*true'; then
    echo "Initializing OpenBao..."
    INIT_OUTPUT="$(bao operator init -key-shares=1 -key-threshold=1)"
    printf '%s\n' "$INIT_OUTPUT" > "$KEYS_DIR/init_output.txt"
    printf '%s\n' "$INIT_OUTPUT" | awk -F': ' '/Unseal Key 1/ {print $2}' > "$KEYS_DIR/unseal_key"
    printf '%s\n' "$INIT_OUTPUT" | awk -F': ' '/Initial Root Token/ {print $2}' > "$KEYS_DIR/root_token"
    chmod 600 "$KEYS_DIR/init_output.txt" "$KEYS_DIR/unseal_key" "$KEYS_DIR/root_token" || true
fi

if [ ! -s "$KEYS_DIR/unseal_key" ] || [ ! -s "$KEYS_DIR/root_token" ]; then
    echo "OpenBao keys are missing. Restore $KEYS_DIR or recreate the OpenBao data directory."
    exit 1
fi

STATUS_JSON="$(bao status -format=json 2>/dev/null || true)"
if printf '%s' "$STATUS_JSON" | grep -q '"sealed":[[:space:]]*true'; then
    echo "Unsealing OpenBao..."
    bao operator unseal "$(cat "$KEYS_DIR/unseal_key")" >/dev/null
fi

export BAO_TOKEN="$(cat "$KEYS_DIR/root_token")"

bao secrets enable -path="$TRANSIT_MOUNT" transit >/dev/null 2>&1 || true
bao write -f "$TRANSIT_MOUNT/keys/$KEY_NAME" type=aes256-gcm96 >/dev/null 2>&1 || true
bao secrets enable -path="$KV_MOUNT" -version=2 kv >/dev/null 2>&1 || true
bao audit enable file file_path=/bao/audit/audit.log >/dev/null 2>&1 || true

cat >/tmp/sps-p2g-policy.hcl <<POLICY
path "$KV_MOUNT/data/$SECRET_PATH" {
  capabilities = ["read"]
}
path "$KV_MOUNT/metadata/$SECRET_PATH" {
  capabilities = ["read"]
}
path "$TRANSIT_MOUNT/encrypt/$KEY_NAME" {
  capabilities = ["update"]
}
path "$TRANSIT_MOUNT/decrypt/$KEY_NAME" {
  capabilities = ["update"]
}
POLICY
bao policy write "$POLICY_NAME" /tmp/sps-p2g-policy.hcl >/dev/null

if [ ! -s "$TOKEN_FILE" ] || ! BAO_TOKEN="$(cat "$TOKEN_FILE")" bao token lookup >/dev/null 2>&1; then
    BAO_TOKEN="$BAO_TOKEN" bao token create -policy="$POLICY_NAME" -period=720h -field=token > "$TOKEN_FILE"
    chmod 600 "$TOKEN_FILE" || true
fi

export BAO_TOKEN="$(cat "$KEYS_DIR/root_token")"

bao kv put "$KV_MOUNT/$SECRET_PATH" \
    ConnectionStrings__P2G="${P2G_CONNECTION_STRING:-Host=postgres;Database=sps_p2g_gateway;Username=sps_p2g;Password=change-this-postgres-password}" \
    P2G__DescriptorHmacSecret="${P2G_DESCRIPTOR_HMAC_SECRET:-change-this-descriptor-hmac-secret-at-least-32-chars}" \
    P2G__InternalApiKey="${P2G_INTERNAL_API_KEY:-}" \
    Keycloak__Enabled="${P2G_KEYCLOAK_ENABLED:-true}" \
    Keycloak__Authority="${P2G_KEYCLOAK_AUTHORITY:-https://keycloak.example/realms/sps}" \
    Keycloak__Audience="${P2G_KEYCLOAK_AUDIENCE:-sps-p2g-gateway}" \
    Keycloak__RequireHttpsMetadata="${P2G_KEYCLOAK_REQUIRE_HTTPS_METADATA:-true}" \
    Keycloak__RevenueAuthorityRole="${P2G_KEYCLOAK_REVENUE_AUTHORITY_ROLE:-p2g-authority}" \
    Keycloak__SipsConnectRole="${P2G_KEYCLOAK_SIPS_CONNECT_ROLE:-p2g-sips-connect}" \
    Keycloak__DashboardRole="${P2G_KEYCLOAK_DASHBOARD_ROLE:-p2g-dashboard}" \
    Keycloak__ConfigurationAdminRole="${P2G_KEYCLOAK_CONFIGURATION_ADMIN_ROLE:-p2g-configuration-admin}" \
    Keycloak__SipsConnectClientId="${P2G_KEYCLOAK_SIPS_CONNECT_CLIENT_ID:-sips-connect-p2g}" \
    SipsConnectClient__Enabled="${SIPS_CONNECT_CLIENT_ENABLED:-false}" \
    SipsConnectClient__BaseUrl="${SIPS_CONNECT_BASE_URL:-https://sips-connect-uat.example}" \
    SipsConnectClient__TokenEndpoint="${SIPS_CONNECT_TOKEN_ENDPOINT:-https://keycloak.example/realms/sips/protocol/openid-connect/token}" \
    SipsConnectClient__ClientId="${SIPS_CONNECT_CLIENT_ID:-sps-p2g-gateway}" \
    SipsConnectClient__ClientSecret="${SIPS_CONNECT_CLIENT_SECRET:-change-this-sips-connect-client-secret}" \
    SipsConnectClient__Scope="${SIPS_CONNECT_SCOPE:-}" \
    SipsConnectClient__PaymentStatusPath="${SIPS_CONNECT_PAYMENT_STATUS_PATH:-/api/v1/payments/status}" \
    SipsConnectClient__RequestTimeoutSeconds="${SIPS_CONNECT_REQUEST_TIMEOUT_SECONDS:-30}" \
    SipsConnectClient__RequireHttps="${SIPS_CONNECT_REQUIRE_HTTPS:-true}" \
    SipsConnectClient__AllowInvalidServerCertificate="${SIPS_CONNECT_ALLOW_INVALID_SERVER_CERTIFICATE:-false}" \
    Auth__AllowAuthorityApiKeyFallback="${P2G_AUTH_ALLOW_AUTHORITY_API_KEY_FALLBACK:-false}" \
    Auth__AllowSipsConnectInternalKeyFallback="${P2G_AUTH_ALLOW_SIPS_CONNECT_INTERNAL_KEY_FALLBACK:-false}" \
    CallbackSecurity__RequireHttps="${P2G_CALLBACK_SECURITY_REQUIRE_HTTPS:-true}" \
    CallbackSecurity__AllowPrivateNetworkTargets="${P2G_CALLBACK_SECURITY_ALLOW_PRIVATE_NETWORK_TARGETS:-false}" \
    DataProtection__PersistKeysToFileSystemPath="${P2G_DATA_PROTECTION_KEYS_PATH:-/var/lib/sps-p2g-gateway/data-protection-keys}" \
    >/dev/null

echo "OpenBao bootstrap complete. Gateway token written to $TOKEN_FILE."
