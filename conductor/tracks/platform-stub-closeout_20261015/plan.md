---
type: implementation-plan
---

# Execution plan

## Four-round assessment and delivery loop

Owner instruction (2026-10-15): execute this loop four times. Existing implementation edits finish their bounded package before the first assessment snapshot; they are not discarded or counted as a completed round.

Each round uses two independent parallel assessments against the full goal: an adversarial correctness/completeness assessment, and a sycophancy/optimism assessment that challenges unsupported completion claims and searches for practical ways around proposed deferrals. Reconcile their evidence into a concrete plan. A separate adversarial review must challenge that plan; fix the plan before dispatching parallel implementation waves. Finish each wave with independent code review and a batch of fixes. Assessors do not approve their own code.

- [x] Round 1: parallel assessments; consolidated plan; adversarial plan review and correction; parallel delivery; independent source review and fixes. Runtime acceptance remains in the final integrated sweep.
- [x] Round 2: parallel assessments complete; reconciled plan accepted after adversarial corrections; deployment, generic-hosted, shared action envelope and local receipt/governance amendments independently reviewed with corrections. Remaining round-2 delivery continued inside the round-3 lanes (see `round2-closeout.md`, `round3-closeout.md`).
- [x] Round 3: repeat both assessments, with special attention to hidden semantic stubs and operational recovery; reviewed plan; parallel delivery; independent review and fixes. Codex session stopped mid-delivery 2026-10-15 23:04Z; all work checkpointed as `3d08ace` on `feat/platform-stub-closeout`. Resumed 2026-10-16 in Claude Code (protocol `round3-claude-resume.md`): lanes N/A/B/C/V delivered, every lane independently reviewed (correctness + security on value/auth paths) with ≤2 fix cycles; four fail-closed bug classes found and fixed tree-wide; SurrealForge 1.2.1 prepared for owner publish. Closed 2026-10-16 — summary `round3-closeout.md`, evidence `round3-join-ledger.md`. Source-level only; runtime acceptance remains in the final integrated sweep.
- [ ] Round 4: repeat both assessments across the complete result; reviewed final closure plan; parallel delivery; independent review and fixes.

Every deferral must survive challenge: identify the concrete external dependency, evidence that available authorized work cannot remove it, owner, exact unblock condition, and closure verification. Implementation size or missing local effort is not a deferral. Identomat workflow mocks remain authorized but do not constitute live configuration evidence. Stripe remains in development mode.

The user's integrated test policy remains in force: collect fixes through these rounds, then run the full build/test/lint/typecheck sweep at the end. Static review is distinct from executed verification. Record discovered test failures together and fix them in a batch before rerunning affected gates.

## Delivery checklist

- [~] Inventory branches/worktrees, preserve and consolidate outstanding changes into main, clean integrated references.
- [~] Inventory actual production stubs and reconcile historical open items against code.
- [~] Verify current Identomat KYC/KYB contracts and identify remaining implementation work.
- [~] Verify Stripe implementation and identify remaining integration work.
- [ ] Implement Identomat KYC/KYB gaps and meaningful regression coverage.
- [ ] Implement Stripe gaps and meaningful regression coverage.
- [ ] Close remaining platform stubs in bounded work packages.
- [ ] Independently review all changes and resolve actionable findings.
- [ ] Run integrated final build/test/lint/typecheck/schema gates and record evidence.
- [ ] Complete Git cleanup and report any unavoidable external dependencies.

## Initial Git evidence

2026-10-15: one registered worktree. Main started at `c6c55dd`; `feat/kyc-aml-providers-value-rail` at `5cd9add` was 50 commits ahead. Seven divergent commits on `feat/ardanova-azoa-integration-phase2` were all patch-equivalent according to `git cherry`; all other local and remote tips were ancestors of that HEAD. Remote references refreshed successfully. Independent consolidation review passed.

Local consolidation completed: reviewed track commit `6aa7f0c`, no-ff feature merge `7fc0bae`, patch-equivalent ancestry merge `bb512cf`. `git diff --exit-code 6aa7f0c HEAD` passed. All eight local topic branches deleted after ancestry checks; only main and one worktree remain. Remote main push and remote topic-branch cleanup await integrated verification.

## Initial package inventory

This inventory records the opening findings, not the current source state. Round closeout and review records supersede its original descriptions. Package completion remains unchecked until integrated acceptance; authored and source-reviewed work is not a passing runtime gate.

