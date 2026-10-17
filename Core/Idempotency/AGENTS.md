# Core/Idempotency

`IdempotencyParameterNames.ResultPayload` is the durable handoff between an
operation owner and reconciliation. When an operation row carries an explicit
outer `IdempotencyKey`, it may also carry the owner's serialized replay result.
Reconciliation completes the outer claim with that payload after chain
confirmation; falling back to a raw transaction hash is valid only for owners
that did not supply a replay payload.

`IdempotencyClaimRecovery.TryFailAsync` is the shared best-effort recovery seam
for an owned claim when an unexpected pre-effect exception must still bubble.
It persists only a fixed safe message and attaches any recovery exception type
to the original exception; it never replaces the original stack or records
`ex.Message` as replay state.

## Allocation receipt identity

`AllocationIdempotency` preserves the existing internal allocation ledger key
(`alloc:{apiKeyId}:{tail}`), while its `Correlation` is a separate,
domain-separated lower-case SHA-256 reference. Only the correlation may enter a
durable operation or HTTP response. The raw `LedgerKey` stays internal for the
idempotency ledger and reconciliation hand-off.

Receipt routes require an explicit client key and use `CreateFromClientKey`;
they cannot reconstruct an allocation that used the request-content fallback.

## Allocation refund exclusion

Only the identity gate, allocation-kind validation, amount parsing and fee quote
failures may record `allocation:no-value-effect:v1`, before fee obligations,
wallet preparation or chain effects. The refund path atomically changes that
failed allocation claim to a permanent refund reservation using typed CAS.
Generic failures never qualify. A colon-key integration regression verifies the
package's deterministic record identity against the typed extension.

Prepared restitution wire is stored on the exact value-move claim before submit.
Only the first typed conditional write succeeds. Clearing it compares the complete
expected preparation and requires the caller's provider-backed final no-effect
verdict; pending/unknown evidence never authorizes clearing or a new transaction.
The same CAS advances `PreparationGeneration`. Writers must name the generation
they read, so a delayed preparer cannot reinstall a cleared generation. Provider
preparation keys include the exact value claim and generation; EVM nonce ownership
therefore remains stable across retries and changes only after final no-effect proof.
