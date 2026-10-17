---
type: Contract Amendment
status: Accepted by independent lane C review; implementation in progress
---

# Native recovery, identity and resource closure

This amendment adopts every invariant and eligibility condition in `round3-native-cleanup-contract-input.md` as normative. Root authors the implementation; lane C independently reviews the contract and source. Existing custody, native preparation and transaction-position proof remain the implementation foundation.

## Native stage/result contract

Extend the native quote with a separately disclosed fallback buffer-cleanup stage. It is excluded from the normal success sequence and included in the original maximum network-fee disclosure. The planned refund destination defaults to the original payer and is frozen before signing. Normal successful deployment never executes fallback cleanup. An explicit cleanup request can select this branch only after the domain verifies the original wire-resolution rules.

Extend NativeNextStage with a bounded disposition and resource-effect evidence, rather than interpreting every Complete result as successful deployment. Preserve original actual resource identity and economic accounting independently of workflow completion. Dispositions distinguish next normal stage, cleanup required, authority finalization required, recovery quote required, succeeded, aborted clean and failed clean. Receipt/SDK/UI/action projection all consume this same distinction. A deployed program with unfinished authority removal remains a real deployed resource with pending hardening, never a generic failed deployment.

GetNextStage validates ordered history, every preparation intent/stage/generation and exact authoritative observation. Failed/no-effect generations remain in history with actual fee accounting. Confirmed prefix stages are never replayed as fresh business effects. Unknown or missing evidence is not a cleanup trigger. Ordinary observation may update evidence, but may not authorize another stage or participant signature.

## Explicit recovery endpoints and immutable linkage

Add authenticated actor/tenant-scoped read-only recovery quote and explicit recovery start APIs to the existing deployment service/controller, SDK deployment module and operations UI. Inputs identify the original operation and expected version, requested mode (abort predeploy buffer, finish already-required cleanup, finalize existing deployed authority), original resource and a new request key for separately funded recovery. The server reconstructs resource/protocol/artifact/authority/destination from the original immutable request and finalized history; the caller cannot supply replacement business instructions.

Within original caps, recovery records an explicit mode under the parent version CAS and uses the originally quoted fallback. Insufficient cap headroom returns RecoveryQuoteRequired with a read-only exact linked quote. New consent covers only that linked effect, new payer authority and new bounded fees; old caps and all old costs remain unchanged. The exact effect authority stays the original buffer/program authority and current authorized owner; selecting another payer never changes that authority or refund destination.

Extend DeploymentOperation with recovery mode, parent/child identity and immutable binding hash. Add atomic ClaimRecoveryChild and CompleteRecoveryChild store operations. Child claim compares parent actor/tenant, immutable request/artifact/protocol, version/generation/history digest, resolved wire set and absence of another active child. It fences every subsequent parent preparation/first-send CAS and creates/replays the exact child atomically. Child first-send checks the same live parent link and binding. Completion applies authoritative child evidence to parent and child together without rewriting prior receipts. New fee recovery after a failed child requires that child's exact generation to be proven no-effect/final and an exclusive audited replacement link; no parallel cleanup children.

All formerly authorized parent wires must be final or provably unable to execute before a child can claim cleanup. A version fence cannot retract wire already held by a sender. An unauthorized prepared candidate may be retired by CAS; a delayed preparer cannot later authorize it. Confirmed Deploy/Upgrade excludes buffer rollback/program destruction. Current loader account state is checked in addition to the original CreateBuffer proof; matching seed/address alone is not origin proof. Exact finalized CloseBuffer instruction/account/balance evidence proves returned rent.

## Precision, compiler and metadata closure

Use strict decimal strings for public AVM uint64 asset/application IDs, converting once at the provider boundary with canonical range validation. Update SDK, persisted intent, metadata and DEX handoff consistently. No floating-point conversion or signed-long truncation. Coordinate the shared ID contract with A; Root owns shared helpers/model changes.

For server-side TEAL compilation, obtain and retain actual configured/verified algod build/protocol identity along with canonical compiled bytes and hash; caller text is not compiler evidence. Uploaded bytecode/ELF provenance remains explicitly declared unless verified. Extend transaction-position code-history evidence for advertised nested AVM mutations using official confirmed inner-transaction ordering; missing proof remains unknown, not a fabricated match.

Solana metadata reads reuse the existing Metaplex PDA/codec and return authentic metadata, update authority and mutability. Missing/unreadable supply is distinct from zero. Reuse shared bound-Holon extraction for nested DappSeries and standalone composition; include those resources in the existing immutable live authority witness. Public runtime discovery/download/invoke uses current caller access and never inherits deployer custody.

## Acceptance

Real-store races cover parent first-send versus cleanup, concurrent child claim, changed authority/refund/resource, no-effect child replacement and completion projection loss. Real local loader tests cover funded buffer with failed write, abort before deploy, close response loss, actual returned rent, deploy success with failed hardening and cap-exhausted linked recovery. Never close Program/ProgramData as compensation. Exact AVM IDs round-trip at 2^53 and uint64 bounds; compiler identity fixtures reject caller spoofing; nested transaction-position history and authentic Solana metadata include positive and ambiguous cases. SDK/UI tests exercise distinct original effect and recovery state, exact consent retention, lost response and actor/tenant changes.

All tests are authored now and executed only in the integrated sweep after Round 4. This amendment is not a passing-test claim or an external deferral.