- [~] KYC callback: validate active attempt/session/authority before AML or identity effects; compare-and-swap terminal writes.
- [~] Hosted sessions: prevent duplicate external creation and concurrent domain resumption; preserve ambiguous outcomes for reconciliation.
- [~] KYB: dedicated tenant-owned business attempt, official Identomat company/beneficiary/review result mapping, authoritative callbacks, persistence and client parity.
- [~] Stripe: monotonic cumulative partial transfer reversal and failure-recovery coverage.
- [~] Stripe: explicit connected-account bank payout lifecycle, separate transfer and bank states, authoritative account-scoped events.
- [ ] Stripe: operator reconciliation/retry and safe refund for charged-but-uncredited on-ramp lots; durable inbox recovery.
- [ ] Wallet authentication: Ethereum and Solana signature verification alongside Algorand, consistent challenge/address binding.
- [ ] Provider DEX execution: reconcile direct provider errors with existing Tinyman/Jupiter adapters; implement actual supported execution paths and Ethereum adapter.
- [ ] Solana: metadata create/update, absent associated-token-account creation, token-account closure.
- [ ] Algorand/Solana: chain-specific contract call/deployment inputs and implementation.
- [ ] Wormhole: authoritative source-message sequence observation and provider settlement; preserve exactly-once invariants.
- [ ] SDK/frontend: remove duplicated fiat reversal DTO and cover newly implemented flows.
- [ ] Documentation: reconcile stale Ethereum key generation, custody and Identomat KYB claims against code/vendor evidence.
- [ ] Additional KYC adapters: close the explicitly unavailable Veriff and generic-hosted scaffolds against documented, authenticated provider contracts.
- [ ] Action capabilities: resolve the correctness findings keeping GovernanceProposalCreate, GovernanceVote, GovernanceTally and CredentialRevoke disabled; require independent verdict before lifting those gates.
- [ ] NFT and credential lifecycle: replace the opening database-only mint/transfer/burn implementation with authenticated, durable chain operations and confirmed ownership projection. Round 1 source delivery and reviews cover the replacement; real protocol and persistence acceptance remains pending.
- [ ] Tinyman backend transaction construction: the opening `BuildSwapTransactionAsync` returned instructions as success without transaction bytes and mislabeled quote/wallet identifiers as tokens. Implement the backend builder with quote, slippage, sender and network binding in the scheduled protocol package.

Detailed initial source evidence: `.omc/research/platform-stub-inventory-2026-10-15.md`. Marker scans are not sufficient to prove absence of hidden semantic stubs; each package needs behavior review.

## Verification preparation

- .NET SDK 10.0.301 is installed. Final test/build sweep has not started.
- Podman development SurrealDB started with its existing volume preserved and `SURREALDB_HOST_PORT=8020`, matching `SurrealTestDefaults.Endpoint`.
- Schema/flowchart generation must run after all hosted-session, KYB and bank-payout POCO edits are complete. Do not hand-author generated SurrealQL.
- Development credentials are loaded from the ignored root `.env` through the explicit Development-only allowlist described below. Deployed secret availability remains unverified.

## Provider configuration evidence

The owner identified the ignored root `.env` as the credential source and authorized development Stripe keys only. A read-only test-key `/v1/account` call authenticated successfully: US account, charges and payouts enabled, transfers and card payments active. No live key was used.

Stripe CLI 1.53.1 was invoked through the official `@stripe/cli` npm package. Its development `listen --print-secret` output was captured without display and saved as `Fiat__Test__WebhookSecret` in ignored `.env`. This is a local CLI-forwarding secret, not proof of a deployed webhook endpoint.

A shared cryptographically generated Identomat company callback key was saved as `Kyc__Identomat__CallbackApiKey` in the same ignored file; both KYC and KYB use it. The owner confirmed that Identomat workflows are not created and authorized mocked verification without them. Configuration IDs were not fabricated; vendor dashboard setup and live verification remain external work. Secrets are absent from tracked artifacts.

- Round 3 source repair: claim invite consumption now joins user ownership and consent revocations atomically; standalone consume and raw-subject claim path retired. Expected target ID retained before claim enables ordinary credential recovery by userId; active/self-owned password login enforced. Added conflict rollback, competing claim, consumed replay, invalid request and recovery login fixtures. Authored only; independent source review and final integrated runtime sweep pending.

- C bounded source review accepted atomic invite/user/consent claim and ordinary credential recovery. Runtime pending.
- Consent SDK lifecycle and consent delivery UI/BFF authored with response-loss/cross-family fixtures; A review queued. Owner consent management UI remains required.

- Owner consent management UI authored with save-before-send, original receipt/current-status separation, idempotent revocation and stale-read protection. Independent review found rejected/expired command recovery could stall; added atomic exact-command withdrawal tombstone competing with admission, original receipt on admission winner, SDK/UI flow and concurrency/response-loss fixtures. Fresh scope vocabulary checked. Source rereview pending; generated command Withdrawn schema and runtime gates still pending.

- Native Solana metadata levy/guard delta independently source-accepted by C. Provider quote/reservation/replay and changed evidence/budget fixtures authored; no execution. Actual opcode3 ELF/runtime/packet and NFT creation adoption remain required.

- Root bounded SOURCE ACCEPTED tenant custody KYC corrections: witnessed atomic create/retire/attach, custody+policy first-egress admission, recovery requires prior CreationStartedAt, unwitnessed tenant manager mutation overloads retired, typed attach-once and owner CAS. EnsureAllOk confirmed in KycEvidenceFence. Runtime remains pending; B continues Hold/B2 caller cutover.

- Removed wallet create-if-absent interface fallback to upsert; explicit production implementations retained. Finite lane convergence checklists requested and received for WTT/Hold. NFT protocol cost authorization design under review; no false completion or implementation deferral.

