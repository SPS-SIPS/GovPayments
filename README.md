# Government Payments Project

`Sps.P2G.Gateway` is the SPS-hosted Government Payments Project gateway for invoice-first government payments.

Every SIPS Connect transaction dispatched to this service is treated as P2G against a previously recorded bill. The sending bank is expected to include the required P2G tags/references in the verification and payment requests; the gateway does not branch into generic bank account receive-and-return processing.

## Project Layout

```text
src/
  Sps.P2G.Gateway/
tests/
  Sps.P2G.Gateway.Tests/
docs/
  itax-revenue-authority-integration.md
  operations-runbook.md
security/
  production.env.example
  openbao/
production/
  docker-compose.yml
  .env.example
  openbao/
  postgres/
  gateway/
```

The iTax Revenue Authority integration guide is available at `docs/itax-revenue-authority-integration.md`.
The production operations runbook is available at `docs/operations-runbook.md`.

## Local Configuration

Most local workflows are available through `make`:

```bash
make help
make restore
make build
make test
make verify
make run
make deps-up
make stack-up
make security-up
make openapi
```

Start local dependencies:

```bash
make deps-up
```

Or run the full local stack, including the gateway:

```bash
docker compose up -d
```

The full compose stack starts Postgres, Keycloak, OpenBao, the OpenBao bootstrap job, and the gateway. The bootstrap job initializes/unseals OpenBao when needed, writes runtime configuration to KV, and writes a gateway token under `security/openbao/runtime`.

The gateway is exposed at:

```text
http://localhost:18081
http://localhost:18081/openapi/v1.json
http://localhost:18081/health/ready
```

Override the gateway binding with `GATEWAY_HOST_PORT=8081 docker compose up -d` when that port is available and preferred.

OpenBao is exposed to the host at `http://localhost:8201` by default for local operator access. Override it with `OPENBAO_HOST_PORT=8200 docker compose up -d` if that port is available and preferred.

Configure Postgres and service secrets with environment variables or `appsettings.json`:

```bash
export ConnectionStrings__P2G='Host=localhost;Database=sps_p2g_gateway;Username=postgres;Password=postgres'
export P2G__DescriptorHmacSecret='change-me'
export P2G__InternalApiKey='change-me-internal-key'
```

Apply the schema:

```bash
make db-migrate
```

Or let the service apply migrations at startup:

```bash
export Database__RunMigrationsOnStartup=true
export Database__MigrationRetryCount=10
export Database__MigrationRetryDelaySeconds=3
```

Run the service:

```bash
make run
```

Useful migration commands:

```bash
make db-migration-add MIGRATION_NAME=AddNewIndex
make db-migration-list
make db-migration-remove
make db-script
make db-drop
```

Generate OpenAPI artifacts:

```bash
make openapi
```

The generator writes `artifacts/openapi/openapi.json` and `artifacts/openapi/openapi.yaml`.

## OpenBao Secrets

Production deployments load runtime secrets from OpenBao KV v2 before startup validation runs. The OpenBao security bundle lives under `security/openbao` and is adapted from the Helios OpenBao pattern with Government Payments Project service names.

The local/UAT compose stack bootstraps OpenBao automatically:

```bash
docker compose up -d
```

Manual OpenBao commands remain available for recovery and administration:

```bash
make security-prepare
make security-up
make security-init
make security-unseal
make security-bootstrap
```

Store runtime values at `secret/sps-p2g-gateway/runtime` using flat ASP.NET configuration keys:

```bash
ConnectionStrings__P2G
P2G__DescriptorHmacSecret
P2G__InternalApiKey
Keycloak__Authority
Keycloak__Audience
Keycloak__RequireHttpsMetadata
```

At runtime, the gateway only needs enough environment configuration to reach OpenBao:

```bash
OpenBao__Enabled=true
OpenBao__BaseUrl=http://openbao:8200
OpenBao__TokenFile=/run/openbao/gateway_token
OpenBao__KvMount=secret
OpenBao__SecretPath=sps-p2g-gateway/runtime
```

See `security/openbao/README.md` and `security/production.env.example` for the complete deployment shape.

## Production Compose

The `production/` directory contains the host deployment bundle that consumes the published GitHub Container Registry image:

```bash
cd production
cp .env.example .env
docker compose pull
docker compose up -d
```

