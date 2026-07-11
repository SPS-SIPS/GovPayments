#!/bin/sh
set -eu

SCRIPT_DIR="$(cd -- "$(dirname -- "$0")" && pwd)"
CERT_DIR="${CERT_DIR:-$(cd -- "$SCRIPT_DIR/.." && pwd)/certs}"
CERT_NAME="${CERT_NAME:-sps-p2g}"
CERT_HOST_IP="${CERT_HOST_IP:-10.128.200.244}"
CERT_DNS_NAMES="${CERT_DNS_NAMES:-localhost,sps-p2g-gateway,sps-p2g-openbao}"
CERT_DAYS="${CERT_DAYS:-825}"

CRT_FILE="$CERT_DIR/$CERT_NAME.crt"
KEY_FILE="$CERT_DIR/$CERT_NAME.key"
CONFIG_FILE="$CERT_DIR/$CERT_NAME.openssl.cnf"

mkdir -p "$CERT_DIR"

if [ -s "$CRT_FILE" ] && [ -s "$KEY_FILE" ]; then
    echo "Certificate already exists at $CRT_FILE and $KEY_FILE."
    exit 0
fi

cat > "$CONFIG_FILE" <<EOF
[req]
default_bits = 4096
prompt = no
default_md = sha256
distinguished_name = dn
x509_extensions = v3_req

[dn]
CN = $CERT_HOST_IP

[v3_req]
subjectAltName = @alt_names
keyUsage = critical, digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth

[alt_names]
IP.1 = $CERT_HOST_IP
EOF

index=1
old_ifs="$IFS"
IFS=","
for dns_name in $CERT_DNS_NAMES; do
    IFS="$old_ifs"
    dns_name="$(printf '%s' "$dns_name" | xargs)"
    if [ -n "$dns_name" ]; then
        echo "DNS.$index = $dns_name" >> "$CONFIG_FILE"
        index=$((index + 1))
    fi
    IFS=","
done
IFS="$old_ifs"

openssl req -x509 -nodes -newkey rsa:4096 \
    -keyout "$KEY_FILE" \
    -out "$CRT_FILE" \
    -days "$CERT_DAYS" \
    -config "$CONFIG_FILE"

chmod 600 "$KEY_FILE"
chmod 644 "$CRT_FILE" "$CONFIG_FILE"

echo "Self-signed certificate generated at $CRT_FILE for IP $CERT_HOST_IP."