- R3 bounded independent DEX source review: coordinator/exposure/withdrawal inspected; no blocking issue identified in durable pre-disclosure exposure, separate server-send authorization, validated recovery and withdrawal preserving prior-send evidence. In-flight sends cannot be recalled. Runtime remains unrun.
- R3 NFT protocol-cost foundation authored: immutable opaque-ID server offer, exact participant/draft/physical-network/stage binding and separate fee/rent/levy ceilings; keyed immutable storage and fixtures. Independent review requested. Lifecycle/provider/SDK/UI integration and generated schema remain required before capability completion.

R3 NFT foundation independent review: C source-accepted receipt contracts/helper/immutable keyed store and fixtures. No concrete blocker found; DB-clock fresh admission remains required when wired. Root inspected new files for raw CRUD, catches, duplicated helper and imports; new store uses typed CREATE/SELECT, catches only duplicate conflict, infrastructure/invalid persisted evidence bubbles. No execution gates. Root also source-accepted DEX exact DependencyUnavailable-to-Unavailable mapping and provider-shaped fixture. Next root slice: wire selected saved NFT receipt into lifecycle admission atomically before provider custody, then Solana economics/opcode3/packet checks and SDK/UI. Current Solana SignDurableAsync still checks NFT packet size after signing at caller; move packet preflight ahead of custody as part of provider work.

- R3 Solana preparation: shared provider-owned full-envelope packet budget now runs before durable custody and native fee RPC. Added boundary/overflow fixtures and an actual oversized signing-seam fixture asserting no custody or submission. Independent behavioral source review accepted; requested helper/test layout correction applied. Tests remain unrun. NFT selected-receipt lifecycle/database-expiry and provider economics integration remains next, not deferred.

R3 root independently source-reviewed additive DEX Rust opcode4 (six accounts/82-byte instruction) and JupiterCostGuard builder/fixture: exact Rent17 + canonical Jupiter/Whirlpool code hashes, loader linkage and prior-slot checks, existing opcode1/2/3 unchanged. Bounded source accepted and notified A; subsequent compiler integration not yet reviewed. Actual executable mutation/guard/CU/packet gates remain unrun.

- R3 NFT quote integration authored: null-omitted request/command/intent fields; draft binding excludes only its own cost authorization; store Claim returns typed nullable conflict and fences exact persisted receipt + database-time expiry with creation and workflow authority. Original replay/cancellation wins before quote checks. DB missing/expired/tampered/draft fixtures added. C independently source-accepted store integration. Service now scoped-loads mandatory Solana cost quote, persists it, threads it to Create, and refuses unconsented old/replacement preparation while retaining prepared recovery. DI/harness and no-admission unknown-quote fixture updated; service review pending. Endpoint/provider economics and generation/recipient consent integration still required. No gates run.

B independently SOURCE ACCEPTED latest NFT service selection at round3-nft-cost-selection-review.md. Required remaining fixtures: positive propagation, expired receipt existing replay, historical serialization identity, later-generation refusal. Provider/public quote/renewal/recipient costs unfinished. Root captured official immutable Metaplex master-edition processor/state source to .omc/research/round3-nft-master-edition-policy-2026-10-15.md: at reviewed revision MAX_MASTER_EDITION_LEN is 1+8+9+2 = 20, not historic 282. CreateMasterEditionV3 allocates that exact length via create_or_allocate_account_raw; no extra levy call in processor. Use pinned source for NFT edition rent in forthcoming quote policy. No source policy constants changed yet. A owns v0-aware shared SignDurableAsync conversion, preserving packet preflight and actual custody semantics; coordinate C. Root no further edits to that method.

- R3 NFT provider quote authored: INftProtocolCostModule implemented by Solana with actual pinned guard/policy/Rent/account reads, physical genesis and fee-for-compiled-message, no custody. Explicit allocation policy includes 20-byte master edition from pinned official source, distinguishes credential rent, and separates rent/levy/network fee. Null-omitted recovery plan evidence and optional opcode3 assembly support the quoted message. Shared metadata test evidence helper extracted; provider quote/policy failure/Rent fixtures authored. C review requested; preparation enforcement/public quote issuance/SDK/UI remain next. No gates run.
- R3 independent reviews: root accepted A v0-aware shared durable signing conversion and B workflow-origin mandate mapping with clarifications on domain AwaitingConsent versus existing run/node states and exact original execution record identity.

- R3 NFT provider preparation now enforces selected receipt before custody: exact current policy evidence and participant/generation/network/intent binding, independent account-rent/protocol-levy caps and exact compiled-message fee. Exact-byte submission rechecks its fee cap; historical unquoted prepared wire remains recoverable. Existing unit mints now obtain provider quotes; new changed authority/policy/limit, admitted-expiry and send-fee fixtures added. Local protocol fixture now obtains real quotes and loads env-configured independent guard pins. C accepted preceding readonly quote slice; preparation review queued. Actual guard deployment/catalogue/CU/packet/runtime remains final-gate work.
