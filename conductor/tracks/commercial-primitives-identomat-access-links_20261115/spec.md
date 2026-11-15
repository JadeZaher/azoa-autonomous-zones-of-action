---
type: spec
track_id: commercial-primitives-identomat-access-links_20261115
title: "Identomat Access Links, Ride-Along SDK Components, and Commercial/Account Primitives"
status: completed
---

# Track Specification: Identomat Access Links, Ride-Along SDK Components, and Commercial/Account Primitives

## Goal
Deliver MoonPay/FastWallet-caliber commercial and onboarding primitives across AZOA (`contracts/commercial-primitives.v1.json`, `AZOA.WebAPI`, `sdk/azoa-wallet`, and `frontend`):
1. **Shared Wire Contract (`contracts/commercial-primitives.v1.json`)**: Canonical JSON Schema consumed directly by AZOA services, `azoa-sdk`, and downstream consumers (ArdaNova via gitlink `contracts/azoa-upstream`).
2. **Mintable Signed Access Links (`/api/commercial/access-links`)**: HMAC-SHA256 signed, expiring ride-along URLs across 7 scopes (`kyc`, `consent`, `add_funds`, `cash_out`, `invoice_pay`, `receipt_view`, `statement_view`) with server verification and hosted `/ride` launch routes.
3. **Identomat KYC/KYB Adoption with Graceful Deferral Ledger**: Canonical `Identomat` session lifecycle returning explicit `PendingVendorCredentials` plus structured entries in `ProviderDeferralLedger` whenever vendor API/Company keys are unset—never fabricating KYC approvals.
4. **Invoices, Payment Requests, Ownership Receipts & Account Documents**: Full CRUD/payment/void flows, SHA-256 content-hashed ownership receipts with JSON/print export, and account statement/report generation under `/api/commercial/*` and `/documents`.
5. **Ride-Along React SDK Components (`azoa-sdk/react`)**: Export `AccessLinkRideAlongCard`, `IdentomatVerificationRide`, `InvoiceAndPaymentRequestPanel`, `OwnershipReceiptsPanel`, and `AccountDocumentsAndReportsPanel` with slot injection (`MoneySlots`).
6. **Feature Gate Unlock**: Enable default feature gates in `appsettings.json` / `appsettings.Development.json` while keeping live money & missing vendor credentials explicitly gated and logged.

## Non-Goals
- Modifying sibling `overseasgenai*` repositories.
- Bypassing live provider credentials or fabricating verified KYC/fiat settlement outcomes.
