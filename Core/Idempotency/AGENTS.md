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
