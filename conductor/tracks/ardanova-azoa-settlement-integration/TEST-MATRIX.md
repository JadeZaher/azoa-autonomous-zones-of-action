---
type: test-plan
track: ardanova-azoa-settlement-integration
---

# Test matrix

| Area | Scenario | Required proof |
| --- | --- | --- |
| Stripe ingress | Invalid/missing signature | 400; no event or settlement row |
| Stripe ingress | Duplicate event ID | One event row and one downstream claim |
| Stripe ingress | Distinct events for one PaymentIntent | One business settlement and at most one AZOA write |
| Stripe ingress | Wrong account, currency, amount, or order | Deny before readiness or AZOA call |
| Readiness | KYC pending/rejected/expired/unavailable | Every value family denied; collaboration allowed |
| Readiness | Provider/policy/trust revision changed | Former approval becomes not ready |
| Hosted KYC | Concurrent begin | One durable attempt and at most one provider session |
| Hosted KYC | Ambiguous begin response | Recoverable admission state; no second blind create |
| Hosted KYC | Bad callback/webhook secret | Generic denial; no lookup or state transition |
| Hosted KYC | Duplicate/stale event | Idempotent success/no-op; terminal state unchanged |
| Hosted KYC | Redirect claims success | No approval until authenticated server-side outcome |
| Settlement | SDK timeout after submission | `AwaitingReconciliation`; zero resubmissions |
| Settlement | Same principal with wrong/missing `Idempotency-Key` | Same not-found projection; reject a response-reference mismatch |
| Settlement | Reconciliation repeats | One terminal projection; no new value effect |
| Startup | Manual/mock/admin override in Production | Host startup fails |
| Startup | Manual/mock/admin override on Mainnet | Host startup fails in every environment |
| Secrets | API responses, logs, receipts, audit | No Stripe/KYC/AZOA credentials or raw provider result |
| Deployment | Railway release | Exact target IDs and terminal `SUCCESS` plus health evidence |

The final verification sweep includes unit tests, live-Surreal integration,
consumer SDK contract tests, Stripe/KYC transport simulators, frontend checks,
Railway template validation, and an independent security review. It runs once
after all implementation fixes for the increment are applied.
