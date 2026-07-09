#!/bin/bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SECURITY_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"
OPENBAO_UID="${OPENBAO_UID:-100}"
OPENBAO_GID="${OPENBAO_GID:-1000}"

mkdir -p "$SECURITY_DIR/data" "$SECURITY_DIR/logs" "$SECURITY_DIR/audit" "$SECURITY_DIR/keys"

if [ "$(id -u)" -eq 0 ]; then
    chown -R "$OPENBAO_UID:$OPENBAO_GID" "$SECURITY_DIR/data" "$SECURITY_DIR/logs" "$SECURITY_DIR/audit"
    chmod 770 "$SECURITY_DIR/data" "$SECURITY_DIR/logs" "$SECURITY_DIR/audit"
elif command -v sudo >/dev/null 2>&1; then
    sudo chown -R "$OPENBAO_UID:$OPENBAO_GID" "$SECURITY_DIR/data" "$SECURITY_DIR/logs" "$SECURITY_DIR/audit"
    sudo chmod 770 "$SECURITY_DIR/data" "$SECURITY_DIR/logs" "$SECURITY_DIR/audit"
else
    echo "Error: sudo is required to assign OpenBao directory ownership."
    echo "Run as root, install sudo, or manually chown data/logs/audit to $OPENBAO_UID:$OPENBAO_GID."
    exit 1
fi

chmod 700 "$SECURITY_DIR/keys"

echo "OpenBao directories are ready under $SECURITY_DIR."
