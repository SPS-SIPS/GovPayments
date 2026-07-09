# iTax Revenue Authority Integration Guide

This guide describes how iTax integrates with the SPS P2G Gateway as a Revenue Authority system.

It intentionally does not include request or response payload examples. Use the generated OpenAPI file for exact schemas, required fields, response structures, and validation details:

```text
artifacts/openapi/openapi.yaml
```

Generate it locally with:

```bash
make openapi
```

## Integration Scope

iTax is responsible for creating and managing recorded government invoices. SPS P2G Gateway is responsible for making those invoices payable through SIPS Connect, recording payment outcomes, exposing lifecycle events, delivering callbacks, and supporting reconciliation.

Every payable transaction in this integration is invoice-first P2G. A bank payment must resolve to a bill that iTax already created in the gateway.

## Base URLs

Use the environment-specific gateway base URL provided by SPS.

Local development defaults:

```text
http://localhost:8081
```

All Revenue Authority endpoints are under:

```text
/api/v1/authorities/{authorityId}
```

## Authentication

Production integrations should use Keycloak client credentials.

Each Revenue Authority should have a Keycloak confidential client. The client identity must map to the authority code configured in the gateway.

Send the token on every Revenue Authority API call:

```text
Authorization: Bearer {access-token}
```

During transition or local development, API-key fallback may be enabled:

```text
X-Authority-Api-Key: {authority-api-key}
```

All calls may include a correlation ID:

```text
X-Correlation-Id: {itax-trace-id}
```

The gateway echoes this value and records it on bill lifecycle events, which makes it useful for support and reconciliation.

## Onboarding Requirements

Before iTax can create invoices, SPS must provision:

- Revenue Authority record and `authorityId`.
- Revenue Authority code, such as `SRA`.
- Keycloak client credentials, or temporary API key if fallback is enabled.
- One or more active MDA/service revenue-account configurations.
- Callback target URL if iTax wants push-based event delivery.

Revenue account setup is required because every bill resolves to a settlement account by `mdaCode`, `serviceCode`, and `currency`, or by explicit `revenueAccountId`.

Relevant setup endpoints:

```text
POST /api/v1/authorities
POST /api/v1/dashboard/authorities/{authorityId}/revenue-accounts
```

In production, authority creation and MDA/service revenue-account configuration are portal/dashboard operations. Configuration writes require the `p2g-configuration-admin` role. Normal iTax service integration starts after the authority and account configurations exist.

## MDA And Service Configuration

Every invoice must reference a valid configured MDA/service/currency combination. That configuration controls which settlement account the citizen payment is attached to.

iTax should consume the configuration catalog before creating invoices:

```text
GET /api/v1/authorities/{authorityId}/accounts
GET /api/v1/authorities/{authorityId}/accounts/{accountId}
```

Useful filters:

```text
mdaCode
serviceCode
currency
status
page
pageSize
```

Configuration records include the MDA code/name, service code/name, revenue code, currency, settlement account details, default marker, and status.

Only portal users with the configuration-admin role should create or update these records:

```text
GET /api/v1/dashboard/authorities/{authorityId}/revenue-accounts
POST /api/v1/dashboard/authorities/{authorityId}/revenue-accounts
PATCH /api/v1/dashboard/authorities/{authorityId}/revenue-accounts/{accountId}
```

Operational rules:

- Invoice creation fails when the MDA/service/currency combination has no active default account.
- If iTax sends an explicit `revenueAccountId`, that account must belong to the authority, be active, and match the invoice currency.
- One default account should exist per payable MDA/service/currency combination.
- Disabling a configuration prevents new invoices from using that account.

## Invoice Creation

Create one invoice:

```text
POST /api/v1/authorities/{authorityId}/bills
```

Create many invoices:

```text
POST /api/v1/authorities/{authorityId}/bills/bulk
```

Important invoice creation rules:

- `externalInvoiceId` must be unique per authority.
- `upr` must be unique per authority.
- Currency is currently limited to `USD` and `SOS`.
- Amount must be greater than zero.
- `expiresAt` must be in the future.
- A matching active MDA/service/currency revenue-account configuration must exist.
- Repeating the same `externalInvoiceId` with the same invoice data is idempotent.
- Reusing the same `externalInvoiceId` with different data is rejected.

After creation, the invoice is available for SIPS Connect verification and payment.

## Pulling Invoices

iTax can pull invoices for lookup, customer service, or reconciliation.

List invoices:

```text
GET /api/v1/authorities/{authorityId}/bills
```

Useful filters:

```text
upr
externalInvoiceId
taxpayerReference
mdaCode
serviceCode
status
page
pageSize
```

Get one invoice by gateway bill ID:

