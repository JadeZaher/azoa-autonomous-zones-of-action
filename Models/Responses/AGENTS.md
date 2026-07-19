# Response contracts

## AZOAResult factories

Expected success and failure paths use `AZOAResult<T>.Success` and `.Failure`
instead of repeating private static object-initializer helpers in managers and
stores. `CaptureException` remains available only at a true exception boundary;
ordinary persistence methods let unexpected exceptions reach central logging.

## public node transparency

`NodeTransparencyResponses` is a purpose-built public projection. Do not replace
it with the operator governance DTOs: those include actor avatar ids and raw JSON
snapshots intended only for `node:govern` principals. Public audit items contain
typed economic fields, versions, and timestamps only. Their opaque cursor is a
transport token, not a record id. `ContentSha256` is a weak semantic ETag,
domain-separated by response kind but not cross-language canonical JSON. Audit
page hashes intentionally exclude the randomized protected cursor encoding;
equivalent tokens represent the same next boundary. The hash is not a signed
claim that the stored history is append-only.

## KYC profile summary

`KycStatusSummaryResponse` is the only KYC projection exposed to a
tenant-driven child credential. It deliberately omits documents, provider
payloads/session ids, reviewer identity/notes, and rejection reasons. The full
submission contract remains user/operator-only.

`TenantCustodialAccountStatusResponse`, `TenantKycSessionResponse`, and
`TenantKycSubmissionResponse` are the only service-to-service projections for
tenant onboarding. They intentionally omit encrypted key fields, raw/private
keys, seed phrases, documents and document references, provider result payloads,
and reviewer detail. `TenantKycStatus` collapses Azoa `IN_REVIEW` into `Pending`
and `EXPIRED` into `Rejected` for the four-state integration contract.
The generic `externalSubject` field is canonical; `ardanovaUserId` is emitted as
a compatibility alias for the initial ArdaNova client.
Identity, KYC-provider, and wallet readiness are separate fields so a missing
production custody adapter does not hide a provisioned identity or block KYC.

## Blockchain operation reads

`BlockchainOperationResponse` is a strict public allowlist for the generic
operation-read routes. It may expose the operation identity, lifecycle state,
timestamps, chain/network labels, and public transaction reference. It must not
grow a generic `Parameters` bag or initiator/idempotency fields: those can carry
payment-provider correlation, replay payloads, custody instructions, or other
server-only data. Add a purpose-built response model when a consumer needs a
new fact.

## Bridge transaction responses

`BridgeTransactionResult.IdempotencyKey` remains a persisted, server-only
exactly-once key. Its JSON serialization is explicitly suppressed so direct
bridge endpoints and flat Bridge/Back quest outputs cannot disclose a raw
client key or its avatar-namespaced ledger derivative. Store mappings preserve
the field for replay and reconciliation; do not replace it with a public
diagnostic field.

## Allocation receipts

`AllocationReceiptResponse` is the caller-authorized settlement projection. It
contains only an opaque receipt/operation reference, target account facts,
public transaction reference, timing, fee facts, and a bounded failure code.
It must never add the raw idempotency key, API-key or tenant identity, operation
parameters, provider/error payload, KYC material, or custody secrets.
