# Government Payments Project Operations Runbook

This runbook covers the minimum production operations needed to keep the Government Payments Project healthy.

## First Checks

Use these checks before deeper investigation:

```bash
curl -fsS http://<gateway-host>/health/ready
curl -fsS http://<gateway-host>/metrics
docker compose ps
docker compose logs --tail=200 gateway
```

Readiness checks database connectivity, pending callback backlog, and dead-letter callback thresholds. Metrics expose bill, payment, callback, outbox, and reversal counters.

## OpenBao

OpenBao is the main runtime security and secrets service for this product. The project keeps OpenBao material under `security/openbao` and uses the root `docker-compose.yml`.

For production/UAT hosts that should run the published container image, use the `production/` bundle:

```bash
cd production
cp .env.example .env
docker compose pull
docker compose up -d
```

The production bundle pulls `ghcr.io/hanadderia/sps-p2g-gateway:${GATEWAY_IMAGE_TAG:-latest}` and stores generated runtime data under `production/openbao`, `production/postgres`, and `production/gateway`. The OpenBao bootstrap job writes runtime KV values from `production/.env` each time it runs, so treat `.env` as the source of truth for those seeded settings.

Default production host bindings:

```text
Gateway: https://10.128.200.244:8991
OpenBao: https://10.128.200.244:8992
Postgres: 10.128.200.244:5435
```

The production compose bundle includes an HTTPS edge proxy and a `cert-init` job that creates `production/certs/sps-p2g.crt` and `production/certs/sps-p2g.key` when they are missing. Replace the self-signed certificate with a CA-issued certificate when one is available.

Start the full stack:

```bash
docker compose up -d
```

The compose stack initializes/unseals OpenBao when needed, writes runtime configuration to KV, writes the gateway token to `security/openbao/runtime/gateway_token`, and starts the gateway after bootstrap succeeds.

Manual recovery commands:

```bash
make security-prepare
make security-up
make security-init
make security-unseal
make security-bootstrap
```

Check status:

```bash
docker compose exec openbao bao status
```

If OpenBao is sealed:

```bash
make security-unseal
```

If gateway startup fails while loading secrets:

```bash
docker compose logs --tail=200 openbao
docker compose exec openbao bao kv get secret/sps-p2g-gateway/runtime
```

Confirm the gateway has only OpenBao connection settings in environment and receives sensitive runtime values from KV:

```bash
docker compose exec gateway env | egrep 'OpenBao__|DataProtection__|Auth__|Keycloak__'
```

## Secret Rotation

Write the replacement secret to OpenBao KV:

```bash
export BAO_TOKEN="$(cat security/openbao/keys/root_token)"

docker compose exec -T openbao bao kv patch secret/sps-p2g-gateway/runtime \
  P2G__DescriptorHmacSecret='<new-secret>'
```

Restart gateway instances after the update:

```bash
docker compose up -d --force-recreate gateway
```

For Keycloak client secrets, rotate in Keycloak first, update the relevant authority/bank client secret used by the caller, then confirm tokens can still be issued before disabling the previous secret.

## Data Protection Keys

Data Protection keys must persist across gateway restarts and be shared by all gateway instances. Callback signing secrets depend on this.

Expected setting:

```bash
DataProtection__PersistKeysToFileSystemPath=/var/lib/sps-p2g-gateway/data-protection-keys
```

Expected local compose volume:

```bash
docker volume inspect p2g_p2g-data-protection-keys
```

If callback subscriptions cannot be unprotected after a restart, restore the previous Data Protection key-ring from backup before retrying callback delivery.

## Keycloak Client Onboarding

Revenue authorities use confidential clients with service accounts.

For each authority:

- Create a confidential OIDC client whose `clientId` equals `RevenueAuthority.Code`.
- Enable service accounts.
- Assign `p2g-authority`.
- Add an audience mapper for `sps-p2g-gateway`.
- Store the client secret in the authority-side integration secret store, not in this gateway database.

SIPS Connect uses one confidential client:

- `clientId`: `sips-connect-p2g`
- role: `p2g-sips-connect`
- audience: `sps-p2g-gateway`

Outbound calls from this gateway to SIPS Connect UAT are configured separately through the production `.env` `SIPS_CONNECT_*` variables. The OpenBao bootstrap maps them to the gateway `SipsConnectClient` configuration section:

```text
SipsConnectClient:Enabled
SipsConnectClient:BaseUrl
SipsConnectClient:TokenEndpoint
SipsConnectClient:ClientId
SipsConnectClient:ClientSecret
SipsConnectClient:Scope
SipsConnectClient:PaymentStatusPath
SipsConnectClient:RequestTimeoutSeconds
SipsConnectClient:RequireHttps
SipsConnectClient:AllowInvalidServerCertificate
```

Dashboard users receive realm roles:

- `p2g-dashboard`
- `p2g-configuration-admin` for users who can manage MDA/service settlement accounts

Verify client credentials:

```bash
curl -fsS https://<keycloak>/realms/sps/protocol/openid-connect/token \
  -d grant_type=client_credentials \
  -d client_id=<authority-code> \
  -d client_secret='<secret>'
```

## Callback Dead Letters

List dead-lettered callbacks:

```text
GET /api/v1/authorities/{authorityId}/callback-deliveries?status=DeadLetter
```

Common causes:

- target returned 4xx
- callback URL blocked by HTTPS/private-network policy
- subscription disabled or missing
- maximum retries reached after repeated 5xx or timeout

After correcting the cause, retry:

```text
POST /api/v1/authorities/{authorityId}/callback-deliveries/{deliveryId}/retry
```

For active-active gateway deployments, callback outbox rows are claimed before dispatch. If a worker dies mid-send, the claim expires and another instance can retry after `Callbacks:ClaimTimeoutSeconds`.

## Payment Incident Checks

For duplicate bank requests, the gateway should return `ACCP` for the same `txId` and payload after the first accepted payment. Different payloads for the same `txId` return `DUPLICATE_TX_DIFFERENT_PAYLOAD`.

Check payment state:

```text
POST /api/v1/sips-connect/payments/status
GET /api/v1/authorities/{authorityId}/payments?txId={txId}
GET /api/v1/authorities/{authorityId}/bill-events?cursor=0&billId={billId}&includePayload=true
```

Check Postgres if API access is unavailable:

```sql
select "TxId", "Status", "Amount", "Currency", "PaidAt"
from "BillPayments"
where "TxId" = '<tx-id>';

select "EventType", "Sequence", "CreatedAt"
from "BillEvents"
where "BillId" = '<bill-id>'
order by "Sequence";
```

## Migrations

Production can apply migrations on startup when enabled:

```bash
Database__RunMigrationsOnStartup=true
```

For manual deployment:

```bash
make db-script
make db-migrate
```

Review `artifacts/sql/migrations.sql` before applying manually in controlled environments.

## Go-Live Gates

- `make verify` passes.
- `docker compose config` passes.
- OpenBao is initialized, unsealed, audited, and backed up.
- Gateway starts with `Auth__AllowAuthorityApiKeyFallback=false`.
- Gateway starts with `Auth__AllowSipsConnectInternalKeyFallback=false`.
- Data Protection key-ring storage is persistent and backed up.
- Keycloak service-account tokens contain the `sps-p2g-gateway` audience.
- At least one callback success and one callback retry have been tested in staging.