```text
GET /api/v1/authorities/{authorityId}/bills/{billId}
```

The OpenAPI document defines the exact query parameter formats and response fields.

## Cancelling Invoices

Cancel an invoice:

```text
POST /api/v1/authorities/{authorityId}/bills/{billId}/cancel
```

Cancellation is allowed only while the bill is still unpaid. The gateway currently allows cancellation from:

- `Available`
- `Fetched`
- `PaymentInitiated`

Paid bills cannot be cancelled. If a paid transaction must be undone, the lifecycle goes through the SIPS return/reversal path.

## Voiding Invoices

Void an invoice before citizen payment:

```text
POST /api/v1/authorities/{authorityId}/bills/{billId}/void
```

Voiding requires a human-readable description explaining why the bill or invoice is no longer valid. The gateway allows voiding only while the bill is still unpaid:

- `Available`
- `Fetched`
- `PaymentInitiated`

After a bill is voided, SIPS Connect verification and payment confirmation reject it with `VOIDED`. Use cancellation for ordinary authority-side cancellation, and use voiding when the invoice itself must be invalidated with an explicit operational reason.

## Invoice Lifecycle

Core bill statuses:

| Status | Meaning |
| --- | --- |
| `Available` | Invoice was created and can be paid. |
| `Fetched` | SIPS/bank verification found the invoice. |
| `PaymentInitiated` | A payment attempt has started. |
| `Paid` | Payment was accepted and recorded. |
| `Cancelled` | iTax cancelled the unpaid invoice. |
| `Voided` | iTax voided the unpaid invoice with a required reason. |
| `Reversed` | The accepted payment was reversed. |
| `PartiallyReversed` | A partial reversal state is reserved for future use. |
| `Expired` | Invoice is no longer payable after expiry. |
| `Failed` | A failure state is reserved for exceptional processing. |

Lifecycle events recorded by the gateway:

| Event | Typical cause |
| --- | --- |
| `BillCreated` | iTax created the invoice. |
| `BillFetched` | SIPS/bank verification found the invoice. |
| `VerificationReturned` | Verification result was returned. |
| `PaymentAccepted` | SIPS payment confirmation was accepted. |
| `PaymentRejected` | SIPS payment confirmation was rejected. |
| `BillPaid` | Bill status moved to paid. |
| `BillCancelled` | iTax cancelled the bill. |
| `BillVoided` | iTax voided the bill and recorded the void description. |
| `BillReversed` | Payment return/reversal was accepted. |
| `CallbackQueued` | Gateway queued a callback delivery for iTax. |

The normal happy path is:

```text
Available -> Fetched -> PaymentInitiated -> Paid
```

The normal cancellation path is:

```text
Available/Fetched/PaymentInitiated -> Cancelled
```

The normal void path is:

```text
Available/Fetched/PaymentInitiated -> Voided
```

The normal return path is:

```text
Paid -> Reversed
```

## Pull-Based Event Subscription

iTax can poll bill events using a cursor.

```text
GET /api/v1/authorities/{authorityId}/bill-events?cursor=0
```

The response includes the next cursor. iTax should persist that cursor and use it on the next poll.

Optional filters:

```text
types
billId
includePayload
```

Recommended polling behavior:

- Start with `cursor=0` during initial integration testing.
- Persist `nextCursor` after each successful poll.
- Poll frequently enough for iTax operational needs.
- Use `types` when only specific lifecycle events matter.
- Keep `includePayload=false` unless support or audit workflows need the original event details.

Event records include hashes and previous hashes so iTax can audit event-chain integrity without relying only on mutable status fields.

## Callback-Based Event Subscription

iTax can receive push notifications by creating callback subscriptions.

Create subscription:

```text
POST /api/v1/authorities/{authorityId}/callback-subscriptions
```

List subscriptions:

```text
GET /api/v1/authorities/{authorityId}/callback-subscriptions
```

Update subscription:

```text
PATCH /api/v1/authorities/{authorityId}/callback-subscriptions/{subscriptionId}
```

Rotate callback secret:

```text
POST /api/v1/authorities/{authorityId}/callback-subscriptions/{subscriptionId}/rotate-secret
```

Callback delivery security:

- The callback URL must be HTTP(S), and production defaults require HTTPS.
- Localhost and private-network IP callback targets are rejected by default.
- The gateway signs every callback.
- iTax should verify the signature before trusting the event.
- The callback secret is returned only when the subscription is created or rotated.

Callback headers sent by SPS:

```text
X-SPS-Event-Id
X-SPS-Timestamp
X-SPS-Signature
X-SPS-Idempotency-Key
```

iTax should treat `X-SPS-Idempotency-Key` as the callback de-duplication key.

Callback delivery behavior:

