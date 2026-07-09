# Bank Receive-and-Return Implementation Guide

This document describes the minimum integration profile for a bank that receives incoming payments and can only send payment returns. The bank does not initiate ordinary outbound payments.

The field names and response bodies are derived from `jsonAdapter.node-a.json`.

## Scope

The bank must implement:

- Incoming verification requests
- Incoming payment transactions
- Incoming payment status requests
- Outgoing payment status requests
- Outgoing payment returns

The bank should not expose or enable ordinary outbound payment initiation for this profile.

## P2G Invoice Gateway Overlay

This same receive-and-return profile can host the SPS Government Invoice Gateway integration without changing BPC/SVIP message contracts.

The gateway is an invoice-first P2G service. Revenue Authorities and MDAs pre-create bills in SPS, citizens verify an invoice ID, bill ID, or UPR through the existing SIPS Connect verification flow, and bank-side middleware uses the verified response to build the normal payment instruction. Do not introduce a separate BPC bill enquiry API for this flow.

Use the existing messages:

| P2G step | SIPS Connect message | Adapter object | Notes |
| --- | --- | --- | --- |
| Invoice verification | `acmt.023/acmt.024` | `CB_VerificationRequest` / `CB_VerificationResponse` | `accountNo` carries invoice ID, bill ID, UPR, or QR-derived reference. |
| Payment execution | `pacs.008/pacs.002` | `CB_PaymentRequest` / `CB_PaymentResponse` | Payment must use the verified invoice amount, currency, and creditor account. |
| Payment status | `pacs.028/pacs.002` | `CB_StatusRequest` / `CB_PaymentStatusResponse` | Status response is built from the stored gateway payment record. |
| Return/reversal | `pacs.004/pacs.002` | `ReturnRequest` / `CB_ReturnResponse` | Returns are allowed only for previously received/paid gateway transactions. |

The gateway service can expose internal endpoints such as:

```text
POST /api/v1/sips-connect/verify
POST /api/v1/sips-connect/payments/confirm
POST /api/v1/sips-connect/payments/reverse
```

SIPS Connect should still see and emit the normal adapter payloads defined in `jsonAdapter.node-a.json`.

## Message Direction

| Capability | Direction | Adapter section used |
| --- | --- | --- |
| Incoming verification | SIPS to Bank | `CB_VerificationRequest` |
| Incoming verification response | Bank to SIPS | `CB_VerificationResponse` |
| Incoming transaction | SIPS to Bank | `CB_PaymentRequest` |
| Incoming transaction response | Bank to SIPS | `CB_PaymentResponse` |
| Incoming status request | SIPS to Bank | `CB_StatusRequest` |
| Incoming status response | Bank to SIPS | `CB_PaymentStatusResponse` |
| Outgoing status request | Bank to SIPS | `StatusRequest` |
| Outgoing return request | Bank to SIPS | `ReturnRequest` |
| Return response | SIPS to Bank | `CB_ReturnResponse` |

## General Response Mechanism

Each incoming request must be acknowledged with a mapped response object from the same business flow.

- Verification requests return a verification decision.
- Payment requests return acceptance, rejection, or processing status.
- Status requests return the current transaction state and original payment details.
- Return requests receive a return processing response from SIPS.

All date-time fields should be sent in UTC using ISO-8601 format.

## 1. Incoming Verification

SIPS sends a verification request to confirm whether an account can receive funds.

### Request: `CB_VerificationRequest`

```json
{
  "accountNo": "1234567890",
  "accountType": "BANK",
  "agent": "BANKBICXXXX",
  "verificationId": "VER-20260708-000001"
}
```

| Field | Type | Description |
| --- | --- | --- |
| `accountNo` | string | Account or alias to verify. |
| `accountType` | string | Account type supplied by SIPS. |
| `agent` | string | Sending agent/BIC. |
| `verificationId` | string | Verification reference. |

### Response: `CB_VerificationResponse`

```json
{
  "isVerified": true,
  "message": "Account verified",
  "accountNo": "1234567890",
  "accountType": "BANK",
  "name": "Customer Name",
  "address": "Customer Address",
  "currency": "SOS"
}
```

If the account cannot receive funds, return `isVerified: false` and provide a clear `message`.

### P2G Verification Mapping

For invoice-first P2G, treat `accountNo` as the invoice lookup key. It may contain:

- UPR
- Revenue Authority invoice ID
- Gateway bill ID
- QR-derived bill reference

The gateway must find an active, unpaid, unexpired bill and return a compact descriptor in the response `name` field. The descriptor is intended for bank-side middleware, not for manual customer entry.

