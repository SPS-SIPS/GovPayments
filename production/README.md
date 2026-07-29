# Government Payments Project Production Compose

This directory is a production/UAT deployment bundle for hosts that should consume the published GitHub Container Registry image instead of building the gateway locally.

## Image

The gateway service pulls:

```text
ghcr.io/sps-sips/govpayments:${GATEWAY_IMAGE_TAG:-latest}
```

If the GitHub package is private, authenticate Docker on the deployment host
under the same Unix account that runs Docker Compose:

```bash
./scripts/login-ghcr.sh
```

The helper prompts for a GitHub deployment account and token, keeps the token
out of `.env` and shell history, and verifies access to the configured image.

GitHub Packages currently does not support fine-grained personal access tokens
for registry authentication. Do not use a staff member's classic token because
`read:packages` applies to every package that person can read. For manual
server deployments:

1. Create a dedicated GitHub deployment account with no staff privileges.
2. In the `govpayments` package settings, give that account read access only to
   this package. Remove inherited access if it would grant broader repository
   or package permissions.
3. Create a short-lived classic token for the deployment account with only
   `read:packages`.
4. If the organization enforces SSO, authorize that token for `sps-sips`.

This confines the classic token to the packages visible to the isolated
deployment identity.

For non-interactive automation, pass credentials through the process
environment:

```bash
GHCR_USER="<deployment-account>" GHCR_TOKEN="<classic-read-token>" ./scripts/login-ghcr.sh
```

Do not save `GHCR_TOKEN` in the production `.env` file. Docker stores the login
in the current user's Docker configuration. Pull the image and then immediately
remove that stored credential:

```bash
docker compose pull gateway
docker logout ghcr.io
```

For automated deployments, prefer a self-hosted GitHub Actions runner on the
deployment host. Grant the workflow `packages: read`, grant its repository
GitHub Actions access to the package, authenticate with the job-scoped
`GITHUB_TOKEN`, pull the image, and log out. This avoids a long-lived registry
credential on the server.

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
- `SIPS_CONNECT_BASE_URL`
- `SIPS_CONNECT_TOKEN_ENDPOINT`
- `SIPS_CONNECT_CLIENT_ID`
- `SIPS_CONNECT_CLIENT_SECRET`
- `SIPS_CONNECT_CLIENT_ENABLED=true` after the real SIPS environment values are configured
- `GATEWAY_IMAGE_TAG` when deploying a pinned commit SHA instead of `latest`

The sample host bindings are localhost-only. Replace them in private deployment configuration:

```text
HOST_IP=127.0.0.1
GATEWAY_HOST_PORT=8443
POSTGRES_HOST_PORT=5432
OPENBAO_HOST_PORT=9443
```

Start the stack:

```bash
docker compose pull
docker compose up -d
```

The production compose stack starts Postgres, OpenBao, an idempotent OpenBao bootstrap job, the gateway, and an HTTPS edge proxy. Keycloak is expected to be an external production identity service; its issuer URL is written to OpenBao from `.env`.

## Keycloak And SIPS Connect

SIPS Connect calls this gateway with Keycloak bearer tokens. Keep environment URLs, client identifiers, and roles in private deployment configuration:

```text
Gateway base URL: private deployment value
Keycloak issuer:  P2G_KEYCLOAK_AUTHORITY
API audience:     P2G_KEYCLOAK_AUDIENCE
SIPS client id:   private deployment value
Required role:    P2G_KEYCLOAK_SIPS_CONNECT_ROLE
```

When this gateway calls SIPS Connect for outbound operations such as payment status checks, it uses:

```text
SIPS_CONNECT_CLIENT_ENABLED
SIPS_CONNECT_BASE_URL
SIPS_CONNECT_TOKEN_ENDPOINT
SIPS_CONNECT_CLIENT_ID
SIPS_CONNECT_CLIENT_SECRET
SIPS_CONNECT_SCOPE
SIPS_CONNECT_PAYMENT_STATUS_PATH
SIPS_CONNECT_REQUEST_TIMEOUT_SECONDS
SIPS_CONNECT_REQUIRE_HTTPS
SIPS_CONNECT_ALLOW_INVALID_SERVER_CERTIFICATE
```

The bootstrap job writes these values into OpenBao at `secret/sps-p2g-gateway/runtime` on each run.

## HTTPS Certificate

The `cert-init` service generates a self-signed certificate on startup when `certs/sps-p2g.crt` and `certs/sps-p2g.key` are missing. The certificate includes `HOST_IP` as an IP subject alternative name and uses `CERT_DNS_NAMES` for DNS subject alternative names.

You can also generate it manually:

```bash
HOST_IP=<deployment-host-or-interface> ./scripts/generate-self-signed-cert.sh
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

Sample localhost defaults:

```text
Gateway: https://127.0.0.1:8443
OpenBao: https://127.0.0.1:9443
Postgres: 127.0.0.1:5432
```

Override `HOST_IP`, `GATEWAY_HOST_PORT`, `OPENBAO_HOST_PORT`, and `POSTGRES_HOST_PORT` in `.env` when needed.

## Checks

```bash
docker compose ps
curl -k -fsS https://${HOST_IP:-127.0.0.1}:${GATEWAY_HOST_PORT:-8443}/health/ready
curl -k -fsS https://${HOST_IP:-127.0.0.1}:${OPENBAO_HOST_PORT:-9443}/v1/sys/health
docker compose logs --tail=120 gateway openbao-bootstrap edge-proxy
```