- `2xx` response marks the callback delivered.
- `5xx`, timeout, or network failure retries with backoff.
- `4xx` marks the callback as dead-lettered.

## Callback Operations

List callback deliveries:

```text
GET /api/v1/authorities/{authorityId}/callback-deliveries
```

Useful filters:

```text
subscriptionId
eventId
status
page
pageSize
```

Retry a delivery:

```text
POST /api/v1/authorities/{authorityId}/callback-deliveries/{deliveryId}/retry
```

Retrying resets the delivery to `Pending`, resets the attempt counter, and schedules it for immediate worker pickup.

## Reconciliation

iTax can reconcile payments and reversals through API lists or CSV exports.

Reconciliation can be requested for a full authority, for a specific MDA/service, or for a specific configured revenue account. CSV exports include MDA/service and revenue-account columns so finance can split files by the account that received the citizen payment.

List payments:

```text
GET /api/v1/authorities/{authorityId}/payments
```

Export payments:

```text
GET /api/v1/authorities/{authorityId}/payments/export.csv
```

List reversals:

```text
GET /api/v1/authorities/{authorityId}/reversals
```

Export reversals:

```text
GET /api/v1/authorities/{authorityId}/reversals/export.csv
```

Useful payment filters:

```text
from
to
upr
externalInvoiceId
txId
status
mdaCode
serviceCode
revenueAccountId
accountNumber
page
pageSize
```

Useful reversal filters:

```text
from
to
upr
externalInvoiceId
txId
mdaCode
serviceCode
revenueAccountId
accountNumber
page
pageSize
```

CSV exports are intended for finance operations and settlement review. The API list endpoints are better for system-to-system incremental reconciliation.

## Dashboard Endpoints

Dashboard endpoints are intended for human operational users and require the dashboard role when Keycloak is enabled.

List bills across authorities:

```text
GET /api/v1/dashboard/bills
```

Useful filters:

```text
upr
externalInvoiceId
taxpayerReference
mdaCode
serviceCode
status
page
pageSize
```

Summary:

```text
GET /api/v1/dashboard/summary
```

Callback delivery view:

```text
GET /api/v1/dashboard/callbacks
```

Useful filters:

```text
status
page
pageSize
```

Operational health snapshot:

```text
GET /api/v1/dashboard/operations
```

The operations endpoint summarizes callback subscriptions and delivery states, outbox backlog, overdue work, accepted/rejected/reversed payments over the last 24 hours, top rejection reasons, and recent bill state totals.

## Health And Monitoring

Liveness:

```text
GET /health/live
```

Readiness:

```text
GET /health/ready
```

Prometheus metrics:

```text
GET /metrics
```

Use readiness for deployment routing. It checks database connectivity, pending outbox backlog, and dead-letter callback thresholds.

## SIPS Connect Flow Visibility

iTax does not call SIPS Connect endpoints directly. Banks and SIPS Connect use them to verify and pay recorded invoices:

```text
POST /api/v1/sips-connect/verify
POST /api/v1/sips-connect/payments/confirm
POST /api/v1/sips-connect/payments/status
POST /api/v1/sips-connect/payments/reverse
```

iTax observes the outcome through:

- Invoice status.
- Bill events.
- Callback deliveries.
- Payment and reversal reconciliation endpoints.
- Dashboard operational views.

## Recommended iTax Integration Flow

1. Receive SPS-provided `authorityId` and production credentials.
2. Confirm revenue-account configuration for every payable MDA/service/currency combination.
3. Generate or receive invoice data inside iTax.
4. Create the invoice in the SPS P2G Gateway.
5. Store the returned gateway bill ID alongside the iTax invoice record.
6. Expose the UPR or invoice reference to the payer.
7. Poll bill events or configure callbacks for lifecycle updates.
8. Mark invoice paid in iTax after `BillPaid` or payment reconciliation confirms the payment.
9. Handle reversals through `BillReversed` events or reversal reconciliation.
10. Use dashboard endpoints for support, operations, and exception handling.

## Go-Live Checklist

- iTax has production Keycloak credentials.
- API-key fallback is disabled for production unless SPS explicitly approves transition mode.
- All MDA/service/currency revenue-account configurations are created in the portal and verified by iTax.
- Invoice references and UPR generation rules are agreed with SPS.
- Callback endpoint is public, HTTPS, stable, and signature-verifying.
- iTax persists event cursors if using polling.
- iTax de-duplicates callbacks using `X-SPS-Idempotency-Key`.
- Finance team has access to payment and reversal reconciliation.
- Operations team has dashboard access and alerting on readiness/metrics.
- Test payments, duplicate payments, rejected payments, cancellation, voiding, callback retry, and reversal flows have been rehearsed in staging.