Successful P2G verification response:

```json
{
  "isVerified": true,
  "message": "Invoice verified",
  "accountNo": "1234567890",
  "accountType": "ACCT",
  "name": "P2G1|SRA|TAX|INV123456|USD|125.00|20260731|TIN8891|K7F2",
  "address": "Somali Revenue Authority",
  "currency": "USD"
}
```

Response field usage:

| `CB_VerificationResponse` field | P2G meaning |
| --- | --- |
| `isVerified` | `true` only when the bill is payable. |
| `message` | Short reason such as `Invoice verified`, `NOT_FOUND`, `EXPIRED`, `CANCELLED`, `ALREADY_PAID`, or `NOT_PAYABLE`. |
| `accountNo` | Creditor/revenue account to use in the later `pacs.008`. |
| `accountType` | Creditor clearing type, for example `ACCT`. |
| `name` | Compact invoice descriptor for bank-side decoding. |
| `address` | Revenue Authority, MDA, or account display name. |
| `currency` | Verified bill currency. |

Descriptor format:

```text
P2G1|{MDA}|{SVC}|{INV}|{CCY}|{AMT}|{YYYYMMDD}|{PAYERREF}|{MAC}
```

Descriptor rules:

- Keep the descriptor at or below 140 characters.
- Use a pipe-delimited positional format, not JSON or natural language.
- Include a schema prefix such as `P2G1`.
- Include MDA, service, invoice reference, currency, amount, due date, payer reference, and MAC.
- If a payer reference or invoice reference is too long, mask or shorten it before returning the descriptor.
- Generate the MAC using HMAC-SHA256 over the canonical bill identity and payment terms, then expose only a short checksum in the descriptor.
- Store the full HMAC, descriptor version, and key version in the gateway verification snapshot.

Failed P2G verification response:

```json
{
  "isVerified": false,
  "message": "ALREADY_PAID",
  "accountNo": "",
  "accountType": "",
  "name": "",
  "address": "",
  "currency": ""
}
```

On successful verification, the gateway should append `BillFetched` and `VerificationReturned` events, update the bill status projection to `Fetched` when appropriate, and queue subscribed callbacks without blocking the verification response.

## 2. Incoming Transaction

SIPS sends an incoming payment transaction to the receiving bank.

### Request: `CB_PaymentRequest`

```json
{
  "fromBIC": "SENDBICXXXX",
  "toBIC": "RECVBICXXXX",
  "localInstrument": "INST",
  "categoryPurpose": "CASH",
  "txId": "TX-20260708-000001",
  "endToEndId": "E2E-20260708-000001",
  "amount": 100.5,
  "currency": "SOS",
  "debtorName": "Sender Name",
  "debtorAccount": "9876543210",
  "debtorAddress": "Sender Address",
  "debtorAccountType": "BANK",
  "debtorAgentBIC": "SENDBICXXXX",
  "creditorName": "Receiver Name",
  "creditorAccount": "1234567890",
  "creditorAddress": "Receiver Address",
  "creditorAccountType": "BANK",
  "creditorAgentBIC": "RECVBICXXXX",
  "creditorIssuer": "RECVBANK",
  "remittanceInformation": "Invoice payment",
  "date": "2026-07-08T09:00:00Z",
  "settlementMethod": "CLRG",
  "chargeBearer": "SLEV",
  "bizMsgIdr": "BIZ-000001",
  "msgDefIdr": "pacs.008",
  "clearingSystem": "SIPS",
  "msgId": "MSG-000001"
}
```

### Response: `CB_PaymentResponse`

```json
{
  "endToEndId": "E2E-20260708-000001",
  "status": "ACCP",
  "reason": "Accepted",
  "additionalInfo": "Payment posted successfully",
  "acceptanceDate": "2026-07-08T09:00:02Z",
  "acceptedAtUtc": "2026-07-08T09:00:02Z",
  "txId": "TX-20260708-000001"
}
```

Recommended status handling:

| Status | Meaning |
| --- | --- |
| `ACCP` | Accepted for processing or posted. |
| `RJCT` | Rejected. |
| `PDNG` | Pending manual or asynchronous processing. |

When rejecting, include `reason` and `additionalInfo`.

### P2G Payment Mapping

For invoice-first P2G, bank-side middleware must construct the payment from the verified response. It must not let the customer override the amount or currency after verification.

Required `CB_PaymentRequest` values:

