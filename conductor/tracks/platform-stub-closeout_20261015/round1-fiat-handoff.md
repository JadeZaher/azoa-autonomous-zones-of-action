---
type: implementation-handoff
---

# Round 1 lane B handoff

Authored 2026-10-15. No build, tests, lint or schema generation executed; integrated sweep remains root-owned.

## Implemented

- Structured allocation no-effect proof only at first four pre-effect refusal gates; typed failed-claim-to-refund reservation CAS. Generic failures remain ambiguous. Colon-key real Surreal integration regressions authored.
- Refund retries require authoritative failed/canceled previous refund and expected attempt; stable key per attempt, cumulative full refund cap. Duplicate stale operator retries cannot start another attempt. SDK/operator API/UI parity.
- Durable event replay uses persisted verified correlations. Refund sweep advances attempt ordering before provider call; permanent failure no longer pins oldest50. Inbox create catch limited to uniqueness failures.
- Payment environment/network persisted in new payout/lot claims, payout uniqueness includes mode/network; legacy defaults unbound. Cross-mode provider effects rejected and cash-out availability refuses unclassified historical obligations.
- Failed bank payout recovery authoritatively refetches failed/canceled bank payout and matching original funding transfer; audited operator request then stable reversal. Confirmed insufficient-funds refusal advances reversal attempt; ambiguous response keeps original key/window.
- Restitution capped by cumulative recovered funding minus already restored quantity; durable target/key reservation precedes transfer. Late paid final bank read blocks value movement; duplicate/reordered refund events preserve debt bounds.
- Prepared value-transfer module now replaces interim post-send transaction receipts. Typed first-write immutable preparation on idempotency key precedes all broadcast; racers reload winning wire. Algorand module validates exact signed semantics, rejects clawback/close/rekey/group, checks genesis and indexer evidence. Finalized expired absence permits CAS-clearing expected prepared bytes; next attempt may prepare replacement. Unknown never clears. Return draw-accounting errors preserve reservation and completed-key replay repairs draw.
- Allocation ledger completion now requires terminal status+hash. On-ramp lot additionally requires authoritative operation-store confirmation before Issued or aging; pending records retain op ID.
- Dev-only test Stripe activation and URLs, Compose explicit allowlisted secret aliases, host-port-aware return/onboarding URLs, Identomat ReturnUrl. No live key alias, no root dotenv mount. Runtime genesis/reserve/current price/current identity gates remain mandatory.
- Central NFT service/store DI wired. NFT SDK/UI delegated to stripe_explore.
- Development NFT fee policies explicitly set revision 1 and zero native units for Algorand/Solana Devnet/Testnet. Empty treasury is valid only because every fee is zero. Mainnet and disabled Ethereum receive no implicit policy; Ethereum local activation will install its explicit development policy with verified module readiness. Production economics remain operator configured.

## Tests authored or adjusted

OnRampRecoveryTests: pre-effect refund exclusion, generic ambiguity refusal, pending/lost refund response, failed retry expected-attempt, crossmode.
FailedBankPayoutRecoveryTests: partial/full cumulative restitution, duplicate replay, paid/pending refusal, mode isolation, late-paid between reads.
OffRampTests: provider-owned prepared mock, crash after submission/replay original identity, accounting repair no second transfer, mode-aware clones.
AlgorandProviderTransactTests: signed SDK polymorphic roundtrip, no-submit preparation, exact-wire resubmit, intent tampering refusal.
SurrealIdempotencyStoreTests: actual colon key proof transition, generic failed exclusion, first prepared wire immutable/expected-clear.
Root-authored OnRampSagaTests independently reviewed: pending hash remains unaged, same operation later confirmed; missing hash stays pending.

## Remaining active implementation/verification (not external deferrals)

- Ethereum/Solana prepared value-transfer module parity; assigned as subsequent Round1 subwave by root. Unsupported modules fail before effect, not completion claim.
- All compile/runtime evidence pending integrated sweep, including generated new fields/schema goldens.
- A 51-row real-Surreal refund fairness regression is authored; runtime evidence and concurrent CAS verification await final acceptance.
- Prepared module indexed semantic/amount/genesis and expiry/caught-up-indexer HTTP fixtures are now authored alongside signed-wire tests; runtime evidence remains pending. Final absence logic statically reviewed by root.
- Legacy pre-preparation ambiguous value-move claims remain parked rather than retransmitted; historical rows are unbound and cannot cross modes.
- Development local real Stripe acceptance/setup remains root-coordinated; vendor KYC/KYB workflow IDs remain actual external dependency as already tracked.

## Shared stable provider contract

Interfaces/IValueTransferTransactionModule.cs and Models/Blockchain/PreparedValueTransfer.cs:
PrepareValueTransferAsync(ValueTransferIntent), SubmitValueTransferAsync(PreparedValueTransfer), ObserveValueTransferAsync(PreparedValueTransfer).
Intent includes chain/network/asset/sender/recipient/base-unit quantity/signing context. Prepared record includes transaction ID/signed base64/protocol identity/intent.
IValueMoveReceiptStore: TryPrepareAsync, GetPreparedAsync, TryClearPreparedAsync expected preparation. Ordinary operations use typed CRUD/CAS; no new raw transactions.
