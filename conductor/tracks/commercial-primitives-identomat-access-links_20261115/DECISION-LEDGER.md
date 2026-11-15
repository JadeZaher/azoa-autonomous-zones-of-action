---
type: decision-ledger
track_id: commercial-primitives-identomat-access-links_20261115
updated: 2026-11-15
---

# Decision Ledger: AZOA Commercial Primitives & Access Links

| Decision ID | Decision | Rationale | Status |
| --- | --- | --- | --- |
| COM-01 | Standardize KYC/KYB ride-along integration on `Identomat` while logging missing vendor credentials as `PendingVendorCredentials` in `ProviderDeferralLedger`. | Preserves fail-closed compliance integrity while unblocking UI, access-link minting, and BFF wiring immediately. | Accepted & Implemented |
| COM-02 | Publish canonical cross-repo schemas in `contracts/commercial-primitives.v1.json` referenced via gitlink `contracts/azoa-upstream` in consumer repos. | Prevents schema drift across C# backend, `azoa-sdk`, Node frontend, and ArdaNova without duplicating ownership. | Accepted & Implemented |
| COM-03 | Mint short-lived HMAC-SHA256 signed ride-along URLs across 7 granular scopes (`kyc`, `consent`, `add_funds`, `cash_out`, `invoice_pay`, `receipt_view`, `statement_view`). | Supports embedded consumer ride-along components and shareable payment/onboarding links without exposing static API secrets. | Accepted & Implemented |
| COM-04 | Unlock non-monetary feature flags by default while retaining explicit deferral records and live-money approval gates (`PAY-D03`). | Removes artificial software feature gates locally while honoring provider and compliance boundaries. | Accepted & Implemented |
