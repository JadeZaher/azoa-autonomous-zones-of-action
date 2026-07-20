---
type: doc
track: ardanova-azoa-settlement-integration
---

# Integration contract

## Secret and authority matrix

| Surface | Owner | Deployment inputs | Must never cross into |
| --- | --- | --- | --- |
| Stripe Payments | ArdaNova | `Stripe__Payments__SecretKey`, `Stripe__Payments__WebhookSecret` | AZOA, browser, logs, KYC adapter |
| Identomat | AZOA KYC adapter | `Kyc__Identomat__CompanyKey`, `Kyc__Identomat__ConfigId`, `Kyc__Identomat__CallbackApiKey` | ArdaNova, browser, logs |
| AZOA SDK | ArdaNova server | AZOA base URL plus tenant-scoped API key/bearer credential | browser, Stripe metadata, client request body |
| Wallet custody | AZOA | node custody configuration | ArdaNova, Stripe, KYC provider |

The reference deployment uses Stripe only for ArdaNova Payments and Identomat
for AZOA KYC. Secrets are deployment inputs only and are never accepted by an
API or written to an audit response. A future Stripe Identity adapter would need
a separately approved boundary and distinct restricted key/webhook secret.

## Runtime sequence

```text
Stripe signed payment event
  -> ArdaNova verifies raw body and deduplicates event ID
  -> validates stored order and persists business settlement intent
  -> evaluates participant readiness and product authorization
  -> calls AZOA SDK once with stable caller-scoped idempotency key
  -> persists original AZOA idempotency key binding, opaque receipt reference,
     and state
  -> terminal successful receipt: complete local settlement
  -> awaiting reconciliation: observe/reconcile same receipt, never resubmit
```

## State mapping

| Input state | ArdaNova settlement state | Allowed next action |
| --- | --- | --- |
| Payment incomplete/failed | `PaymentPending` or `PaymentFailed` | Await Stripe or stop; no AZOA call |
| Payment terminal, participant not ready | `EligibilityBlocked` | Preserve payment fact; refund/operator policy, no AZOA call |
| AZOA write not yet submitted | `ReadyToSubmit` | One worker may claim and submit |
| SDK transport ambiguous | `AwaitingReconciliation` | `GET`/`POST` receipt routes with the same API-key principal and original `Idempotency-Key` only |
| AZOA terminal failure | `SettlementFailed` | Apply explicit product recovery; no implicit retry |
| AZOA terminal success | `Settled` | Project the receipt facts and stop |

## Hosted KYC outcome rule

A browser redirect is presentation only. The short-lived provider session URL or
client credential is returned only to the authenticated subject, never logged,
embedded in an application URL, or stored in plaintext. A callback/webhook is a
wake-up signal, not approval. The server authenticates the event, resolves the
stored provider session, fetches or verifies the authoritative provider result,
deduplicates the provider event, and conditionally transitions the matching
attempt once. Only the minimal normalized status, decision instant,
provider/trust revision, expiry, and payload digest enter AZOA's approval
envelope.

## Current receipt binding (v1)

The current AZOA receipt routes are `GET /api/allocation/receipt` and
`POST /api/allocation/receipt/reconcile`. Both authorize through the same API-key
principal and the original `Idempotency-Key`; `ReceiptReference` is an opaque
response correlation, not a request credential. ArdaNova stores the original
key in its protected settlement record, sends it unchanged on receipt reads, and
verifies that every response retains the recorded `ReceiptReference`. A future
SDK/API version may introduce reference-addressed reads only through a separate
versioned contract and ownership proof.

## External references

- Identomat API and dashboard key flow:
  https://docs.identomat.com/developer-tools/api-reference and
  https://docs.identomat.com/get-started/api-access
- Identomat callback authentication:
  https://docs.identomat.com/developer-tools/developer-guide/callbacks
- Stripe Identity Verification Sessions and events:
  https://docs.stripe.com/identity/verification-sessions
- Stripe Identity eligibility:
  https://docs.stripe.com/identity/use-cases