The gateway image defaults to `ghcr.io/hanadderia/sps-p2g-gateway:latest`. Set `GATEWAY_IMAGE_TAG` in `production/.env` to deploy a pinned commit SHA. See `production/README.md` for required secrets, directories, and runtime checks.

Default production host bindings use HTTPS for the public services:

```text
Gateway: https://10.128.200.244:8991
OpenBao: https://10.128.200.244:8992
Postgres: 10.128.200.244:5435
```

SIPS Connect UAT should call the gateway at `https://10.128.200.244:8991/api/v1/sips-connect/*` with a Keycloak token for `P2G_KEYCLOAK_SIPS_CONNECT_CLIENT_ID`. Outbound SIPS Connect status-check settings live in the production `.env` as `SIPS_CONNECT_*` variables and are loaded into OpenBao by the bootstrap job.

## Auth Headers

Revenue Authority APIs currently accept API-key fallback:

```text
X-Authority-Api-Key: {api-key-returned-at-authority-creation}
```

SIPS Connect callback APIs use:

```text
X-Internal-Api-Key: {P2G__InternalApiKey}
```

All APIs accept an optional correlation header:

```text
X-Correlation-Id: {caller-trace-id}
```

The gateway echoes the value on the response. If omitted, the gateway generates one. Bill creation stores the correlation ID on the `BillCreated` event for reconciliation across caller logs, service logs, and event polling.

## Production Guardrails

Outside development, testing, and integration-testing environments the service fails startup when unsafe defaults are present. Production requires:

- `ConnectionStrings:P2G`
- non-default `P2G:DescriptorHmacSecret` of at least 32 characters
- `Keycloak:Enabled=true`
- `Keycloak:Authority` or `Keycloak:MetadataAddress`
- `Keycloak:RequireHttpsMetadata=true`
- `Auth:AllowAuthorityApiKeyFallback=false`
- `Auth:AllowSipsConnectInternalKeyFallback=false`
- `OpenBao:Enabled=true`
- `DataProtection:PersistKeysToFileSystemPath` points to persistent shared storage
- `CallbackSecurity:RequireHttps=true`
- `CallbackSecurity:AllowPrivateNetworkTargets=false`

## Keycloak/OIDC Auth

The service is scaffolded for Keycloak JWT bearer auth.

Recommended realm model:

- Dashboard: human users authenticate with the OIDC authorization-code flow and receive `p2g-dashboard`.
- Configuration admins: portal users who create MDA/service revenue-account mappings receive `p2g-configuration-admin`.
- Revenue Authorities: one confidential client per authority, with service accounts enabled. The Keycloak `client_id` should match `RevenueAuthority.Code`, and the client should receive `p2g-authority`.
- SIPS Connect: one confidential client, default `sips-connect-p2g`, with `p2g-sips-connect`.
- Service-account and dashboard tokens must include the API audience `sps-p2g-gateway`. The sample realm includes explicit audience mappers for the starter clients.

Enable JWT validation:

```bash
export Keycloak__Enabled=true
export Keycloak__Authority='http://localhost:8080/realms/sps'
export Keycloak__Audience='sps-p2g-gateway'
export Keycloak__RequireHttpsMetadata=false
```

For automated tests or isolated development without Keycloak discovery, the app also supports explicit issuer/signing-key validation:

```bash
export Keycloak__Authority=''
export Keycloak__ValidIssuer='https://issuer.test/realms/sps'
export Keycloak__SymmetricSigningKey='replace-with-at-least-32-bytes'
```

Use the explicit signing-key path only for tests/local development; real deployments should use Keycloak discovery/JWKS.

Machine clients should use Keycloak client credentials to obtain a bearer token and call APIs with:

```text
Authorization: Bearer {access-token}
```

During transition, the existing API-key and internal-key headers remain supported.

To require Keycloak bearer tokens and disable fallback secrets:

```bash
export Keycloak__Enabled=true
export Auth__AllowAuthorityApiKeyFallback=false
export Auth__AllowSipsConnectInternalKeyFallback=false
```

In strict mode:

- Authority APIs require a bearer token with `p2g-authority`; token `azp` / `client_id` must match `RevenueAuthority.Code`. Client roles are accepted only from the calling authority client; realm role assignment is also supported.
- SIPS Connect APIs require the configured `sips-connect-p2g` client and `p2g-sips-connect`.
- Dashboard APIs require `p2g-dashboard`.
- MDA/service revenue-account configuration writes require `p2g-configuration-admin`.

