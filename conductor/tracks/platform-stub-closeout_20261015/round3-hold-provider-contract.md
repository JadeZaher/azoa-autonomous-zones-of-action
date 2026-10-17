---
type: contract-amendment
status: accepted-for-staged-implementation
owner: lane-B
---

# B2 hold value preparation and custody amendment

Root independently accepted this amendment on 2026-10-15. Every new preparation, including non-Hold callers, requires a durable PreparationKey; missing historical keys permit observation only. Full caught-up validity-window and immutable-protocol evidence is required for authoritative absence. Runtime gates remain unrun.

The accepted B2 domain contract remains binding. Source inventory found the existing Algorand value-transfer module still prepares a new transaction on each call, ignores PreparationKey, and lacks single-sender signature enforcement. Existing canonical framing checks are preserved. The synchronous recipient preparer can send during preparation. Neither path may be reused unchanged for funder charges or participant acceptance. This amendment defines the concrete provider seam before new wire work; no runtime gates have run.

## Ownership and ordered source slices

B owns Services/Hold, IPlatformHold contracts, HoldLedger, new HoldCommand/HoldParticipantAuthorization/HoldCommandReceipt POCOs, Interfaces/Stores/IHoldCommandStore, Providers/Stores/Surreal/SurrealHoldCommandStore partials, new hold action resolver and four handler adapters, HoldController/client sections and new fixtures. Move the existing SurrealHoldLedgerStore out of the service file into the proper provider directory during integration. Root retains Program; HoldRegistration changes remain B-owned. Legacy economic callers in ReserveExchangeManager, ExchangeSettlementReconciler and ReversalCompensationService must migrate with explicit original-source evidence rather than fabricate a principal.

First repair Algorand's existing IValueTransferTransactionModule exact preparation and signed-byte verification for all callers. Next author atomic reserve open/refund/chaining plus immutable command receipts. Then funder inbound, outbound release/refund, participant prerequisites and restricted recovery. Integrate direct/workflow/internal callers and SDK/UI before claiming B2 complete. Existing Ethereum/Solana modules stay owned by their provider lanes until explicitly coordinated; their hold capability additions remain required parity work where the existing hold API advertises those chains.

## Existing value transfer seam

Keep one Prepare/Submit/Observe value-transfer path. Add optional structured cost authorization to ValueTransferIntent, retaining old serialized intents for observation of already-authorized wire. New hold intent must include PreparationKey, original logical command, stage/generation, exact sender/recipient/asset/quantity, signing context, protocol identity and immutable accepted cost quote. The provider refuses new hold preparation without this authority; quote expiry gates durable outer admission, not resumption of its saved unsigned plan.

Add a companion provider capability for read-only value cost/recipient readiness quotation and prepared account acceptance. It returns typed canonical identity, token mechanics, exact native fee ceiling, minimum-balance/rent reserve, payer and expiry. It uses the same provider preparation journal, custody signer, submit/observe validators and persisted bytes; it is not a second generic signer. Account acceptance is a distinct explicitly authorized stage with its own PreparationKey, saved exact wire and actual recipient wallet context. Already-existing authoritative readiness needs no recipient signature.

Algorand transfer preparation claims a canonical unsigned plan before custody with NativePreparationJournal. The note binds the complete immutable value intent, including the hold command and accepted quote. Replays recover that exact plan; signed payload checks require canonical envelope exhaustion, valid single-sender Ed25519 signature, no alternate signer/close/rekey/clawback/group, and full saved transaction semantics. Indexer proof is type-checked, pagination exhausted and genesis bound, including validity range and exact received amount. Complete caught-up absence alone permits no-effect replacement. Existing restitution callers gain durable PreparationKey reuse and exact-wire safety; their historic submitted records are observation-only if they lack new preparation evidence.

## Authority and accounting

Direct commands use actual SigningCredentialWitness and exact selected scopes/wallets; workflows use the saved run credential ceiling, frozen node input and shared ActionExecutionFence. Funder inbound requires TransferSign for the exact funder's owned wallet. Recipient acceptance requires that recipient's actual TransferSign authority and exact operation/stage; no principal reconstructed from beneficiary ID. Reserve outbound uses platform custody only after an immutable authorized reserve command and conserved partition commitment; a missing tenant never implies operator authority. New operator commands require actual recent operator purpose, while internal recovery binds the exact previously admitted economic source receipt.

Reserve-backed Open atomically locks the exact network/protocol reserve identity, applies the shared pure partition transition, writes Held, and writes the command receipt. Refund and ReleaseIntoHold use the same transaction boundary; branching parents cannot release/refund after child commitment. Funder-backed Open reserves the aggregate and command first, persists the inbound plan and first-send authorization, then marks Held only from finalized exact net receipt. Funder amounts are custody liabilities, not fresh reserve inventory; they do not inflate platform issuance counters. Outbound settlement consumes that hold liability exactly once; reserve-backed outbound joins its partition accounting with the unique terminal settlement receipt.

Current asset mechanics and account authority must be proved by each provider. Fee-on-transfer/rebase/frozen/delegated mechanics cannot silently reduce the promised net amount. An unsupported mechanism refuses before fresh admission. Costs and account minimums are explicit prior consent, distinct from asset quantity. The existing provider native transaction/ATA/opt-in implementations are reused behind the companion capability with durable stage identity.

AutoRelease authorizes only the admitted destination, window and exact cost budget. A worker may prepare the already-authorized platform outbound stage under that explicit mandate, never a new beneficiary/funder signature. Restricted observation remains available after mandate revocation. If an original run is canceled, no new first-send authority can be acquired; existing authorized bytes may still settle and must be accounted for. Historical holds without durable command authority are evidence-required and never silently granted a new auto-release mandate.

## Acceptance additions

Author provider fixtures for overlapping preparation returning one canonical plan, custody interruption and retries, appended signed objects, wrong single-sender signature, changed quote/intent, malformed or partial Indexer evidence, and protocol mismatch. Real-store fixtures interleave reserve open/refund/chain/outbound claims and prove conserved counters plus one immutable receipt. Funder response loss cannot charge twice. Participant acceptance is not synthesized by timers. Tests distinguish completed command output from later aggregate state and preserve exact receipt replay after authority loss. Final integrated execution remains required; source acceptance is not runtime proof.
