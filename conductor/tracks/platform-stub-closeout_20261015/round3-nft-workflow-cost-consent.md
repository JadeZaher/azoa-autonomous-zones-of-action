---
type: design
---

# Workflow NFT cost consent

Status: proposed for independent adversarial review before implementation.

## Existing constraints

ActionNftOperationService derives its request key from the frozen run/node and records an external operation reference before dispatch. FrozenInput and SigningAuthorityJson are immutable. The NFT store fences workflow effects using ActionExecutionFence. NFT protocol receipts currently bind a direct request draft and initial intent. Substituting a direct request key or rewriting frozen config would break this authority model.

## Proposed flow

1. An admitted Solana NftMint/Grant execution lacking usable protocol consent remains Running with a Pending external observation explaining required cost consent. Its external reference and frozen input are the durable disclosure identity; no NFT lifecycle or transaction is created merely to park it.
2. An authenticated actor endpoint loads the exact execution ID, verifies run/node/reference/frozen-input hash and actor/tenant, resolves the original mint draft, and quotes under its derived request key. Current read authority and the original execution signing witness must both remain valid. The quote must use the same shared intent builder as eventual preparation.
3. An explicit acceptance endpoint takes exact execution ID, offer ID/hash/limits and expected execution version. It reconstructs the original draft and adds only the independently accepted cost authorization. It admits the NFT through the existing atomic quote-expiry plus original workflow authority fence. It must not replace original scope, credential, watermark or frozen intent with the accepting request's newer credential. Actual accepting actor/tenant and current consent are additionally required.
4. The admitted NFT command stores the selected immutable cost receipt. Workflow Matches checks the complete original draft excluding only protocolCostAuthorization, plus the original ActionAuthority and valid receipt bound to this lifecycle. The independent acceptance must not permit any other frozen field to change.
5. Dispatch of an already admitted matching lifecycle resumes that original lifecycle, rather than submitting the frozen pre-consent body again. Observers remain signature-free. Cancellation competes through the original lifecycle key and workflow authority; a cancelled or failed source cannot admit late consent.
6. Replay resolves the original admission before mutable quote expiry and authority checks. Conflicting acceptance must return the original receipt or a conflict, never overwrite it. New preparation generations require separate accepted receipts under their generation and authoritative no-effect history.

## Review questions and required evidence

- Whether an immutable acceptance receipt must be distinct from lifecycle creation to prove exact execution-version and actual accepting credential; do not assume a read followed by Claim gives atomic version protection.
- Exact execution record ID must be bound, not only run/node lookup.
- A source cancellation/revocation race must fail the final atomic admission fence.
- Tests: no custody while parked; wrong actor/tenant/offer/execution; changed frozen intent; expired fresh offer; acceptance replay; concurrent competing acceptances; cancelled source; old credential revoked despite newer caller credential; restart from lost acceptance response; no observer-created signature.

This is required implementation work, not a deferral. It must integrate with SDK/browser disclosure and recovery before claiming workflow Solana mint complete.

## Reviewed plan and initial contract

Independent review accepted the approach with a mandatory separate acceptance receipt atomically created with lifecycle admission. The final transaction must lock the keyed execution at its exact version/signing witness and combine unchanged original ActionExecutionFence with current accepting consent; read-then-Claim is insufficient. Stale recovery reads monotonic execution version before a subsequent exact receipt lookup.

Initial source: NftWorkflowCostAcceptance request/audit contracts, NftWorkflowCostAcceptanceRecord schema POCO, and NftWorkflowCostAcceptanceIdentity identity/binding checks with authored unit fixtures. These are inert foundation only: no acceptance write or route is registered yet. Next implement atomic store admission, original receipt replay/reconciliation, resolver Pending/continuation, endpoints and SDK/UI, then independent review. No gates run.

## Atomic admission source

INftWorkflowCostAcceptanceStore and SurrealNftWorkflowCostAcceptanceStore now implement scoped integrity-checked audit replay and atomic admission. The transaction combines original node credential/grants, current NftMint consent, unchanged ActionExecutionFence, immutable quote freshness, exact keyed Running execution/version/signing JSON/external reference, version increment, audit CREATE and lifecycle CREATE. A frozen-draft reader compares the persisted original mint metadata to the quote before commit; the frozen digest is also locked within the transaction.

Still unregistered and unrun. Required next: real database cancellation/competing acceptance/revocation/replay fixtures, service and resolver wiring, read-only stale acceptance reconciliation, authenticated endpoints, SDK/UI, independent review and final gates.

## Database fixture coverage authored

Real SurrealDB fixtures now cover atomic audit/lifecycle creation and execution-version advancement; exact replay after quote removal and actor deactivation; two competing acceptance IDs; cancellation racing acceptance; changed execution version/reference; and revoked original authority despite a newer valid accepting credential. Fixtures assert no half-commit and no late acceptance after cancellation. The store retries only transient database conflicts using the existing shared helper; stale identity/version or authority conflicts return the original exact receipt or refusal. Fixture bootstrap includes the acceptance, user and workflow tables. No tests executed; independent store review requested.


### Workflow store review correction
B identified that admission must compare complete initial intent, not only quote digest shape. Root now reconstructs self-mint initial intent and checks QuotedIntentHash plus recipient wallet identity before writes; fixtures use a real matching commitment and assert tampered recipient/fee intent leaves no audit/lifecycle. Parallel DB fixture waits are bounded at 15 seconds; Idempotency namespace fixed. All unrun. Required next integration: lifecycle must persist exact accepted-receipt link and fence original signing witness/current revocation at every new preparation and first egress; ActionEffectAuthority alone is insufficient. Already-authorized observation/accounting must remain independent. B correction review pending.

### Original authority after NFT cost acceptance

The immutable lifecycle command now retains its exact WorkflowCostAcceptance receipt (null omitted for historical serialization). Acceptance-store creation checks that link against the audit it commits. NftWorkflowCostAuthorityFence participates in existing requireActionAuthority preparation/first-send updates: it rechecks original credential/grants, exact acceptance audit JSON/hash, exact original execution ID/signing/reference/frozen hash, and the unchanged shared run/node fence. It does not refresh the old cost expiry after admission. Signature-free observation/accounting updates remain independent. Authored real-store fixtures revoke original authority after admission and assert new-effect updates refuse while observation updates remain possible. No gates run; independent delta review pending.

### Service and resolver review follow-through

The workflow cost service/controller and lifecycle atomic-store routing are implemented, with source review by the independent Hold lane accepting their bounded authority/recovery semantics. Solana resolver parks missing cost consent and never performs new signature dispatch while observing accepted work. Additive SDK methods retain the exact request across acceptance and reconciliation. Source fixtures cover ordered execution-before-audit reads, committed receipt recovery after execution loss, parked Solana recovery, and SDK lost-response identity. Runtime validation and the workflow review UI remain required.
