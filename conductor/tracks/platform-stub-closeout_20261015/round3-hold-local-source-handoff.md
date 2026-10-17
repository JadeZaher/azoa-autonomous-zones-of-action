---
type: Handoff
status: source-review-pending-runtime-unrun
---

# Hold local command slice

Lane B authored the first B2 accounting store without enabling it. HoldLocalCommandPlan and HoldReserveMutationPlan build conserved reserve open/refund/chaining mutations. SurrealHoldCommandStore commits current direct or saved workflow authority, exact reserve/hold epoch and immutable command receipt together. Ordinary reads/writes use typed builders; raw statements compose cross-record atomic fences. Workflow mutations include original action authority and saved signing witness. Receipt replay precedes mutable authority and remains the original command outcome after later refund, chaining or credential revocation.

Aggregate identity includes chain, network, protocol, original owner, tenant and hold reference. The child preserves original ownership/custody and cannot shorten the release window. It inherits the existing reserve liability without a second draw. Original admission receipt evidence must match the aggregate before subsequent local transitions.

Authored unit fixtures cover conservation, child/refund exclusivity, changed owner/protocol/liability, shortened windows and refusal to reinterpret funder funding as reserve issuance. Real Surreal fixtures cover overlapping same-key opening, immutable replay after refund/revocation, refund-versus-chain competition, changed intent and revoked authority. All are unrun under the final-sweep policy. Bounded diff whitespace inspection is clean.

Source inventory: Models/Blockchain/HoldCommandContracts.cs; Helpers/HoldCommandIdentity.cs; Persistence/SurrealDb/Models/HoldCommand.cs, HoldCommandReceipt.cs and HoldLedger.cs; Services/Hold/HoldReserveMutationPlan.cs and HoldLocalCommandPlan.cs; Interfaces/Stores/IHoldCommandStore.cs; Providers/Stores/Surreal/SurrealHoldCommandStore.cs; matching plan and real-store tests.

Required next implementation remains economic-source proof adapters, current asset custody/readiness and cost authorization, real funder inbound/outbound stages, participant acceptance, restricted recovery worker, caller/action/SDK/UI migration, and full provider parity. No new DI registration is requested for this incomplete slice. The old separate reserve draw/hold writer must be retired at cutover rather than retained as fallback. This document is an author handoff, not a source approval or runtime claim.

## Subsequent budget contract authoring

ValueCustodyContracts, IValueCustodyModule and ValueCustodyIdentity define the companion read-only quote and distinct actual-recipient acceptance contract. ValueTransferIntent gains an optional null-omitted Cost property; a regression fixture asserts old no-cost serialized bytes/hash remain identical. Generic amounts retain arbitrary unsigned precision; Algorand applies its own uint64 limits. Algorand's read-only quote and budgeted-transfer checks are authored with malformed-account, foreign-authority and zero-signature fixtures. Acceptance preparation/submission/observation now use the same durable native journal with exact recipient authority, saved signed bytes, budget and indexed self-transfer evidence. Authored fixtures cover real recipient signing, no platform substitution, replay without another signature, exact indexed confirmation and appended-envelope rejection. Source review and all runtime gates remain pending. Full Hold domain integration and Ethereum/Solana companion parity remain required implementation.
