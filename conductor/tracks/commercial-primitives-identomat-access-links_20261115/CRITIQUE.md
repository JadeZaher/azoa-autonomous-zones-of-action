---
type: critique
track_id: commercial-primitives-identomat-access-links_20261115
---

# Critical Assessment: Gaps & Structural Trade-Offs

1. **Sandboxed Browser E2E Constraint (`slices-browser`)**:
   - On Windows in the constrained execution environment, `vitest --project slices-browser` (Playwright Chromium) hits `EACCES` binding Vite on `::1:63315`. Unit and jsdom slice suites (`unit`, `slices`) run cleanly, while full Chromium browser E2E must run in CI or an unrestricted local shell.
2. **Operator/Member Invoice Settlement Parity**:
   - `PaymentRequestRecord` relies on `paymentLinkUrl` / external hosted settlement; direct one-click payment-request settlement mutation exists on `CommercialTransport.payPaymentRequest`, whereas ArdaNova BFF currently exposes invoice pay/void + hosted request links. Keep future parity tracked in `FUTURIST.md`.
3. **Identomat Live Webhook Verification**:
   - Session state transitions await real Identomat webhook signing secrets and merchant company keys (`DEFERRED-ITEMS.md`). Do not mark real-identity onboarding production-ready until vendor credentials and signed webhook verification are exercised against Identomat sandbox/production.