The local compose file imports a starter realm from `infra/keycloak/realm-sps.json` with:

- `sips-connect-p2g` confidential client, secret `change-me-sips-connect-secret`
- `SRA` confidential client, secret `change-me-sra-secret`
- `p2g-dashboard` public dashboard client
- `dashboard-admin` temporary password `admin`, with dashboard and configuration-admin roles

Example client-credentials token request:

```bash
curl -s http://localhost:8080/realms/sps/protocol/openid-connect/token \
  -d grant_type=client_credentials \
  -d client_id=SRA \
  -d client_secret=change-me-sra-secret
```

## Core Flow

1. `POST /api/v1/authorities`
2. `POST /api/v1/dashboard/authorities/{authorityId}/revenue-accounts`
3. `POST /api/v1/authorities/{authorityId}/bills`
4. `POST /api/v1/authorities/{authorityId}/bills/{billId}/void`
5. `POST /api/v1/sips-connect/verify`
6. `POST /api/v1/sips-connect/payments/confirm`
7. `POST /api/v1/sips-connect/payments/status`
8. `POST /api/v1/sips-connect/payments/reverse`
9. `GET /api/v1/authorities/{authorityId}/bill-events?cursor=0`

Verification returns the compact P2G descriptor in `CB_VerificationResponse.name` and, for invoice-first lookups, normalized bill-payment fields such as `invoiceId`, `upr`, `billReference`, `mda`, `mdaCode`, `amountPayable`, `creditorAccount`, `creditorName`, `creditorAccountType`, `amountLocked`, and `creditorLocked`. Payment confirmation validates the request against the recorded bill and rejects amount, currency, creditor account, and duplicate-payment mismatches. It also accepts the normalized aliases `billReference` and `amountPayable` when SIPS Connect forwards the payment payload produced from verification.

Authorities can void an unpaid bill before citizen payment by calling the void endpoint with a required description. Voided bills are no longer returned as payable by SIPS Connect verification, and payment confirmation rejects them with `VOIDED`.

Invoices are only payable when their `mdaCode`, `serviceCode`, and `currency` match an active default revenue-account configuration. Revenue Authority integrations can read the configured combinations before creating invoices:

```text
GET /api/v1/authorities/{authorityId}/accounts
GET /api/v1/authorities/{authorityId}/accounts/{accountId}
```

Portal users with `p2g-configuration-admin` manage those configurations:

```text
GET /api/v1/dashboard/authorities/{authorityId}/revenue-accounts
POST /api/v1/dashboard/authorities/{authorityId}/revenue-accounts
PATCH /api/v1/dashboard/authorities/{authorityId}/revenue-accounts/{accountId}
```

SIPS Connect endpoints validate the P2G adapter contract before bill lookup or posting:

- verification requires an invoice/bill reference, sending agent, and verification ID
- payment confirmation requires `txId`, `endToEndId`, `fromBIC`, `toBIC`, B2G `categoryPurpose` (`C2GCRT`, `C2GSQR`, or `C2GDQR`), positive amount, currency, creditor account/name/account type, structured remittance, and `msgDefIdr=pacs.008`
- returns require original transaction ID, `endToEndId`, return ID, and reason
- status requests require `txId` or `endToEndId`

Malformed SIPS requests return normal adapter-shaped rejection payloads (`CB_VerificationResponse`, `CB_PaymentResponse`, `CB_ReturnResponse`, or `CB_PaymentStatusResponse`) instead of generic HTTP errors.

Event polling returns cursor-ordered events with actor/source, correlation ID, payload hash, previous hash, and event hash fields. Use `types`, `billId`, and `includePayload=true` when an authority needs a narrower reconciliation feed:

```text
GET /api/v1/authorities/{authorityId}/bill-events?cursor=0&types=BillPaid&billId={billId}&includePayload=true
```

For tabular finance reconciliation, authorities can list payments and reversals directly:

