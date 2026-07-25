# Government Payments Project Business Specification

## 1. Purpose

The Government Payments Project is a government payment platform that allows citizens, businesses, banks, SIPS Connect, and Revenue Authority systems to work from one controlled bill record.

The platform is invoice-first. This means a bill must be created by a Revenue Authority before it can be verified, paid, reconciled, cancelled, voided, or reversed. The gateway does not treat payments as generic bank transfers. Every successful payment must match a known government bill.

The business goal is to give Revenue Authorities and MDAs a reliable way to:

- publish payable government bills;
- route payments to the correct revenue account;
- prevent payment against invalid, expired, cancelled, or already paid bills;
- receive payment and reversal updates;
- reconcile collections by authority, MDA, service, revenue account, currency, and date;
- give operations teams a clear view of payment status and exceptions.

## 2. Main Participants

### Revenue Authority

The Revenue Authority owns the invoice or tax obligation. Examples include tax, customs, licensing, penalties, fees, or other government collections.

The Revenue Authority creates bills in the gateway, tracks their lifecycle, receives payment updates, and uses reconciliation reports to update its own back-office system.

### MDA or Service Owner

An MDA or service owner represents the government department, ministry, agency, or service line for which the payment is being collected.

Each bill is linked to an MDA code and service code. This allows the platform to separate collections by business area, reporting line, and settlement account.

### Government Payments Project

The gateway is the central control point. It stores bills, validates payment requests, records payment outcomes, manages events, sends callbacks, exposes reconciliation data, and supports operations dashboards.

### SIPS Connect

SIPS Connect is the payment network integration layer used by banks to verify and process payments. It asks the gateway whether a bill is payable, sends payment confirmations, checks payment status, and sends reversals where required.

### Banks and Payment Channels

Banks and payment channels allow the payer to enter or scan a bill reference, verify it, and pay it. Once the bill is verified, the bank should use the verified amount, currency, and government revenue account returned by the gateway.

### Finance and Operations Teams

Finance teams use payment and reversal reports for settlement and reconciliation. Operations teams use dashboards, health views, event history, and callback delivery views to investigate exceptions.

## 3. Platform Setup and Connections

Before a Revenue Authority can create payable bills, SPS must onboard the authority and configure its collection accounts.

The required setup includes:

- Revenue Authority record, such as authority name and code.
- Secure credentials for the authority system.
- MDA and service configurations.
- Revenue account mappings for each payable service and currency.
- Optional callback URL for real-time event notifications.
- Dashboard access for operational and configuration users.

Each revenue account configuration defines where money should be collected for a particular MDA, service, and currency. The configuration includes business details such as:

- MDA code and name;
- service code and name;
- revenue code;
- currency;
- bank code and bank name;
- settlement account number and account name;
- account status;
- whether it is the default account for that MDA/service/currency.

This configuration is important because every bill must resolve to a valid active revenue account. If a bill cannot be linked to an active revenue account, it should not become payable.

## 4. Bill Creation and Management

A bill is created by the Revenue Authority or its back-office system. The bill represents a specific payable obligation.

Typical bill information includes:

- Revenue Authority;
- external invoice ID from the authority system;
- UPR or public payment reference;
- MDA code;
- service code;
- revenue code;
- taxpayer or payer reference;
- payer display name;
- description;
- amount;
- currency;
- due date or expiry date;
- linked revenue account.

The platform applies business validation before accepting a bill:

- the invoice reference must be unique for that authority;
- the UPR must be unique for that authority;
- the amount must be greater than zero;
- the currency must be supported;
- the expiry date must be in the future;
- the MDA/service/currency must have an active revenue account.

If the same invoice is submitted again with the same details, the platform treats it as a repeat request and returns the existing bill outcome. If the same invoice reference is reused with different details, the platform rejects it to prevent conflicting bills.

Bills can be created one at a time or in bulk.

## 5. Bill Lifecycle

Each bill moves through a controlled lifecycle. The main statuses are:

| Status | Business meaning |
| --- | --- |
| Available | The bill exists and can be paid. |
| Fetched | A bank or payment channel has verified the bill. |
| PaymentInitiated | A payment attempt has started. |
| Paid | A valid payment has been accepted and recorded. |
| Cancelled | The authority cancelled the unpaid bill. |
| Voided | The authority invalidated the unpaid bill with a reason. |
| Reversed | A previously accepted payment was reversed. |
| Expired | The bill is no longer payable after its expiry date. |
| Failed | An exceptional processing failure occurred. |

The normal payment path is:

```text
Available -> Fetched -> PaymentInitiated -> Paid
```

The normal authority-side cancellation path is:

```text
Available/Fetched/PaymentInitiated -> Cancelled
```

The normal reversal path is:

```text
Paid -> Reversed
```

Paid bills cannot be cancelled or voided. If money has already moved and must be undone, the process is handled as a reversal.

## 6. Bill Verification

When a payer wants to pay, the bank or payment channel sends the bill reference through SIPS Connect to the gateway.

The lookup reference may be:

- UPR;
- authority invoice ID;
- gateway bill ID;
- QR-derived bill reference.

The gateway checks whether the bill exists and whether it is payable. A bill is not payable if it is missing, expired, cancelled, voided, already paid, or otherwise invalid.

