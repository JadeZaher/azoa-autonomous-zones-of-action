# Response contracts

## AZOAResult factories

Expected success and failure paths use `AZOAResult<T>.Success` and `.Failure`
instead of repeating private static object-initializer helpers in managers and
stores. `CaptureException` remains available only at a true exception boundary;
ordinary persistence methods let unexpected exceptions reach central logging.

## public node transparency

`NodeTransparencyResponses` is a purpose-built public projection. Do not replace
it with the operator governance DTOs: those include actor user ids and raw JSON
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
bridge endpoints and flat Bridge/Back action outputs cannot disclose a raw
client key or its user-namespaced ledger derivative. Store mappings preserve
the field for replay and reconciliation; do not replace it with a public
diagnostic field.

## Allocation receipts

`AllocationReceiptResponse` is the caller-authorized settlement projection. It
contains only an opaque receipt/operation reference, target account facts,
public transaction reference, timing, fee facts, and a bounded failure code.
It must never add the raw idempotency key, API-key or tenant identity, operation
parameters, provider/error payload, KYC material, or custody secrets.

## Analytics projections (D16)

`TransactionHistoryPage`, `TransactionHistoryEntry`, `PositionsResponse` and
`PositionSummary` are self-scoped read projections over `operation_log`. Their defining
rule is that **absence is a fact and must survive serialization**:

- `Pricing` (`NeverPriced` / `Priced`) is REQUIRED and always serialized beside the
  nullable `Value`; `AmountStatus` (`Known` / `Unknown`) is REQUIRED beside the nullable
  `Amount`. A JSON client with default null-stripping, or a `.Value ?? 0` in a consumer's
  tax spreadsheet, turns an absent object into a silent zero — an explicit enum cannot be
  defaulted away. A real quote of `"0"` stays `Priced` and a recorded zero stays `Known`.
- Every total ships beside the count of what was left out of it
  (`UnpricedEntryCount`, `UnknownAmountEntryCount`, `UnclassifiedEntryCount`,
  `UnsettledEntryCount`, `UnstampableEntryCount`, `IsValueComplete`,
  `IsQuantityComplete`, `IsComplete`). A fiat total that silently omits unpriced legs is
  a wrong number that looks right, on a tax surface.

Do not add a convenience "total" field that collapses these, and do not make any of the
status/count fields optional. Rationale: `Services/Analytics/AGENTS.md`.

## Credential revocation result (D19)

`CredentialRevokeResult.Status` is the authority on what happened, and
`PendingConfirmation` — broadcast but unconfirmed — is **neither a success nor a
failure**. It is returned with the tx hash, `IsError = true`, and
`Code = CredentialRevokeCodes.PendingConfirmation`, because `IsError` is what AZOA client
helpers branch on; a nested status string alone would be read as a completed revocation
and the credential may or may not have moved. Only chain truth decides. Do not add a
boolean `Revoked` convenience field: it would have to guess for exactly this state.
