# SPS P2G Gateway

`Sps.P2G.Gateway` is the SPS-hosted Government Invoice Gateway for invoice-first P2G payments.

Every SIPS Connect transaction dispatched to this service is treated as P2G against a previously recorded bill. The sending bank is expected to include the required P2G tags/references in the verification and payment requests; the gateway does not branch into generic bank account receive-and-return processing.

## Project Layout

```text
src/
  Sps.P2G.Gateway/
tests/
  Sps.P2G.Gateway.Tests/
```

## Local Configuration

Start local dependencies:

```bash
docker compose up -d postgres keycloak
```

Or run the full local stack, including the gateway:

```bash
docker compose up --build
```

The gateway is exposed at:

```text
http://localhost:8081
http://localhost:8081/openapi/v1.json
http://localhost:8081/health/ready
```

Configure Postgres and service secrets with environment variables or `appsettings.json`:

```bash
export ConnectionStrings__P2G='Host=localhost;Database=sps_p2g_gateway;Username=postgres;Password=postgres'
export P2G__DescriptorHmacSecret='change-me'
export P2G__InternalApiKey='change-me-internal-key'
```

Apply the schema:

```bash
dotnet ef database update \
  --project src/Sps.P2G.Gateway/Sps.P2G.Gateway.csproj \
  --startup-project src/Sps.P2G.Gateway/Sps.P2G.Gateway.csproj
```

Or let the service apply migrations at startup:

```bash
export Database__RunMigrationsOnStartup=true
export Database__MigrationRetryCount=10
export Database__MigrationRetryDelaySeconds=3
```

Run the service:

```bash
dotnet run --project src/Sps.P2G.Gateway/Sps.P2G.Gateway.csproj
```

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
- `CallbackSecurity:RequireHttps=true`
- `CallbackSecurity:AllowPrivateNetworkTargets=false`

## Keycloak/OIDC Auth

The service is scaffolded for Keycloak JWT bearer auth.

Recommended realm model:

- Dashboard: human users authenticate with the OIDC authorization-code flow and receive `p2g-dashboard`.
- Revenue Authorities: one confidential client per authority, with service accounts enabled. The Keycloak `client_id` should match `RevenueAuthority.Code`, and the client should receive `p2g-authority`.
- SIPS Connect: one confidential client, default `sips-connect-p2g`, with `p2g-sips-connect`.

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

The local compose file imports a starter realm from `infra/keycloak/realm-sps.json` with:

- `sips-connect-p2g` confidential client, secret `change-me-sips-connect-secret`
- `SRA` confidential client, secret `change-me-sra-secret`
- `p2g-dashboard` public dashboard client
- `dashboard-admin` temporary password `admin`

Example client-credentials token request:

```bash
curl -s http://localhost:8080/realms/sps/protocol/openid-connect/token \
  -d grant_type=client_credentials \
  -d client_id=SRA \
  -d client_secret=change-me-sra-secret
```

## Core Flow

1. `POST /api/v1/authorities`
2. `POST /api/v1/authorities/{authorityId}/accounts`
3. `POST /api/v1/authorities/{authorityId}/bills`
4. `POST /api/v1/sips-connect/verify`
5. `POST /api/v1/sips-connect/payments/confirm`
6. `POST /api/v1/sips-connect/payments/status`
7. `POST /api/v1/sips-connect/payments/reverse`
8. `GET /api/v1/authorities/{authorityId}/bill-events?cursor=0`

Verification returns the compact P2G descriptor in `CB_VerificationResponse.name`. Payment confirmation validates the request against the recorded bill and rejects amount, currency, creditor account, and duplicate-payment mismatches.

SIPS Connect endpoints validate the P2G adapter contract before bill lookup or posting:

- verification requires an invoice/bill reference, sending agent, and verification ID
- payment confirmation requires `txId`, `endToEndId`, `fromBIC`, `toBIC`, `categoryPurpose=C2BBPT`, positive amount, currency, creditor account/name/account type, structured remittance, and `msgDefIdr=pacs.008`
- returns require original transaction ID, `endToEndId`, return ID, and reason
- status requests require `txId` or `endToEndId`

Malformed SIPS requests return normal adapter-shaped rejection payloads (`CB_VerificationResponse`, `CB_PaymentResponse`, `CB_ReturnResponse`, or `CB_PaymentStatusResponse`) instead of generic HTTP errors.

Event polling returns cursor-ordered events with actor/source, correlation ID, payload hash, previous hash, and event hash fields. Use `types`, `billId`, and `includePayload=true` when an authority needs a narrower reconciliation feed:

```text
GET /api/v1/authorities/{authorityId}/bill-events?cursor=0&types=BillPaid&billId={billId}&includePayload=true
```

For tabular finance reconciliation, authorities can list payments and reversals directly:

```text
GET /api/v1/authorities/{authorityId}/payments?from={isoDateTime}&to={isoDateTime}&upr={upr}&externalInvoiceId={invoiceId}&txId={txId}&status=ACSC
GET /api/v1/authorities/{authorityId}/reversals?from={isoDateTime}&to={isoDateTime}&upr={upr}&externalInvoiceId={invoiceId}&txId={txId}
GET /api/v1/authorities/{authorityId}/payments/export.csv?from={isoDateTime}&to={isoDateTime}&status=ACSC
GET /api/v1/authorities/{authorityId}/reversals/export.csv?from={isoDateTime}&to={isoDateTime}
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