If the bill is payable, the gateway returns the verified payment terms:

- amount;
- currency;
- revenue account;
- MDA/service details;
- payer or bill reference details;
- payment descriptor for the bank/payment channel.

The bank should not allow the payer to change the amount or currency after verification. This protects government collections from underpayment, overpayment, and incorrect settlement routing.

## 7. Payment Confirmation

After verification, the payer confirms payment through the bank or payment channel. SIPS Connect sends the payment confirmation to the gateway.

Before accepting the payment, the gateway checks that:

- the bill exists;
- the bill is still payable;
- the amount exactly matches the bill amount;
- the currency matches the bill currency;
- the credited revenue account matches the bill's configured revenue account;
- the transaction has not already been processed with different details.

If the payment is valid, the gateway records the payment and marks the bill as paid.

If the payment is invalid, the gateway rejects it with a clear reason, such as:

- bill not found;
- already paid;
- expired;
- cancelled;
- voided;
- amount mismatch;
- currency mismatch;
- revenue account mismatch;
- duplicate transaction with different details.

Duplicate payment messages with the same transaction details are handled safely so the platform does not create a second payment record for the same payment.

## 8. Reversals and Returns

If a payment that was already accepted must be returned, the platform handles this as a reversal against the original paid bill.

The gateway only accepts reversals for payments that are already known and paid. A reversal cannot be applied to an unknown transaction or an unpaid bill.

For the current business scope, reversals are treated as full reversals of the original payment. Partial reversals are reserved for future support.

When a reversal is accepted:

- the reversal is recorded;
- the bill status changes to Reversed;
- a reversal event is created;
- the Revenue Authority can receive the update through callback, polling, dashboard, or reconciliation export.

## 9. Events and Notifications

The platform records important lifecycle events for each bill. These events give the business an audit trail of what happened and when.

Examples of recorded events include:

- bill created;
- bill fetched during verification;
- verification response returned;
- payment accepted;
- payment rejected;
- bill paid;
- bill cancelled;
- bill voided;
- bill reversed;
- callback queued.

Revenue Authorities can receive updates in two ways.

### Polling

The authority system can ask the gateway for new events since the last event it processed. This is useful when the authority prefers to control the timing of updates.

### Callbacks

The authority can register a callback URL so the gateway pushes selected events to the authority automatically.

Callbacks are signed by the gateway so the receiving system can confirm that the notification really came from SPS and has not been changed in transit. Callback delivery is retried when temporary failures occur. Failed deliveries can be viewed and retried by operations users.

## 10. Reconciliation

The platform supports reconciliation for both payments and reversals.

Finance and authority systems can review or export collections by:

- date range;
- UPR;
- external invoice ID;
- SIPS transaction ID;
- payment status;
- MDA code;
- service code;
- revenue account;
- account number.

CSV exports are intended for finance teams and settlement review. System-to-system reconciliation can use list views or event polling.

Reconciliation outputs include the bill identity, payment identity, amount, currency, MDA/service information, revenue account information, payment status, and relevant timestamps.

## 11. Dashboard and Operations Views

The platform includes dashboard-facing capabilities for human users.

Business and operations teams can use the dashboard to:

- view bills across authorities;
- search by UPR, invoice ID, taxpayer reference, MDA, service, and status;
- view payment and reversal summaries;
- monitor callback delivery;
- identify rejected payments and common rejection reasons;
- monitor recent payment activity;
- check operational health and backlog indicators.

Configuration administrators can manage MDA/service revenue-account mappings through dashboard functions.

## 12. Controls and Safeguards

The platform includes several business controls:

- only onboarded Revenue Authorities can create and manage their own bills;
- only configured MDA/service/currency combinations can become payable;
- payment amount and currency must match the verified bill;
- payment must settle to the configured revenue account;
- duplicate transactions are controlled;
- paid bills cannot be cancelled or voided;
- reversals must reference an existing paid transaction;
- all important lifecycle changes are recorded as events;
- callbacks are signed and retryable;
- finance can reconcile collections and reversals independently of callback delivery.

## 13. High-Level End-to-End Flow

1. SPS onboards the Revenue Authority.
2. SPS configures MDA, service, currency, and revenue-account mappings.
3. The Revenue Authority creates a bill in the gateway.
4. The payer receives or enters the UPR, invoice ID, or QR reference.
5. The bank/payment channel verifies the bill through SIPS Connect.
6. The gateway confirms whether the bill is payable and returns the official amount, currency, and revenue account.
7. The payer confirms payment.
8. SIPS Connect sends the payment confirmation to the gateway.
9. The gateway validates the payment against the bill.
10. If valid, the gateway records the payment and marks the bill as paid.
11. The Revenue Authority receives the payment outcome through callback, polling, dashboard, or reconciliation.
12. Finance uses payment and reversal reports for settlement and control.

## 14. Business Summary

The Government Payments Project is the control layer for government bill payments. It ensures that government payments are based on valid pre-created bills, routed to the correct revenue accounts, paid for the correct amount and currency, and visible to Revenue Authorities, finance teams, and operations teams.

The platform does not simply receive money. It manages the full business lifecycle around a government bill: creation, verification, payment, notification, reconciliation, cancellation, voiding, and reversal.
