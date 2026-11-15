---
type: deferred-items
track_id: commercial-primitives-identomat-access-links_20261115
---

# Deferred Items Ledger (AZOA)

| ID | Capability / Gate | Status | Reason & Unblock Action |
| --- | --- | --- | --- |
| DEF-AZOA-01 | Live Identomat KYC/KYB verification | Deferred (`PendingVendorCredentials`) | Identomat merchant API key, Company key, and webhook secret are not yet provisioned (`"identomat will take time"`). Provide credentials in secret manager and run live Identomat sandbox verification. |
| DEF-AZOA-02 | `frontend` Playwright Chromium suite (`vitest --project slices-browser`) | Environment-blocked | Windows sandbox raises `EACCES` binding Vite port `::1:63315`. Run `npx vitest run --project slices-browser` in GitHub Actions CI or unrestricted shell before deployment. |
| DEF-AZOA-03 | `azoa-sdk` public npm publish | Pending release | Local consumers (`ardanova-client`) receive updated build in `node_modules/azoa-sdk` matching version pin; publish release package on npm when ready for remote CI. |
| DEF-AZOA-04 | Live real-money settlement activation | Gated (`PAY-D03`) | Requires provider credentials, legal/compliance sign-off, and explicit owner authorization. |