| Field | Required P2G value |
| --- | --- |
| `categoryPurpose` | `C2BBPT` |
| `endToEndId` | UPR or gateway bill ID. |
| `amount` | Verified bill amount. |
| `currency` | Verified bill currency. |
| `creditorAccount` | Revenue account returned by verification. |
| `creditorName` | Revenue Authority, MDA, or account display name. |
| `creditorAccountType` | Clearing type returned by verification. |
| `remittanceInformation` | Structured bill reference, preferably `BILL:{upr-or-invoice-id}`. |
| `txId` | SIPS transaction ID used as payment idempotency key. |
| `msgDefIdr` | `pacs.008` or the configured ISO version. |

Gateway validation before accepting payment:

- Find the bill by UPR, bill ID, or invoice reference from `endToEndId` / `remittanceInformation`.
- Reject if the bill is missing, expired, cancelled, already paid by another transaction, or otherwise not payable.
- Validate amount equals the bill amount exactly.
- Validate currency equals the bill currency.
- Validate creditor account equals the resolved revenue account.
- Enforce idempotency by `txId`, `endToEndId`, and original message ID where available.

Accepted P2G payment response:

```json
{
  "endToEndId": "SRA-TAX-2026-INV123456",
  "status": "ACCP",
  "reason": "Accepted",
  "additionalInfo": "Invoice payment accepted",
  "acceptanceDate": "2026-07-08T09:00:02Z",
  "acceptedAtUtc": "2026-07-08T09:00:02Z",
  "txId": "TX-20260708-000001"
}
```

After a successful payment result, the gateway should create the payment record, append `PaymentAccepted` and `BillPaid`, update the bill status projection to `Paid`, expose the event through cursor-based polling, and queue subscribed callbacks.

If SIPS Connect sends a negative or rejected status, append `PaymentRejected`, store the payment attempt, and do not mark the bill as paid.

## 3. Incoming Status Request

SIPS may ask the receiving bank for the current status of an incoming transaction.

### Request: `CB_StatusRequest`

```json
{
  "endToEndId": "E2E-20260708-000001",
  "txId": "TX-20260708-000001",
  "agent": "SENDBICXXXX"
}
```

### Response: `CB_PaymentStatusResponse`

```json
{
  "fromBIC": "SENDBICXXXX",
  "toBIC": "RECVBICXXXX",
  "localInstrument": "INST",
  "categoryPurpose": "CASH",
  "txId": "TX-20260708-000001",
  "endToEndId": "E2E-20260708-000001",
  "amount": 100.5,
  "currency": "SOS",
  "debtorName": "Sender Name",
  "debtorAccount": "9876543210",
  "debtorPostalAddress": "Sender Address",
  "debtorAccountType": "BANK",
  "debtorAgentBIC": "SENDBICXXXX",
  "debtorIssuer": "SENDBANK",
  "creditorName": "Receiver Name",
  "creditorPostalAddress": "Receiver Address",
  "creditorAccount": "1234567890",
  "creditorAccountType": "BANK",
  "creditorAgentBIC": "RECVBICXXXX",
  "creditorIssuer": "RECVBANK",
  "remittanceInformation": "Invoice payment",
  "date": "2026-07-08T09:00:00Z",
  "settlementMethod": "CLRG",
  "chargeBearer": "SLEV",
  "status": "ACCP",
  "reason": "Accepted",
  "additionalInfo": "Payment posted successfully",
  "acceptanceDate": "2026-07-08T09:00:02Z",
  "acceptedAtUtc": "2026-07-08T09:00:02Z",
  "bizMsgIdr": "BIZ-000001",
  "msgDefIdr": "pacs.008",
  "clearingSystem": "SIPS",
  "msgId": "MSG-000001"
}
```

The response should be built from the bank's stored transaction record. If the transaction is unknown, return a rejected or not-found status according to the agreed SIPS status code set.

For P2G status responses, build the response from the gateway payment record and bill event log. Do not infer paid status from a callback delivery, dashboard projection, or external Revenue Authority acknowledgement.

## 4. Outgoing Status Request

The bank can send a status inquiry to SIPS for a transaction.

### Request: `StatusRequest`

```json
{
  "endToEnd": "E2E-20260708-000001",
  "txId": "TX-20260708-000001",
  "toBIC": "SIPSBICXXXX"
}
```

| Field | Type | Description |
| --- | --- | --- |
| `endToEnd` | string | Original end-to-end payment reference. |
| `txId` | string | Original transaction ID. |
| `toBIC` | string | Target BIC for the status inquiry. |

The expected status response should follow the payment status response structure used by `CB_PaymentStatusResponse`.

For P2G reconciliation, prefer gateway event polling for Revenue Authority systems and reserve SIPS status requests for payment-network transaction state.

