# SPS P2G Production Compose

This directory is a production/UAT deployment bundle for hosts that should consume the published GitHub Container Registry image instead of building the gateway locally.

## Image

The gateway service pulls:

```text
ghcr.io/hanadderia/sps-p2g-gateway:${GATEWAY_IMAGE_TAG:-latest}
```

If the GitHub package is private, authenticate the deployment host first:

```bash
echo "$GHCR_TOKEN" | docker login ghcr.io -u "$GHCR_USER" --password-stdin
```

## First Run

```bash
cd production
cp .env.example .env
```

Edit `.env` before startup. At minimum replace:

- `POSTGRES_PASSWORD`
- `P2G_DESCRIPTOR_HMAC_SECRET`
- `P2G_INTERNAL_API_KEY` when SIPS Connect internal-key fallback is used
- `P2G_KEYCLOAK_AUTHORITY`
- `GATEWAY_IMAGE_TAG` when deploying a pinned commit SHA instead of `latest`

The default host bindings are:

```text
HOST_IP=10.128.200.244
GATEWAY_HOST_PORT=8991
POSTGRES_HOST_PORT=5435
OPENBAO_HOST_PORT=8992
```

Start the stack:

```bash
docker compose pull
docker compose up -d
```

The production compose stack starts Postgres, OpenBao, an idempotent OpenBao bootstrap job, the gateway, and an HTTPS edge proxy. Keycloak is expected to be an external production identity service; its issuer URL is written to OpenBao from `.env`.

## HTTPS Certificate

The `cert-init` service generates a self-signed certificate on startup when `certs/sps-p2g.crt` and `certs/sps-p2g.key` are missing. The certificate includes `HOST_IP` as an IP subject alternative name and uses `CERT_DNS_NAMES` for DNS subject alternative names.

You can also generate it manually:

```bash
HOST_IP=10.128.200.244 ./scripts/generate-self-signed-cert.sh
```

Clients must trust `certs/sps-p2g.crt` or call the endpoints with certificate verification disabled while this self-signed certificate is in use.

## Runtime Directories

The compose file uses bind-mounted directories under this folder:

```text
gateway/data-protection-keys/
certs/
nginx/conf.d/
openbao/audit/
openbao/data/
openbao/keys/
openbao/logs/
openbao/runtime/
postgres/data/
```

Postgres uses `PGDATA=/var/lib/postgresql/data/pgdata`, so the actual database cluster is stored under `postgres/data/pgdata`. This keeps the bind mount compatible with the tracked `.gitkeep` placeholder in `postgres/data`.

The generated contents are ignored by git. Back up `openbao/keys`, `openbao/data`, `openbao/runtime`, `postgres/data`, and `gateway/data-protection-keys` according to the operations runbook.

## Ports

Defaults:

```text
Gateway: https://10.128.200.244:8991
OpenBao: https://10.128.200.244:8992
Postgres: 10.128.200.244:5435
```

Override `HOST_IP`, `GATEWAY_HOST_PORT`, `OPENBAO_HOST_PORT`, and `POSTGRES_HOST_PORT` in `.env` when needed.

## Checks

```bash
docker compose ps
curl -k -fsS https://${HOST_IP:-10.128.200.244}:${GATEWAY_HOST_PORT:-8991}/health/ready
curl -k -fsS https://${HOST_IP:-10.128.200.244}:${OPENBAO_HOST_PORT:-8992}/v1/sys/health
docker compose logs --tail=120 gateway openbao-bootstrap edge-proxy
```