```text
GET /api/v1/authorities/{authorityId}/payments?from={isoDateTime}&to={isoDateTime}&upr={upr}&externalInvoiceId={invoiceId}&txId={txId}&status=ACSC&mdaCode={mdaCode}&serviceCode={serviceCode}&accountNumber={accountNumber}
GET /api/v1/authorities/{authorityId}/reversals?from={isoDateTime}&to={isoDateTime}&upr={upr}&externalInvoiceId={invoiceId}&txId={txId}&mdaCode={mdaCode}&serviceCode={serviceCode}&accountNumber={accountNumber}
GET /api/v1/authorities/{authorityId}/payments/export.csv?from={isoDateTime}&to={isoDateTime}&status=ACSC&mdaCode={mdaCode}&serviceCode={serviceCode}&accountNumber={accountNumber}
GET /api/v1/authorities/{authorityId}/reversals/export.csv?from={isoDateTime}&to={isoDateTime}&mdaCode={mdaCode}&serviceCode={serviceCode}&accountNumber={accountNumber}
```

Idempotency is persisted for:

- bill creation: `authorityId + externalInvoiceId`
- payment confirmation: `txId`
- reversal: `originalTxId + endToEndId + returnId`

Retries with the same payload return the existing result. Reuse of the same idempotency key with a different payload is rejected.

## Callback Delivery

Callbacks are subscription-based and outbox-backed. Subscription creation returns the callback signing secret once. The stored secret is protected with ASP.NET Core Data Protection, then unprotected only by the background dispatch worker when signing deliveries.

Callback delivery headers:

```text
X-SPS-Event-Id
X-SPS-Timestamp
X-SPS-Signature
X-SPS-Idempotency-Key
```

Signature input:

```text
timestamp + "." + eventId + "." + payloadJson
```

Delivery behavior:

- `2xx` marks the callback and outbox message `Delivered`.
- `5xx`, timeout, and network errors retry with exponential backoff until `Callbacks:MaxAttempts`.
- `4xx` marks the callback and outbox message `DeadLetter`.

Callback admin controls:

```text
GET /api/v1/authorities/{authorityId}/callback-subscriptions
PATCH /api/v1/authorities/{authorityId}/callback-subscriptions/{subscriptionId}
POST /api/v1/authorities/{authorityId}/callback-subscriptions/{subscriptionId}/rotate-secret
GET /api/v1/authorities/{authorityId}/callback-deliveries?status=DeadLetter
POST /api/v1/authorities/{authorityId}/callback-deliveries/{deliveryId}/retry
```

Retrying a callback delivery resets it to `Pending`, resets the attempt counter, and schedules it for immediate worker pickup. Rotating a callback secret returns the new secret once.

## Health

```text
GET /health/live
GET /health/ready
```

`/health/live` reports process liveness. `/health/ready` checks database connectivity, pending outbox backlog, and dead-letter callback count.

Readiness thresholds:

```bash
export Health__MaxPendingOutboxMessages=10000
export Health__MaxDeadLetterCallbacks=100
```

## Operations

Every request is logged with method, path, status code, elapsed time, and the active `X-Correlation-Id` logging scope.

Partner-facing endpoint groups are rate limited with fixed windows. Defaults:

```bash
export RateLimits__Enabled=true
export RateLimits__AuthorityPermitLimit=300
export RateLimits__SipsConnectPermitLimit=600
export RateLimits__DashboardPermitLimit=120
export RateLimits__WindowSeconds=60
export RateLimits__QueueLimit=0
```

Requests are protected by a configurable body-size limit and baseline security headers:

```bash
export RequestProtection__Enabled=true
export RequestProtection__MaxRequestBodyBytes=1048576
```

Oversized requests return `413` with `payload_too_large`. Responses include `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, and `X-Permitted-Cross-Domain-Policies: none`.

Dashboard users can inspect callback and payment operational health:

```text
GET /api/v1/dashboard/operations
```

The response includes callback subscription/delivery counts, outbox pending/dead-letter/overdue counts, accepted/rejected/reversed payment counts for the last 24 hours, top rejection reasons, and recent bill state totals.

Callback URLs are validated before subscription and again before dispatch. Defaults require HTTPS and reject localhost or private-network IP targets:

```bash
export CallbackSecurity__RequireHttps=true
export CallbackSecurity__AllowPrivateNetworkTargets=false
```

Metrics are exposed in Prometheus text format:

```text
GET /metrics
```

## OpenAPI

```text
GET /openapi/v1.json
```

The generated OpenAPI document includes the Revenue Authority, bill, SIPS Connect, dashboard, and health endpoints.

## Verify

```bash
dotnet test Sps.P2G.sln
```

The test suite includes a PostgreSQL Testcontainers integration test. Docker must be running for the full suite.