## 5. Outgoing Return

The bank can return a received transaction to SIPS. This is the only outbound money movement supported by this profile.

### Request: `ReturnRequest`

```json
{
  "txId": "TX-20260708-000001",
  "endToEndId": "E2E-20260708-000001",
  "reason": "AC01",
  "additionalInfo": "Invalid creditor account",
  "returnId": "RTN-20260708-000001"
}
```

| Field | Type | Description |
| --- | --- | --- |
| `txId` | string | Original transaction ID. |
| `endToEndId` | string | Original end-to-end ID. |
| `reason` | string | Return reason code. |
| `additionalInfo` | string | Human-readable return explanation. |
| `returnId` | string | Bank-generated return reference. |

### Response: `CB_ReturnResponse`

```json
{
  "txId": "TX-20260708-000001",
  "endToEndId": "E2E-20260708-000001",
  "status": "ACCP",
  "reason": "Accepted",
  "additionalInfo": "Return accepted for processing"
}
```

### P2G Return/Reversal Mapping

P2G returns are reversal events against a previously paid bill. The gateway must reject returns that do not reference an existing paid transaction.

Rules:

- Idempotency key is `txId` + `endToEndId` + `returnId`.
- For MVP, support full reversal of the original transaction only.
- If the referenced original transaction is a paid P2G transaction, append `BillReversed` and set the bill status projection to `Reversed`.
- If a future return payload carries a partial amount while partial reversal is disabled, record the exception and return a validation rejection.
- Queue subscribed `BillReversed` callbacks and expose the event through polling.

Use `pacs.004` / `ReturnRequest` for the money movement return. Use the gateway bill event log for Revenue Authority notification and reconciliation.

## Operational Requirements

- Store `txId`, `endToEndId`, amount, currency, debtor, creditor, status, acceptance date, and return ID where applicable.
- Enforce idempotency on `verificationId`, `txId`, `endToEndId`, and `returnId`.
- Validate account eligibility before accepting incoming payments.
- Never send a normal `PaymentRequest` from this bank profile.
- Permit outbound `ReturnRequest` only for transactions previously received by the bank.
- Log every request and response with correlation IDs, timestamps, status, and failure reason.
- Return clear rejection information when validation fails.

Additional P2G requirements:

- Store immutable bill events for `BillCreated`, `BillFetched`, `VerificationReturned`, `PaymentAccepted`, `PaymentRejected`, `BillPaid`, `BillReversed`, and callback lifecycle events.
- Keep dashboard tables and bill status fields as projections only; the append-only event log is the source of truth.
- Revenue Authority polling must be cursor based and return only events with a sequence greater than the supplied cursor.
- Webhooks/callbacks are push notifications for subscribed event types only and must use outbox-based at-least-once delivery.
- Sign callback payloads with HMAC-SHA256 and include event ID, timestamp, signature, and idempotency key headers.
- Mask payer account numbers in callbacks unless full account disclosure is explicitly required and approved.
- Never store plaintext Revenue Authority API keys or HMAC secrets.
- Never trust client-submitted amount for invoice-based P2G; use only the verified bill amount.
- Keep BPC/SVIP integration on the existing `acmt.023/acmt.024`, `pacs.008/pacs.002`, and `pacs.004` flows.

## Minimum Endpoint Checklist

| Endpoint capability | Required behavior |
| --- | --- |
| Incoming verification | Validate account and return `CB_VerificationResponse`. |
| Incoming transaction | Validate, post or queue payment, and return `CB_PaymentResponse`. |
| Incoming status request | Look up transaction and return `CB_PaymentStatusResponse`. |
| Outgoing status request | Send `StatusRequest` to SIPS and process the status response. |
| Outgoing return | Send `ReturnRequest` to SIPS and store `CB_ReturnResponse`. |

## P2G Minimum Endpoint Checklist

| Endpoint capability | Required behavior |
| --- | --- |
| Gateway verification callback | Accept `CB_VerificationRequest`, look up bill, return `CB_VerificationResponse` with P2G descriptor in `name`. |
| Gateway payment callback | Accept `CB_PaymentRequest`, validate exact invoice terms, return `CB_PaymentResponse`. |
| Gateway status callback | Accept `CB_StatusRequest`, return stored payment/bill state as `CB_PaymentStatusResponse`. |
| Gateway return flow | Send or receive return instructions only for previously paid gateway transactions. |
| Revenue Authority event polling | Return only new bill events after the supplied cursor. |
| Revenue Authority callback delivery | Push signed subscribed events with retry and outbox guarantees. |
