---
type: runbook
---

# AZOA — Session Runbook

**Last updated:** 2026-08-20 18:40 · **Branch:** `main` · **Last commit:** `c6c55dd feat(schema): move onto SurrealForge 1.2.0 and its split-out CLI package`

> **Two files are named RUNBOOK.md — they are different documents.**
> This one (`conductor/RUNBOOK.md`) is the *session/continuation* runbook: goal,
> state, decisions, and the next agent's plan. The root
> [`RUNBOOK.md`](../RUNBOOK.md) is the *operational* reference (dev-up/down,
> deploy, diagnostics) and is linked from `tracks.md` + `DEVELOPMENT.md`.
> Do not merge them; do not let this file restate ops procedures.

---

## 1. Goal

Take AZOA from "deployable fail-closed node" to a **single stable node serving two
real application consumers**, each driving its own avatars. Federation is
explicitly **future state** and out of scope for all work below.

Three workstreams, now scoped by the 2026-08-20 grilling gate:

1. **Identomat KYC** — an API-driven, backend-handled flow that certifies an
   avatar and binds it to a real person.
2. **Full E2E provider pattern** — Ethereum, Algorand, Solana **including the
   complete bridge lifecycle**, plus the pure-DB DAG operation set.
3. **Frontend workflow builder** — all four improvement axes, optimized for the
   two consumers below, whose end users **should barely feel that a blockchain
   is involved**.

Added during the gate: **fiat must become a first-class workflow primitive**
(pay fiat to prove ownership; receive a payout), not an out-of-band sidecar.

Added after the consumer gap-analysis (2026-08-20):

4. **Google + GitHub sign-in** (Phase 1B) — currently two decorative buttons and
   no backend scheme; unimplemented front to back.
5. **Consumer primitives** (Phase 5) — soulbound revocation, governance/voting,
   Qard Hasan tracking, and a DEX-style analytics surface. Without these, the
   platform work alone does **not** meet both consumers' stated goals.

**Scope pinned:** USD only (D15). Finance-focused, not tax/regulator-focused —
AZOA exposes analytics facts and consumers run their own tax workflows (D16).

### The two consumers (these define "done" for the builder)

| | **ArdaNova** (`C:\Users\atooz\Documents\Escherbridge\ardanova`) | **Taawun** (`C:\Users\atooz\.codex\worktrees\6e53\tawun`) |
|---|---|---|
| What it is | Worker-owned cooperative gig platform — "Upwork where work earns equity" | Islamic-compliant community app builder (mosques, charities, founders) |
| Stack | Node/JS, game SDK, MCP backend, contracts | Go, Datastar, MCP control plane, CRDT/IndexedDB local-first |
| Econ surfaces | Per-project equity ASAs; ARDA token bridging project tokens → stablecoin **USD payouts**; treasury-backed deterministic rates; Stripe on/off-ramp; soulbound credentials (BRONZE→DIAMOND); task bounties; guild bidding | **Escrow quests** (settle to creator+platform only on buyer confirmation); creator revenue **splits**; Zakat calculator; donation drives; **Qard Hasan** (interest-free loan) trackers; volunteer stipends |
| Users | Innovator, Contributor, Supporter, Guild Member | Architect, Maintainer, Viewer (congregant/donor) |
| Hard constraint | Equity/valuation math stays in ArdaNova; AZOA receives already-decided amounts | **Riba-free** — no interest-bearing primitive may exist in a Taawun flow. **Anti-gharar** — ambiguity must fail closed and refund |

Taawun's own product doc states *"anything touching money runs on AZOA"* and
splits state into **convergent** (CRDT, theirs) vs **transactional** (balances,
payments, escrows, splits, pledges — **AZOA quest graphs**). That split is the
contract the builder must serve.

---

## 2. State

### Verified green — measured 2026-08-21 by the orchestrator, not recalled or reported

| Gate | Result |
|---|---|
| `dotnet build azoa.sln --no-incremental` | **0 errors**, 23 warnings (19 real + 4 MSBuild doc-file notices) |
| `dotnet test tests/AZOA.WebAPI.Tests` | **2431 passed**, 1 skipped, **0 failed** (was 1823) |
| `cd frontend && npx tsc --noEmit` | **exit 0, zero errors** |
| `cd frontend && npm test` | **65 passed**, 11 files (harness did not exist before today) |

⚠ **Always use `--no-incremental` when quoting a warning count.** An incremental
build reports 0 simply because nothing recompiled; that mistake was made twice
this session and propagated into two agent briefs. The clean-HEAD baseline at
`c6c55dd` was **27**, not 40 and not 0.

**Still not run, status genuinely unknown:** integration tests (need live
SurrealDB) and the SDK vitest suite. Do not report these as green.

#### Prior baseline, for reference (measured 2026-08-20 at `c6c55dd`)
`dotnet build` 0 errors / 40 warnings · `dotnet test` 1823 passed / 1 skipped /
0 failed · `npm audit` 0 vulnerabilities. The production-readiness spec's claim
that the frontend audit has high-severity findings was stale then and remains so.

Two drift corrections from this run:
- The production-readiness spec claims *"the frontend dependency audit has
  high-severity findings"* — **stale**, it is clean.
- Warning count is **40** against the recorded 28-warning baseline
  (`build-warning-baseline-2026-06-16`); ~12 have crept in. Re-baseline or fix.

**Not run this session:** integration tests (need live SurrealDB) and the SDK
vitest suite. Treat their status as unknown, not green.

### Chain reality (verified by reading provider source, not docs)

| Chain | Provider | Real today | Fail-closed / absent |
|---|---|---|---|
| **Algorand** | `AlgorandProvider.cs` (1944 ln) | `GetBalance`, `Mint` (ASA create), `Burn` (AssetDestroy), `Transfer` (axfer), faucet, real Ed25519 signer, non-retrying broadcast + pending marker | `SupportsBridging=false`; `MintWrapped`/`BurnWrapped`/`ReleaseFromBridge` refuse; Exchange/Swap need Tinyman/Pact; contract deploy/call need TEAL |
| **Solana** | `SolanaProvider.cs` (621 ln) | **Signer is genuinely complete** (Solnet: deserialize → Ed25519 → `Populate`/`Serialize` → `VerifySignatures`, fee-payer check) | Mint/Burn/Transfer + all bridge primitives return explicit errors — **no SPL build→submit pipeline** |
| **Ethereum** | **none** | — | **Provider class does not exist.** `appsettings.json` advertises it with devnet/testnet `IsEnabled: true` → `BlockchainProviderNotFoundException` at runtime. This is a false-capability defect, not merely incompleteness |
| **Simulated** | `SimulatedBlockchainProvider.cs` | complete | correctly hard-blocked in Production by `GuardAgainstSimulatedModeInProduction` |

**Pure-DB DAG ops already work:** 43 quest node handlers exist; only **7** set
`RequiresChainCapability => true` (Back, Bridge, FungibleTokenCreate, Grant,
Refund, Swap, Transfer). The other **37 run chain-free** against SurrealDB. The
Tier-1/Tier-2 seam is `IQuestNodeHandler.RequiresChainCapability`, enforced at
both dispatch points via `Services/Quest/ChainCapabilityGate.cs`.

### Cross-cutting blockers to real value (all three still open)

1. **No KYC adapter works at all.** `GenericHostedKycProviderService` hardcodes
   `Available = false` and self-describes as *"a scaffold and is not
   production-ready"*; `VeriffKycProviderService` is 49 lines, also
   `Available = false`; Manual is dev-only; the configured default is
   `Kyc:Provider = "unavailable"`. **Identomat has zero code.** Critically, the
   generic scaffold **cannot be configured into** an Identomat integration — it
   returns `Unavailable` from every method regardless of config.
2. **No production custody.** `CustodialAccounts:CustodyMode = "Disabled"`. The
   only KMS/HSM reference in the tree is a string comparison returning *"No
   production KMS/HSM custody adapter is registered."* Keys remain
   config-derived.
3. **Provider-boundary conformance harness does not exist** — that track is
   **0 of 38** plan items. FR-6 requires a route pass conformance before it can
   be enabled, so no route can currently be certified.

The KYC gate itself **is** properly wired: `IValueAccessService` is injected into
`AllocationManager`, `FungibleTokenManager`, and `NftManager`.

### Review ledger

| Phase | Checkpoint | Findings | Verdict |
|---|---|---|---|
| Readiness assessment (2026-08-20) | this session, evidence-based | 3 doc-drift defects + Ethereum false-capability | **Assessed** — no code changed |
| Identomat API research | 4 doc pages fetched → `.omc/research/identomat-kyc-api-2026-08-20.md` | callback is notification-only; body-borne shared secret; 600 req/min IP cap | **Captured** — not yet designed against |
| **Plan review (2026-08-20)** | fable adversarial critic, prompted to refute; 5 claims re-verified against source | **3 CRITICAL** (fiat dispatch path did not exist as specified; D10 did not close the money-printer; custody blocker had no step), 4 HIGH, 3 MEDIUM/LOW | **REFUTED as written** → D7/D9/D10 rewritten, D12 deleted, D13/D14 added, Phase 3 re-planned with a design step, assumptions re-ranked |

| **Phase 0 — honesty fixes (2026-08-21)** | build + unit sweep at completion | boot guard added that diffs `Blockchain:Chains` config against the registry (deliberately NOT via `GetAllEnabledProviders`, which silently skips unregistered chains); Ethereum disabled on every network; parent provider `AGENTS.md` corrected; 1 nullable-deref fixed; baseline re-recorded | **SHIPPED** — 0 errors, 39 warnings (was 40), **1826 passed** / 1 skipped / 0 failed. One guard test loads the *real shipped* `appsettings.json`, so re-enabling any chain without a provider now fails a test |
| **Phase 4 step 19 — builder decomposition (2026-08-21)** | opus adversarial review, separate context, prompted to refute | `quest-canvas.tsx` **1109 → 118 lines**, 20 files. Reviewer diffed `dag-warnings.ts` vs the original memo (**2 cosmetic diff lines**), every `className` literal (3, all explained), and the ReactFlow prop set (**identical, 17 props, same order**). Found 2 MEDIUM + 5 LOW, none a regression | **COULD NOT BREAK IT** — refactor judged faithful. Caveat the reviewer stated itself: `frontend/` has no test runner, so every behavioral claim is static reading + mechanical diff, never execution |

| **Phase 1 — Identomat KYC + AML (2026-08-21)** | opus security review, separate context, adversarial | **3 HIGH, 6 MEDIUM, 1 SUSPECTED-HIGH, 3 SUSPECTED-MEDIUM, 3 LOW**; 16 items verified sound | **REFUTED → REMEDIATED.** Every finding fixed. SUSPECTED-HIGH **refuted empirically** against live SurrealDB 3.1.4. HIGH-1's twin found in a second gate (`RealValueKycGate`) the review never named |
| **Phase 1B — OAuth (2026-08-21)** | built to D17/D18 with structural proofs | no-email-linking enforced by *absence of a lookup method*, not a condition; `(tenant_scope, provider, subject)` modelled on `kyc_identity` | **SHIPPED** — 2317 backend / 65 frontend green |
| **Phase 2 step 8 — value-rail seam** | orchestrator-briefed, verified by orchestrator | `IValueRailProvider` + `RailAmount` + rail-aware gate + Ethereum DI; fixed one failing test at root rather than relaxing it | **SHIPPED** — 2273/0 verified directly |
| **Phase 2 steps 9 + 10 — Ethereum, Solana SPL** | per-provider suites (85 + 24 tests) | **3 fabricated-success bugs** found outside brief, incl. an unknown tx reported `CONFIRMED` on the reconcile path | **SHIPPED** |
| **Phase 2 step 11 — bridge modules** | D22 capability seam | `SupportsBridging` deleted repo-wide; Algorand ships **source-only**, which the boolean could not express; every precondition proven against Algod before signing, incl. the rekey check | **SHIPPED** — 2312/0 |
| **Phase 2 FR-6 — conformance harness** | 77 tests, auto-enrolling | 13 declared exemptions, zero silent skips; a `NotApplicable` with no compensating control **throws**. VAA fail-open removed — `WormholeConfig` now has zero booleans | **SHIPPED** |
| **Phase 4 steps 21 + 22 — builder** | opus adversarial review that **built a jsdom harness and executed the code** | **2 CRITICAL** — infinite render loop on mount (301 renders, with *and* without StrictMode); copy/paste dead code — plus 1 HIGH and 9 lower | **REFUTED → REMEDIATED.** All three green gates passed while the canvas could not render |

Everything else in §7 is **unimplemented**. The **plan itself** has been
adversarially reviewed once and corrected; Phases 0 and 1 were judged
executable, Phases 2–4 were not and have been revised.

### ⚠ Phase 1 security findings — the three that matter (2026-08-21)

These are recorded because each is a *class* this codebase keeps reproducing,
not a one-off bug. Fixes are in flight; re-verify before trusting any of it.

1. **The AML decision was INERT — D24's whole purpose was unmet.** `ScreenAsync`
   and `RegisterMonitorAsync` had **zero production callers** and
   `KycScreeningResult.Decision` was **read by nothing**. `KycGateService`
   consults only KYC submissions, so a sanctioned person passing identity
   verification got full value access with a `HIT` row sitting inert in the
   database. Monitoring was also a no-op, since nothing registered a monitor.
   *Lesson: "the adapter exists" is not "the gate consults it." Folding a
   control into a phase does not wire it — check the read side.*
2. **Cross-tenant leak — the THIRD instance of this class.**
   `kyc_screening_monitor` was `UNIQUE(query_full_name, query_birthday_time_from)`
   with **no tenant in the key**, and its lookup had no tenant predicate — so
   tenant B registering a colliding name+DOB received **tenant A's row**,
   including A's `SubjectAvatarId`. The identity table got this right
   (`(tenant_scope, person_key)`) and the screening table, written in the same
   pass, did not. *Lesson: every new table with a natural key needs the tenant
   dimension reviewed explicitly; getting it right once does not propagate.*
3. **A CAS that did not cover the domain write.** `ResumeAsync` ran *before* the
   version CAS in `HostedSessionFlow`, so two concurrent deliveries both resumed
   and one lost silently — leaving `hosted_session = FAILED` while the KYC
   submission read `APPROVED`. The gate reads the submission, so **value access
   opened against a session the node had recorded as failed.** The existing
   idempotency test ran its five redeliveries **sequentially**, short-circuited
   on the terminal check, and passed trivially. *Lesson: a concurrency test that
   never runs concurrently proves nothing.*

**Unresolved contradiction this surfaced, worth fixing permanently:** the schema
tree disagrees with itself on SurrealDB UNIQUE-on-nullable semantics —
`bridge_tx.surql:7,8` states NULLs do **not** collide, while `api_key.surql:6`
and `avatar.surql:6` invoke "NULL-equals-NULL collision semantics". Both cannot
hold. It matters because `hosted_session` has a UNIQUE index over a nullable
`provider_session_id` that is NONE on every fresh claim — if NONEs collide, a
single Identomat outage leaving one un-attached claim would **brick KYC
node-wide** until an operator deleted the row by hand. An agent is determining
the real semantics against a live instance and recording it authoritatively.

**Verification bar changed 2026-08-21 — this is load-bearing for every future
frontend change.** The long-standing "do not run frontend typecheck, it is
pre-existing noise" rule is **obsolete**: `npx tsc --noEmit` in `frontend/` now
exits **0 with 0 errors** (measured twice, independently), and `npm run build`
exits 0. The typing debt that rule existed for has been paid off. Since
`frontend/` has **no test harness at all**, tsc + build + eslint are the *only*
mechanical gates that exist there — treat them as required, not forbidden. The
`no-frontend-typecheck` memory has been corrected.

⚠ **Two findings from the step-19 review that are NOT yet closed** (an agent is
working them; re-verify before trusting):
- `CanvasToolbar` is ungated by `readOnly` — Clear / Presets / Auto-layout
  still destroy a displayed graph in read-only mode. Pre-existing, but
  `AGENTS.md` documented the gate as complete, which is worse than silence.
- **`dag-warnings.ts` is behind `Services/QuestDagValidator.cs`**: the backend
  enforces an **OnFailure fan-out** rule (`:204-220`) the client never mirrors,
  so a two-OnFailure-arm graph passes the pre-check and then eats a 400 at
  publish. This is precisely the defect **step 21** exists to fix, found early.

---

## 3. Decisions

Settled by the 2026-08-20 grilling gate. Do not re-litigate.

**D1 — One real person MAY hold many verified avatars.** Store a salted hash of
`document_number` + `issuing_state` as the person key, but do **not** enforce
global one-person-one-avatar. *Rationale:* the node's whole model is "two
consumers driven by their own avatars" — forcing one avatar per human would make
a person using both ArdaNova and Taawun share a single avatar across two
tenants, which directly contradicts the shipped cross-tenant isolation and
self-sovereignty work. Compartmentalized per-dapp avatars are the feature, not
the bug. *(This reverses the option originally recommended — see the adversarial
note in §5.)*

**D2 — The person→avatars link is node-operator-only.** It is recorded so
Sybil-sensitive operations can opt in to uniqueness, but it is **never** readable
by a consumer. *Rationale:* if ArdaNova could query it, ArdaNova could discover
that its user is also a Taawun user — a cross-tenant correlation leak that
would undo the isolation D1 is protecting.

**D3 — Uniqueness is enforced per-operation, not globally.** Operations that
genuinely need one-human-one-claim (airdrops, per-person quotas, one-vote
governance, Zakat eligibility) declare it and check the person key at that point.

**D4 — Full bridge lifecycle is in scope for every chain**, consolidated behind a
**strategy-pattern abstraction** so cross-chain logic is generic, readable, and
lives in provider code rather than being re-implemented per chain.

**D5 — Ethereum is built on Nethereum.** *Rationale:* matches the existing
per-chain-SDK precedent (Algorand2, Solnet) and supplies RLP, EIP-1559 typed
transactions, and ABI encoding rather than hand-rolling them on a value-bearing
path. Signing still sits behind `ITransactionSigner`.

**D6 — All four builder axes are in scope:** decompose `quest-canvas.tsx`,
authoring UX, run observability, and chain-capability clarity.

**D7 — Fiat is a value rail, but "zero new node types" is RETRACTED.**
*(Revised twice on 2026-08-20; the fable adversarial review refuted the second
form. This is the corrected version — read the retraction, it matters.)*

The claim that "quest handlers already dispatch value through
`IBlockchainProviderFactory.GetProvider(chainType, network)`" was **false**, and
was asserted without reading the handlers. Verified ground truth:

- `Services/Quest/Handlers/TransferNodeHandler.cs:31` calls
  `INftManager.TransferAsync(cfg.NftId, ...)` — it moves **an NFT by Guid**.
  `GrantNodeHandler` mints an NFT; `RefundNodeHandler` reverses a prior NFT
  transfer of the same `NftId`; `SwapNodeHandler` delegates to DEX adapters.
  **None of these configs can express amount + currency + destination**, which is
  what a fiat payout is.
- Chain dispatch happens *inside managers*, driven by `wallet.ChainType`
  (`Managers/WalletManager.cs:190,580`), not by node config.
- The handlers default a missing chain type to `"Algorand"`
  (`TransferNodeHandler.cs:41-42`) — an unstamped fiat op would reconcile
  against Algorand.

So fiat requires **either** fiat-specific node types **or** a genuine value-model
refactor. It is not a drop-in. Scope it honestly; do not carry the "zero new
node types" claim forward.

**D9 — `IValueRailProvider` stands as the right seam, but introducing it is a
factory + gate refactor, not a re-parenting.** Verified blockers:

- `BlockchainProviderRegistration` takes `Func<IBlockchainProvider>` and
  `GetProvider` returns `IBlockchainProvider`
  (`Providers/Blockchain/BlockchainProviderFactory.cs:11,31,41,98`). A
  `FiatRailProvider` that does not implement `IBlockchainProvider` is therefore
  **type-impossible to register** as written. A rail-aware factory is required.
- `Services/Quest/ChainCapabilityGate.cs:44-49` checks "actor has ANY wallet
  bound." That is wrong in both directions for fiat: a user with an Algorand
  wallet passes for a fiat payout with no fiat destination, and a fiat-only
  Taawun congregant — exactly the persona who should never see a blockchain —
  **fails** the gate for a pure fiat payout. The gate must become rail-aware.
- `ResolveEnabledNetworkConfig` (`:175-188`) requires Devnet/Testnet/Mainnet;
  Stripe is live/test.

The interface split itself is still right (`IBlockchainProvider :
IValueRailProvider`, chain-only members staying on the chain interface; fiat
reaching Stripe **only through `fiat-sidecar/`**, never directly, per integration
contract §3).

**D10 — `SettlementProfile` is necessary but NOT sufficient; the real primitive
is a reversal-event pipeline.** A release-time check does not close the
money-printer, it relocates it. Taawun escrow releases on buyer confirmation
(day 3); a chargeback lands day 90. Reading a ~120-day window at release either
blocks all fiat-legged escrow for four months — commercially dead, and it kills
"pay fiat to prove ownership" — or releases and eats the post-release reversal.

Worse, reversals are **silently discarded today**:
`sdk/azoa-wallet/src/orchestration/stripe.ts:159-160` returns
`{ status: "ignored" }` for every event that is not `checkout.session.completed`,
so `charge.dispute.created`, `charge.refunded` and
`charge.dispute.funds_withdrawn` are dropped on the floor.

Required instead: the sidecar forwards dispute/refund events; AZOA records them
as **first-class ledger facts** and runs a **compensation saga**; plus a per-flow
risk policy (delayed payout, reserve, or revocable representation until
finality). Partial refunds and multi-recipient Split legs (clawback from N
parties) are part of this primitive, not edge cases.

**D11 — Fiat-in and Identomat KYC-begin share one hosted-session abstraction —
and Phase 1 must actually produce it.** Both are begin → hosted redirect →
callback → **authoritative server-side re-fetch**. But steps 4–7 build against
`IKycProviderService`, a KYC-specific interface, so as originally written Phase 1
produced nothing reusable and step 15 dead-ended. Phase 1 must name
`HostedSessionFlow` as an explicit deliverable, or this reuse claim is dropped.

**D12 — DELETED. The bridge strategy is cross-*chain*, not cross-rail.**
*(Refuted by review.)* The bridge lifecycle is Wormhole-specific end to end —
`AwaitingVAA → VAAReady → Redeeming`, emitter chain-id/address/sequence,
guardian-quorum VAA verification (`Services/CrossChainBridgeService.cs:117-191`).
Fiat has **no lock** (a Stripe capture is a settlement with a 120-day reversal
tail — the opposite of a lock), no proof artifact, no wrapped asset, no redeem.
Generalizing the strategy over fiat would either hollow it into "ordered steps
with idempotency" — re-deriving the saga layer that already exists — or leak VAA
concepts into fiat. "Pay fiat to prove ownership" is a payment-then-issuance
**saga** (D11's pattern), which the durable quest engine already models.

**D13 — The amount type is a defect to fix while the interface is open.**
`Interfaces/IBlockchainProvider.cs:64-85` types amounts as `ulong`, which
overflows at ~18.4 ETH in wei — the interface **cannot represent ordinary
Ethereum amounts**. That same signature is where fiat's missing **currency**
dimension belongs. Fix both in step 8, while re-parenting, not after three
providers depend on the old shape.

**D15 — USD only for now.** Multi-currency/FX is dropped from scope. **But keep
the currency *dimension* in the type** added by D13 (pinned to `USD`), so adding
a second currency later is a value change and not a schema break.

**D16 — Finance-focused, not tax/regulator-focused: expose DEX-style analytics
instead of computing obligations.** AZOA supplies *facts*; consumers compute
their own tax workflows. This is the read-side mirror of the existing boundary
rule ("AZOA receives already-decided amounts, never computes economics") — it
stays consistent rather than adding a new posture.

Shape: a normalized transaction/position history — timestamp, operation type,
asset, amount, counterparty, **USD value at time of transaction**, tx reference,
fee — plus realized/unrealized position primitives, queryable per avatar and
date range.

⚠ **Design constraint that must be honored now, not later:** the USD value at
transaction time has to be **stamped on the operation record at write time**.
Reconstructing historical prices after the fact is lossy and expensive, and a
tax workflow built on reconstructed prices is not defensible. This affects the
operation model in step 8, so it is cheap now and costly later.

**D17 — OAuth is an authentication factor bound to an avatar, never an identity
authority.** The GitHub and Google buttons at
`frontend/src/app/(auth)/register/page.tsx:139-155` are **decorative** —
`type="button"`, no `onClick`, present only on register (not login) — and the
backend registers no OAuth scheme at all (`Program.cs:162-167` is JwtBearer,
plus ApiKey and CredentialFree). This is unimplemented front to back.

Binding model, consistent with D1–D3:
- An `AvatarExternalLogin` record: `(provider, subject) → avatarId`, unique on
  `(provider, subject)`. Subject is the provider's stable id, **never the email**.
- Same as D1: one Google account **may** back different avatars on different
  consumers. Bind **per tenant**, not globally — a global binding would
  reintroduce exactly the cross-tenant collapse D1 exists to prevent.
- **A Google or GitHub account never satisfies KYC.** OAuth proves control of an
  account; Identomat proves a person. They are different claims and must not be
  conflated on the value gate.
- ⚠ **Never auto-link an OAuth identity to an existing account by matching
  email.** That is the classic account pre-hijacking vector: an attacker
  registers a password account on a victim's email, the victim later signs in
  with Google, and the accounts silently merge. Linking requires an already
  authenticated session and an explicit user action.

**D18 — OAuth flow is backend-driven so client secrets never reach the browser.**
`GET /auth/oauth/{provider}/start` → provider redirect (with `state` +
PKCE) → `GET /auth/oauth/{provider}/callback` → validate → issue the **same AZOA
JWT the password path issues** → hand the frontend a short-lived exchange code.
The Next.js bundle must never carry a client secret. Use
`Microsoft.AspNetCore.Authentication.Google` and an equivalent GitHub handler
rather than hand-rolling the exchange.

**D19 — Soulbound revocation uses Algorand's native clawback; it is modest work,
not a research problem.** ASAs carry a `clawback` address as a first-class
field, and this codebase **already sets it**
(`Managers/FungibleTokenManager.cs:155`, `clawbackAddress: wallet.Address`).
Revocation is an `AssetTransferTransaction` with `AssetSender` set — a clawback
transfer signed by the clawback address. `RefundNodeHandler.cs:26-34` currently
fails closed on soulbound assets for want of exactly this primitive.

Cleanest in-app shape: a `CredentialRevoke` node plus a revoke route, authorized
to the credential's **issuer** (not its holder), emitting the same operation
record as any other value move so it lands in D16's analytics. This unblocks
ArdaNova's BRONZE→DIAMOND tiers, which today can promote but never demote.

**D20 — Governance is tallied off-chain, anchored on-chain only if publicly
verifiable voting is actually required.** Do **not** put ballots on chain by
default: it is slow, costs fees per vote, and makes voter choices permanently
public — usually the opposite of what a co-op wants.

Cleanest shape: a proposal is a Holon with typed vote children; tally runs in a
**pure-DB (Tier-1) node**, so it needs no chain capability and no custody. If
ArdaNova wants public verifiability, anchor the **tally hash** on chain at close
— one transaction per proposal, not per vote. This also matches Taawun's
convergent-vs-transactional split: deliberation is convergent, only the outcome
is transactional.

**D21 — Qard Hasan needs a tracker, not a new value primitive — it is close to
free.** A principal-only loan is a Holon carrying a disbursement and a repayment
ledger, with the actual value moves reusing the existing Grant/Transfer
primitives and the balance projection computed in a pure-DB node. **No interest
accrual node may ever exist** (D-riba constraint); the absence is the feature.
The one thing to get right is that partial repayment and write-off are ordinary
states, not error paths.

**D22 — The bridge is generic over *capability* and *protocol*, never over the
provider type — and the capability seam already exists.**

`IBlockchainProviderModule` + `TryGetModule<T>` is live on both
`IBlockchainProvider` and the factory (`BlockchainProviderFactory.cs:142`,
implemented as a `provider is T t` type test), and providers already declare
capabilities this way: `AlgorandProvider : …, IAlgorandASAModule,
IAtomicTransferGroupModule, IAtomicTransferGroupObservationModule`;
`SolanaProvider : …, ISolanaMetaplexModule, ISolanaSPLModule`. The bridge simply
does not use it yet.

*Why not generic over the provider type:* `IBridgeStrategy<TSource, TTarget>
where TSource : IBlockchainProvider` cannot bind, because the chain pair is
**runtime data** — `GetProvider(chainType, network)` returns `IBlockchainProvider`
and the concrete type is unknown at compile time. Closing that generic needs
`MakeGenericType` reflection, which forfeits the compile-time safety that
motivated generics, converts a missing implementation into a runtime failure, and
complicates DI registration. It is a dictionary lookup with extra steps.

**Axis 1 — capability modules (runtime-resolved, existing seam):**

```csharp
public interface IBridgeSourceLeg : IBlockchainProviderModule {
    Task<AZOAResult<BridgeLock>> LockAsync(BridgeLockRequest r, CancellationToken ct);
    Task<AZOAResult<string>>     ReleaseAsync(BridgeReleaseRequest r, CancellationToken ct);
}
public interface IBridgeTargetLeg : IBlockchainProviderModule {
    Task<AZOAResult<string>> MintWrappedAsync(...);
    Task<AZOAResult<string>> BurnWrappedAsync(...);
}
```

```csharp
if (!source.TryGetModule<IBridgeSourceLeg>(out var sourceLeg))
    return FailClosed($"{sourceChain} cannot act as a bridge source.");
if (!target.TryGetModule<IBridgeTargetLeg>(out var targetLeg))
    return FailClosed($"{targetChain} cannot act as a bridge target.");
```

**Axis 2 — protocol (statically known, so the generic earns its keep):**

```csharp
public interface IBridgeProtocol<TProof> {
    Task<AZOAResult<TProof>> ObserveAsync(BridgeLock locked, CancellationToken ct);
    Task<AZOAResult<Unit>>   VerifyAsync(TProof proof, CancellationToken ct);
}
// WormholeProtocol : IBridgeProtocol<WormholeVaa>
```

The lifecycle varies by **protocol**, not by chain pair — Algorand→Solana and
Ethereum→Solana over Wormhole share one proof type and one state machine — and
the protocol is chosen at composition time, so `TProof` binds statically. Keep
guardian-quorum verification inside this one type;
`Secp256k1VaaSignatureVerifier` remains the only verifier and stays fail-closed.

**What this buys, beyond tidiness:**
- **Deletes the hand-written refusal bodies.** A provider that cannot bridge
  simply does not implement the module — **absence *is* the fail-closed**,
  structurally. This strengthens the "no fabricated success on a value path"
  invariant: you cannot fake what you do not implement, and there is no refusal
  body left to regress into an `Ok`.
- **Removes a boolean that can lie.** `SupportsBridging` is one flag for both
  directions and is already inaccurate — Algorand's `LockForBridgeAsync`
  genuinely broadcasts, but the flag reads `false` because the *other* three
  primitives are unsafe. Module presence cannot drift from reality.
- **Makes direction asymmetry expressible** — source-capable but not
  target-capable, which is Algorand's actual state today.
- It is where `Services/CrossChainBridgeService.cs` (**1441 lines**) sheds its
  per-chain branching: resolve two legs + a protocol, run one lifecycle.

**Boundary:** this does **not** extend to fiat. `IBridgeProtocol<TProof>`
presumes a proof artifact and fiat has none — the same reason D12 was deleted.
Cross-rail stays a saga; cross-chain becomes this.

**D14 — Production custody is a scoped workstream, not an assumption.** §2 names
it as a cross-cutting blocker and the plan then moved real value in four steps
without it. Escrow additionally needs a **hold**, which on Algorand is either a
TEAL contract (deploy/call unimplemented) or a platform-custodial holding account
(custody disabled) — so "layer Escrow on the existing primitives" was
unbuildable. Either schedule custody before Phase 3 or record an explicit,
gated decision that dev-grade custody is acceptable for the first milestone.

**D8 — Callback is an untrusted trigger; `/result/` is the only authority.**
Identomat session-status callbacks carry only a status. AZOA must always
re-fetch server-side. (Matches production-readiness FR-5 AC2.)

---

### Settled 2026-08-21 (this session's gate)

**D23 — §4 assumption 0 is RESOLVED: uniqueness is per-tenant by default, with
global scope an explicit per-operation node-operator opt-in.** The person key is
HMAC'd under a **per-tenant salt**, so the same human resolves to an unlinkable
key in ArdaNova versus Taawun. *Rationale:* this is the only default consistent
with D1/D2 — a single node-wide salt would make the person key a cross-tenant
correlation oracle, which is exactly the leak D2 forbids, and this repo has
leaked cross-tenant through generic read surfaces twice. The residual risk,
accepted knowingly: an operator who forgets to opt a Sybil-sensitive operation
into global scope gets no cross-tenant protection. Per assumption 1, denials
must still be **indistinguishable from ordinary ineligibility** in any
consumer-visible response.

**D24 — AML/sanctions screening is folded into Phase 1, not deferred.** The
plan previously listed AML as "entirely absent" with no owning step while
Phase 3 moved real USD. Screening lands in the same adapter as KYC rather than
re-opening it later. Research is captured at
`.omc/research/identomat-aml-screening-2026-08-21.md`. Four facts from it change
the design and are not optional:
- All 5 screening endpoints are on `external-api.identomat.com` (the newer JSON
  host, camelCase `companyKey`) — **not** the legacy `widget.identomat.com` host
  that serves `/result/`. The adapter spans two hosts, as assumption 5 predicted.
- **Monitoring has no callback channel at all.** The 6 documented callback types
  are all session/OTP/video-call; none are screening. Ongoing monitoring is
  **cold-poll only** against `list-screening-monitoring-records` with a `date`
  high-water-mark. This is worse than KYC's "untrusted trigger" — there is no
  trigger.
- The 600 req/min per-IP cap is **one shared budget** across KYC *and* screening.
  A per-avatar poll loop collides with it almost immediately; the monitor poll
  must be a single batched paginated call, never one call per monitored identity.
- There is **no APPROVED/REJECTED enum** on screening. Risk decisioning
  (hit / no-hit / manual-review) is entirely AZOA's, derived from `score` +
  `topics`. Persist the `scoreThreshold` queried with, since it is not echoed
  per hit and the result set cannot otherwise be explained or reclassified.

⚠ **Gap this research closed with a negative finding:** Identomat exposes **no
company/entity screening endpoint** — the AML section is 5 person-only
endpoints, confirmed against the full 40+ endpoint site listing. Any future KYB
counterparty screening cannot be built on Identomat as documented. This is a
product/compliance escalation, not an implementation detail.

**D25 — `KycIdentity` carries a retention period and an erasure path from day
one.** Erasure tombstones the retained personal fields (`first_name`,
`last_name`, `birthday`, `citizenship`) while **preserving the salted person
key**, so GDPR Art. 17 erasure and the KYC record-keeping obligation can both be
satisfied, and uniqueness checks plus the audit trail survive an erasure.
Retrofitting this onto live identity data would be a migration on the most
sensitive table in the system.

**D26 — Phase 2 ships in full: no feature skips.** An evidence sweep of both
consumer repos found that **neither requires** Ethereum (step 9), Solana SPL
(step 10), or a cross-chain bridge (step 11): zero EVM dependency in either tree
(ArdaNova's only hit is a "Solidity" skill-tag chip in an opportunities filter);
`@solana/spl-token` appears solely as an optional peerDependency of AZOA's *own*
`azoa-sdk` inside ArdaNova's lockfile, imported by nothing; and neither repo uses
"bridge" in the literal blockchain sense — ArdaNova's usage is figurative
(project-token → USDCa, Algorand-native throughout). ArdaNova's tokenomics spec
states *"USD is source of truth … blockchain is the accountability layer"* and
designs for graceful degradation to off-chain; Taawun's `pkg/financial` models
every flow as `{Currency, AmountMinor, FeeMinor}` with no wallet, asset, or
chainType field anywhere.

**The owner reviewed this evidence and directed that all three ship anyway** —
providers built first in parallel subagents, then the session closes the
`IValueRailProvider` seam (step 8) over them. Recorded so a future session does
not "rediscover" the scope reduction and re-litigate a decision already made
against the evidence.

⚠ **Known cost of this ordering, accepted:** D13 changes the amount type from
`ulong` to amount+currency, so providers written before step 8 are written
against the old signature and need a mechanical sweep afterwards. Contain it as
**one deliberate migration during step 8** that sweeps all providers at once —
do not let it scatter into per-provider fixups on a value path.

---

### Settled 2026-08-22 (handoff gate)

**D27 — Production KMS/HSM custody is built BEFORE any Phase 3 fiat step.**
The alternative on offer was an explicit gated acceptance of dev-grade
config-derived custody for a first capped milestone; the owner rejected it.
So `CustodialAccounts:CustodyMode = "Disabled"` and the absent production
adapter are now a **scheduled workstream, not a documented risk**, and Phase 3
steps 14–17 stay blocked until it lands. *Foundation already in place:* the
chain-aware platform door shipped 2026-08-21
(`Services/Custody/PlatformKeyDerivationRegistry`, `IPlatformKeyDerivation`,
per-chain `TryDerive`) is the seam a KMS/HSM adapter plugs into — it replaces
the *derivation*, not the chokepoint. Escrow additionally needs a **hold**,
which still does not exist on either path.

**D28 — Terminology neutralization + domain renames.** AZOA is the neutral
value/workflow substrate; consumer-specific vocabulary belongs in the consumer,
not the platform. Three strands, one sweep:

1. **`Avatar` → `User`** and **`Quest` → `Action`** across C# models and
   managers, SurrealDB tables + regenerated schemas, REST routes, the TypeScript
   SDK, and the frontend.
2. **Strip loaded religious language from AZOA's own primitives.** The
   *constraints stay, the vocabulary goes* — this is de-branding, not a scope
   cut. Concretely: D21's "Qard Hasan tracker" becomes a **principal-only loan
   tracker** whose defining property is that **no interest-accrual primitive
   exists**; "riba-free" becomes "no interest-bearing primitive may exist in a
   flow"; "anti-gharar" becomes "ambiguity must fail closed and refund"; Zakat
   is a consumer-side calculation over AZOA facts, not an AZOA node. Taawun
   keeps its own vocabulary in its own product.
3. **Strip residual OASIS-project vocabulary** wherever it survives. Extends the
   existing branding rule recorded in `project-renamed-azoa` (never "Sleek"),
   which already covers C# namespaces, env vars, DB namespace and containers.

⚠ **This is a breaking change to DB tables, REST routes, and the SDK.**
`greenfield-prelaunch-no-compat` applies — prefer a clean rename over compat
shims — **but the node is live on Railway**, so it needs the same reconciliation
treatment as any schema change. Sequencing consequence in D29.

**D29 — Deployment reconciliation, split by urgency.** The live Railway
deployment diverges from the code on three axes: five new tables
(`hosted_session`, `kyc_identity`, `kyc_screening_monitor`,
`kyc_screening_poll_state`, `kyc_screening_result`, plus
`avatar_external_login` / `oauth_exchange_code` from Phase 1B), the forward
migration widening `operation_log.amount`, and hazardous Ethereum wallet rows.
The owner chose full reconciliation. **Split it, because the two halves have
different clocks:**

- **Urgent, do first, independent of everything else:** delete and regenerate
  every Ethereum wallet row. Treat them as **compromised, not merely broken** —
  the old "recovery phrase" leaks ~99 bits of the private scalar, so anyone who
  saw a backup can derive most of the key. This is small, and it should not wait
  behind a rename.
- **Deferred until after D28's rename:** the schema/migration apply. Doing it
  now means doing it twice, because D28 renames tables. One combined apply after
  the rename is strictly less risky than two.

**D30 — Commit strategy: one commit on a branch off `main`.** The owner chose a
single commit over themed ones. Consequence to accept knowingly: this session's
work is **not bisectable** — a regression traced into it lands on one ~8,900-line
commit covering seven workstreams. Mitigation available if it matters later:
the review ledger in §2 maps each workstream to its verdict and test counts, and
every phase's tests are named in the session log.

---

## 4. Assumptions

`<assumption> · default taken · to reverse`

0. **⚠ SHOULD HAVE BEEN A BLOCKING QUESTION — the scope of D3's per-operation
   uniqueness check is unspecified: per-tenant or global?** · default: none
   taken; the plan simply never said · to reverse: this decides the
   `KycIdentity` schema and the check in *every* future Tier-2 node, and the
   wrong default is a **silent cross-tenant privacy break**. Recommended
   resolution (from review): default checks to **per-tenant** scope with the
   person key HMAC'd under a per-tenant salt, and make global scope an explicit
   node-operator opt-in per operation. **Settle this before step 4.**
1. **D3's uniqueness check is itself a side channel, and D2 does not stop it.**
   · default: assume deny/allow outcomes are consumer-visible · to reverse:
   an eligibility denial ("this person already claimed via another avatar")
   tells one tenant its user exists on another — the exact correlation D2
   forbids. Denials must be made **indistinguishable from ordinary
   ineligibility** in consumer-visible responses, and `KycIdentity` must live in
   a table structurally excluded from `Search`/`HolonQuery` (this repo has leaked
   cross-tenant through generic read surfaces twice).
2. **Escrow/Split/Payout/Pledge/Stipend cannot simply layer on the existing
   primitives** · default: **REFUTED by review** — the existing Grant/Transfer/
   Refund handlers are NFT-by-Guid moves (D7) and none of them can *hold*, which
   escrow requires (D14) · to reverse: cheap now, very expensive after the
   builder UI is designed around the current 43-node catalog.
3. **The fiat chargeback reversibility window is ~120 days.** · default: my
   estimate for card schemes, **not verified against Stripe's docs or your
   acquirer's terms** · to reverse: cheap — it is a config constant. *Demoted
   from #0 on review:* the window's value matters far less than the structural
   absence of reversal handling, which D10 now addresses. A perfectly-verified
   window still does not stop a post-release dispute.
2. **Soulbound credential issuance is not simply `NftMint`.** · default: assume
   ArdaNova's BRONZE→DIAMOND credentials need a distinct non-transferable node
   with a revoke path (clawback-revoke is a known deferred item) · to reverse:
   re-model the credential lifecycle after ArdaNova has minted live credentials.
3. **Taawun's riba-free constraint forbids any interest-bearing primitive in
   AZOA flows.** · default: no time-value/interest node is introduced; Qard
   Hasan is modelled as principal-only · to reverse: would require scholar
   review, not just code.
4. **Identomat has one set of base URLs (no separate sandbox host).** · default:
   drive environment separation by key + `configId`, not by URL · to reverse:
   config-shape change across appsettings + deploy secrets.
5. **The new JSON `begin` endpoint and the legacy `result/` endpoint live on
   different hosts** (`external-api.identomat.com` vs
   `widget.identomat.com/external-api/`) · default: implement both base URLs as
   separate configured values · to reverse: minor, but silently wrong if assumed
   to be one host.
6. **`clientUserId` is the avatar↔person binding carrier.** · default: send the
   AZOA avatar id · to reverse: re-issue sessions; existing sessions lose their
   binding.
7. **Integration + SDK suites are green.** · default: assumed from last known
   state, **not verified this session** · to reverse: run them before trusting
   any "done".

---

## 5. Environment & gotchas

- **Branch `main`, clean** except a deleted `.config/dotnet-tools.json` (`D`,
  unstaged). Decide whether that deletion is intended before committing.
- **Local stack:** `./dev-up.sh` / `dev-up.ps1`. Frontend :3000, WebAPI :5000,
  SurrealDB :8000. Full procedures live in the **root** [`RUNBOOK.md`](../RUNBOOK.md).
- **Podman/WSL gotchas** (recurring, see `dev-stack-live-run-gotchas`): the WSL
  socket dies → `wsl --shutdown`; a Flask process squats port 5000; you **must**
  rebuild the image after schema/backend changes or the API crash-loops.
- **Do not run frontend `tsc`** — pre-existing noise, per `no-frontend-typecheck`.
- **Identomat credential names** (values live in the host secret store, never in
  repo): `Kyc__Identomat__CompanyKey`, `Kyc__Identomat__ConfigId`,
  `Kyc__Identomat__CallbackApiKey`.
- **Identomat rate limit: 600 requests/minute per IP; exceeding it blocks the IP
  for one hour** with HTTP 529. This is the single most expensive gotcha in the
  KYC workstream — a naive status-polling loop will lock the production node out
  of its own KYC provider. Callback-driven with bounded backoff only.
- **Identomat callbacks authenticate with a shared secret carried in the JSON
  body** (`callbackApiKey`), not a signature header. Compare in constant time,
  rate-limit the endpoint independently, and never echo the value into logs or
  error responses.
- **Research already captured** — do not re-fetch:
  `.omc/research/identomat-kyc-api-2026-08-20.md` (endpoints, exact field names,
  callback shape, limits, design consequences).

### Adversarial note on D1 (asked for explicitly)

The case *for* one-person-one-avatar is Sybil resistance: if KYC gates value,
one human minting N verified avatars multiplies every per-identity allocation,
airdrop, quota, and vote — and AML regimes prefer one traceable natural person.

That case loses here, for a reason specific to this architecture: **the stated
goal is two consumers each driving their own avatars.** A single human who uses
both ArdaNova and Taawun would, under a uniqueness rule, be forced onto one
shared avatar spanning two tenants — collapsing exactly the isolation boundary
that the cross-tenant sweep and consent-gate work exist to defend. Uniqueness
would also make the person key a cross-tenant correlation oracle.

So the Sybil concern is real but is solved at the **wrong layer** by a global
constraint. D3 moves it to the operations that actually need it, which is both
safer and more flexible. The residual risk to accept: any *new* value operation
that implicitly assumes one-avatar-per-human is now wrong by default, and must
opt in to the person-key check. Flag that in review of every new Tier-2 node.

---

## 6. Key files

**KYC seam**
- `Interfaces/Providers/IKycProviderService.cs` — the 6-method interface an
  Identomat adapter must implement.
- `Providers/Kyc/GenericHostedKycProviderService.cs` — the fail-closed scaffold;
  read it to see the shape, **do not try to configure it into Identomat**.
- `Services/Kyc/KycGateService.cs` — validates the latest ledger entry against
  the operator trust profile.
- `Services/Kyc/ValueAccessService.cs` — the gate consumed by the three managers.
- `Managers/KycControlPlaneManager.cs:347` — carries a nullable-deref warning.

**Providers**
- `Interfaces/IBlockchainProvider.cs` — the full surface (balance, mint, burn,
  transfer, exchange, swap, metadata, tx status/confirmation, deploy/call,
  chain info, and the four bridge primitives + `SupportsBridging`).
- `Providers/Blockchain/BlockchainProviderFactory.cs` — registration + the
  Production simulated-mode boot guard.
- `Providers/Blockchain/Algorand/AlgorandProvider.cs` — the reference-quality
  implementation; copy its `BuildSignSubmitAsync` discipline.
- `Providers/Blockchain/Solana/SolanaTransactionSigner.cs` — **already real**.
- `Providers/Blockchain/AGENTS.md` — authoritative capability ledger, but see
  the doc-drift item in §7 step 2.

**Quest / DAG**
- `Services/Quest/Handlers/` — 43 handlers; 7 chain-gated.
- `Services/Quest/ChainCapabilityGate.cs` — Tier-1/Tier-2 enforcement.
- `Interfaces/Quest/IQuestNodeHandler.cs:30` — `RequiresChainCapability`.

**Fiat**
- `fiat-sidecar/src/routes/checkout.ts`, `webhook.ts` — fiat **in** only.
- `sdk/azoa-wallet/src/orchestration/stripe.ts` — the shipped orchestration
  helper.

**Frontend builder**
- `frontend/src/components/quest-builder/quest-canvas.tsx` — **1109 lines**, the
  decomposition target.
- `run-panel.tsx` (484), `presets.ts` (539), `node-output-schema.ts` (154),
  `node-catalog.ts` (133), `dag-flow.tsx` (129), `quest-node.tsx` (85).
- Uses `@xyflow/react` ^12.11.0.

---

## 7. Continuation plan

> **⚠ Phases 0, 1, 1B, 2 and 4 (steps 19/21/22) SHIPPED on 2026-08-21.** They are
> preserved below as the historical record and for their rationale — do not
> re-execute them. Verified state is in §2; verdicts in the review ledger.
> **Start at "Next session" immediately below.**

### Next session — ordered by dependency (set 2026-08-22)

Step A is executable with no further investigation.

**A. Delete and regenerate the live Ethereum wallet rows (D29, urgent).**
Independent of everything else and should not wait behind the rename. Their
stored phrase leaks ~99 bits of the private scalar — treat as compromised, not
broken. Query the live Railway database for Ethereum wallet rows first; if the
count is zero this is a no-op and worth five minutes to confirm.

**B. The terminology sweep (D28)** — `Avatar` → `User`, `Quest` → `Action`,
neutralize religious vocabulary in AZOA's own primitives, strip residual OASIS
language. Do this **before** the schema apply, because it renames tables and
doing the apply twice is pure risk.
*Order within the sweep:* C# models + managers → regenerate SurrealDB schemas
via the documented golden path (never hand-edit `.surql`) → REST routes →
TypeScript SDK → frontend. The SDK must be rebuilt (`tsup`) or the frontend
cannot resolve the renamed types — see `sdk-renamed-oasis-sdk`.
⚠ `KycIdentity.avatar_ids`, `hosted_session`'s admission key, and the
`(tenant_scope, provider, subject)` index all carry the old noun. Renaming a
column that participates in a UNIQUE index needs the forward-migration
treatment, not just a golden regeneration.

**C. One combined deployment reconciliation (D29)** — after B. Apply the new
tables, the `operation_log.amount` forward migration, and the rename migration
together. Verify the migration's widen → convert → pin sequence against the live
database before running it; `CompatibilityBaselines/` is what a deployment is
materialized from, **not** `Generated/Schemas/`.

**D. Production KMS/HSM custody (D27)** — the gate on all of Phase 3. Plug into
`Services/Custody/PlatformKeyDerivationRegistry`; it replaces the *derivation*,
not the chokepoint. Escrow also needs a **hold**, which exists on neither path
today — scope that explicitly rather than discovering it at step 20.

**E. Phase 5 consumer primitives (steps 24–27)** — unblocked; step 27's
dependency landed with USD-at-time stamping. Renamed per D28: step 26 is a
**principal-only loan tracker**. Steps 25/26 are pure-DB and carry no custody
exposure, so they can run in parallel with D.

**F. Phase 3 (steps 12a, 14–20)** — gated on D. Start with the 12a design
session, not a fan-out.

**Also outstanding, unscheduled:** run the integration + SDK vitest suites
(status genuinely unknown, never run 2026-08-21); `Kyc:Identomat:ReplicaCount`
must be raised before any horizontal scale-out; `Program.cs:1378-1382` carries a
comment describing a Wormhole config flag that no longer exists.

---

### Phase 0 — Honesty fixes (small, unblocks trust in the capability surface)

1. **Remove the Ethereum false capability.** In `appsettings.json:199-213`, the
   `Blockchain:Chains` entry for `Ethereum` advertises devnet/testnet
   `IsEnabled: true` while no `EthereumProvider` exists
   (`Program.cs:1091-1093` registers only Algorand, Solana, Simulated), so
   resolution throws `BlockchainProviderNotFoundException`. Add a boot guard that
   **compares config against the registry** — not one that relies on
   enumeration, because `GetAllEnabledProviders`
   (`BlockchainProviderFactory.cs:132`) *silently skips* unregistered chains, so
   the defect is invisible on that path.
2. **Fix the ONE stale provider doc.** *(Corrected on review — the earlier
   instruction to fix "both" files was wrong.)* Only
   `Providers/Blockchain/AGENTS.md:94-95` calls `SolanaTransactionSigner` a
   *"fail-loud stub"*, and it contradicts its own line 115 which cites "the
   shipped `SolanaTransactionSigner`". `Providers/Blockchain/Solana/AGENTS.md` is
   **already correct** — leave it alone. Fix the parent to say the **signer is
   real and the provider pipeline is what is missing**.
3. **Reconcile the warning baseline** — 40 now vs 28 recorded. Fix the delta or
   re-baseline explicitly in `build-warning-baseline-2026-06-16`.

### Phase 1 — Identomat KYC (independent of all chain work; do it first)

Read `.omc/research/identomat-kyc-api-2026-08-20.md` before starting.

4. **Add the identity-binding persistence model.** A `KycIdentity` record
   holding: salted hash of `person.document_number` + `person.issuing_state`,
   plus minimal retained fields (`first_name`, `last_name`, `birthday`,
   `citizenship`). Per **D1** there is **no** unique constraint on the person key
   across avatars; per **D2** the person→avatars edge is node-operator-only and
   must not be exposed on any consumer-reachable read. Do **not** persist
   address, `personal_number`, or any media.
5. **Implement `Providers/Kyc/IdentomatKycProviderService.cs`** against
   `IKycProviderService`:
   - `BeginSessionAsync` → `POST https://external-api.identomat.com/begin` with
     `companyKey`, `configId`, `clientUserId` = avatar id; return the `url` as
     the hosted redirect and persist `id` / `additionalSessionId`.
   - `GetSessionStatusAsync` → `POST https://widget.identomat.com/external-api/result/`
     with `company_key` + `session_token`. **Bounded backoff only** — see the
     600/min IP cap in §5.
   - `HandleWebhookAsync` → constant-time compare the body's `callbackApiKey`,
     then **discard the payload's `result` and re-fetch** `/result/` (D8).
     Map `approved`/`rejected`; treat `MANUAL_CHECK` as **non-terminal** so it
     can never satisfy the value gate.
   - `GetCapabilities` → `Available` only when every key is configured over
     HTTPS; otherwise report the missing key **names**, never values.
6. **Add the callback endpoint** with its own rate limit, no secret echo in logs
   or error bodies, and CAS-based terminal-state writes so redelivered callbacks
   converge on one write (Identomat documents **no** dedup or retry semantics —
   idempotency is entirely AZOA's job).
7. **Register + gate**: add to the provider registry, add `Kyc:Identomat:*`
   settings, and make Production **fail startup** when `Kyc:Provider` resolves to
   manual/mock/unavailable (production-readiness FR-5 AC3).

### Phase 1B — Google + GitHub sign-in (D17, D18)

Independent of chains and fiat; it only touches identity. Can run in parallel
with Phase 2. Today this is **entirely unimplemented** — two decorative buttons
and no backend scheme.

7a. **Backend OAuth schemes.** Register Google + GitHub handlers alongside the
    existing JwtBearer/ApiKey/CredentialFree schemes (`Program.cs:162-167`).
    Add `GET /auth/oauth/{provider}/start` and
    `GET /auth/oauth/{provider}/callback` per D18 — `state` + PKCE, client
    secrets server-side only, callback issues **the same AZOA JWT the password
    path issues**. Client ids/secrets are host-managed secrets:
    `Auth__Google__ClientId|ClientSecret`, `Auth__GitHub__ClientId|ClientSecret`.

7b. **`AvatarExternalLogin` binding** — `(provider, subject) → avatarId`, unique
    on `(provider, subject)`, **subject never the email**, bound **per tenant**
    (D17). Three rules that are security-critical, not polish:
    - **No auto-link by email** — linking requires an authenticated session and
      an explicit user action (account pre-hijacking vector).
    - **OAuth never satisfies KYC** — it must not touch the value gate.
    - A revoked/deleted provider account must not orphan the avatar.

7c. **Frontend wiring.** Give the existing buttons at
    `frontend/src/app/(auth)/register/page.tsx:139-155` real handlers, **and add
    them to the login page**, which has none today. Handle the callback exchange
    and the "link a provider to my existing account" path.

### Phase 2 — Providers with full bridge (D4, D5)

8. **Introduce `IValueRailProvider` and design the bridge strategy against it —
   before writing any chain code.** Two moves in one step, because the second
   depends on the first (D9, D12):
   - Extract `IValueRailProvider` (move value, balance, operation status,
     `SettlementProfile`) and re-parent `IBlockchainProvider` onto it, leaving
     chain-only members (`DeployContract`, `CallContract`, wrapped mint/burn,
     faucet, token metadata) on the chain interface.
   - **Fix the amount type while the interface is open (D13):** `ulong`
     overflows at ~18.4 ETH in wei, so Ethereum cannot be represented; add the
     **currency** dimension fiat needs in the same change (pinned to `USD` per
     D15, but present in the type so a second currency is not a schema break).
   - **Stamp USD-value-at-transaction-time on the operation record (D16).**
     Step 27's analytics surface depends on it, and historical price
     reconstruction after the fact is lossy — this is cheap now, costly later.
   - Build the **cross-chain** strategy seam on the two generic axes in D22
     (NOT cross-rail — D12 is deleted): bridge legs as capability modules on the
     existing `TryGetModule<T>` seam, plus `IBridgeProtocol<TProof>` owning the
     lifecycle. Lifecycle, ordering, idempotency, reconcile-before-retry and
     fail-closed rules live once, in the orchestrator.
   - Make `Services/Quest/ChainCapabilityGate.cs` **rail-aware** (D9): today it
     checks "actor has ANY wallet bound," which passes a chain-wallet holder for
     a fiat payout and *fails* a fiat-only user.

   **Dependency correction (from review):** steps 9 and 10 do **not** depend on
   this — re-parenting an interface does not change a concrete provider's
   build→sign→submit work, so they may run in parallel with step 8. Only step 11
   (bridge completion) and Phase 3 are genuinely gated on it. Sweep the hardcoded
   `ChainNetwork.Devnet` at `Managers/FungibleTokenManager.cs:236` and
   `Managers/QuestManager.cs:2397` as part of this step.
9. **Build `EthereumProvider` on Nethereum** — full `IBlockchainProvider`
   surface, signing behind `ITransactionSigner`, mirroring `AlgorandProvider`'s
   build→sign→submit discipline (non-retrying broadcast, pending marker on
   confirm timeout, real tx hash, never a synthetic id).
10. **Build the Solana SPL pipeline** — mint/burn/transfer build→submit against
    the already-real signer. The canonical byte contract is specified in
    `Providers/Blockchain/AGENTS.md` §"Solana canonical-byte contract"; the
    provider must produce the serialized **message** (not a full transaction) and
    submit the signer's returned bytes unmodified.
11. **Complete the Algorand bridge** — canonical platform-controlled ASA
    creation, recipient opt-in, then transfer; prove platform ownership of full
    supply before destroy; prove vault control by the release signer, with the
    four focused tests the provider's `AGENTS.md` demands.

    Under D22 this lands as **module opt-in, not a flag flip**: implement
    `IBridgeSourceLeg` on `AlgorandProvider` once lock+release are proven, and
    `IBridgeTargetLeg` once wrapped mint/burn are proven. **Delete
    `SupportsBridging` and the three hand-written refusal bodies** rather than
    setting a bool true. Note the asymmetry this finally makes expressible:
    Algorand's lock genuinely works today while the other three primitives do
    not, so it can ship as a **source-only** chain — which the single
    `SupportsBridging` bool cannot represent, and which is why its real
    capability is currently suppressed entirely.
12. **Build the provider-boundary conformance harness** (currently 0/38) and run
    every chain through it. No route may be enabled without passing.

### Phase 3 — Fiat as a value rail (D7, D9–D11, D13, D14) — gated on step 8

> **Phase 3 needs its own design step before any code.** The review established
> that the original "drop-in rail" framing was false (D7). Do step 12a first.

12a. **Design session — re-derive what fiat actually requires**, with
    `BlockchainProviderFactory.cs`, `ChainCapabilityGate.cs`, and the four Tier-2
    handler configs (`Transfer`, `Grant`, `Refund`, `Swap`) open side by side.
    Decide: rail-aware factory shape; fiat destination/wallet model; currency
    representation; and whether fiat gets its own node types or the value model
    is refactored. **Do not skip this** — every remaining step and the Phase 4
    palette are specified against it.

13. **Resolve custody (D14)** — either implement KMS/HSM-backed custody, or
    record an explicit gated decision that dev-grade custody is acceptable for
    the first real-value milestone. Escrow needs a **hold**, which does not exist
    today on either path (TEAL deploy/call unimplemented; `CustodyMode =
    "Disabled"`). Steps 14–17 move real money and cannot honestly precede this.

14. **Build the reversal-event pipeline (D10)** — this is the money-printer fix
    and it precedes escrow. Sidecar forwards `charge.dispute.created`,
    `charge.refunded`, `charge.dispute.funds_withdrawn`; AZOA records them as
    first-class ledger facts and runs a **compensation saga**. Today
    `sdk/azoa-wallet/src/orchestration/stripe.ts:159-160` discards all of them.
    Include partial refunds and multi-recipient clawback.

15. **Implement `FiatRailProvider`** per the 12a design. It calls `fiat-sidecar/`
    over HTTP and **must not** hold any Stripe credential (contract §3). Declare
    a `SettlementProfile` carrying the reversibility window, not a finality
    claim.

16. **Extend `fiat-sidecar/`** with **both** a payout route and a **status-read
    route**. Today it has only `checkout` + `webhook` — so the
    "inherits reconcile-before-retry" claim is empty until something exists for
    the reconcile loop to call. Keep the rule that AZOA receives already-decided
    amounts and never computes economics.

19. **Wire fiat-in onto the `HostedSessionFlow` seam produced in Phase 1** (D11):
    begin checkout → hosted redirect → callback → authoritative server-side
    re-fetch → resume the suspended run. If Phase 1 did not produce that seam,
    fix Phase 1 rather than writing the pattern a second time.

20. **Add the missing econ nodes:** **Escrow** (hold → release on confirmation →
    refund on ambiguity — Taawun's anti-gharar requirement), **Split** (atomic
    multi-recipient), **Stipend/Pledge**. Three rules bind all of them:
    - **Escrow release reads `SettlementProfile`** *and* relies on step 14's
      compensation path for reversal-after-release. A release-time check alone
      does not close the hole — it relocates it.
    - Any node assuming one-human-one-claim must opt in explicitly to the
      person-key check at the scope settled in §4 assumption 0.
    - **Riba check:** holding escrowed fiat or chargeback reserves generates
      float. If any of it accrues to the platform inside a Taawun flow, that is
      interest-adjacent and needs the scholar review deferred in assumption 5.
      D10's own mitigation (holding funds longer) *creates* this question.

### Phase 4 — Frontend builder (D6, all four axes)

19. **Decompose `quest-canvas.tsx` (1109 lines)** into hooks + panels first —
    every later builder change is cheaper afterwards, and steps 20–23 all touch
    it.
20. **Rail + capability clarity** — distinguish the 7 chain-gated nodes from the
    37 pure-DB ones, and show **which rail** a value node will settle on (chain
    vs fiat) together with its reversibility window from `SettlementProfile`.
    This is what lets Taawun build Zakat/donation/Qard-Hasan flows entirely
    chain-free, and it surfaces the D10 hazard at authoring time.
21. **Authoring UX** — surface `QuestDagExecutabilityValidator` errors inline
    rather than only at publish, node search/palette, undo/redo, subgraph copy.
22. **Run observability** — extend `RunPanel` with live per-node state, output
    inspection, and retry/advance/signal affordances.
23. **Reframe the palette around econ intent, not chain primitives.** Both
    consumers' end users *"should hardly feel like they are interacting with
    blockchain surfaces"*, so the builder should lead with Escrow / Split /
    Payout / Pledge / Credential and treat rail, mint, transfer and bridge as
    the implementation detail beneath them. D7/D9 make this honest rather than
    cosmetic: fiat and chain really are one dispatch path underneath.

---

### Phase 5 — Consumer primitives (closes the ArdaNova/Taawun goal gaps)

These are what the two named consumers need that the platform work above does
not deliver. 24 and 27 are small; 25 and 26 are mostly pure-DB.

24. **Soulbound revocation (D19)** — implement the Algorand clawback transfer
    (`AssetSender` set, signed by the clawback address already configured at
    `FungibleTokenManager.cs:155`), expose a `CredentialRevoke` node + route
    authorized to the **issuer**, and unblock `RefundNodeHandler.cs:26-34`.
    Unblocks ArdaNova's credential tiers, which can currently only promote.

25. **Governance / voting (D20)** — proposal-as-Holon with typed vote children,
    tally in a **Tier-1 pure-DB node** (no chain capability, no custody). Add an
    optional on-chain **tally-hash anchor** at close for public verifiability —
    one transaction per proposal, never per vote.

26. **Qard Hasan tracker (D21)** — loan Holon + disbursement/repayment ledger
    reusing existing Grant/Transfer for the value moves, balance projection in a
    pure-DB node. Partial repayment and write-off are ordinary states. **No
    interest accrual node, ever.**

27. **DEX-style analytics surface (D16)** — normalized transaction/position
    history per avatar and date range (timestamp, type, asset, amount,
    counterparty, **USD value at time**, tx ref, fee) plus realized/unrealized
    position primitives, so consumers build their own tax workflows. **Depends
    on step 8 having stamped USD-at-time on the operation record** — if that was
    missed, this step cannot be built correctly and step 8 must be revisited.

---

### Not yet scheduled — gaps the review named that no step covers

These are real and would bite in production. None has an owner yet; decide
whether each becomes a step, a track, or an accepted risk.

- **AML / sanctions screening — entirely absent.** No step screens payout
  recipients (OFAC), and no per-person transaction monitoring exists *despite the
  person key being built precisely to enable aggregation*. Identomat exposes
  AML screening endpoints (`search-screening-person`,
  `monitor-screening-query`, …) that the research capture never fetched. For a
  node moving real USD this is not optional.
- **Per-tenant KYC quotas — a cross-tenant DoS.** The Identomat limit is 600
  req/min **per IP** with a 1-hour ban. The plan bounds AZOA's *own* polling but
  not consumer-triggered volume, so one consumer's begin-session storm locks
  **every** tenant out of KYC.
- **PII retention / erasure.** Step 4 persists name, birthday, citizenship and a
  document-derived hash with no retention period, no erasure path (GDPR Art. 17
  vs KYC record-keeping tension), and no at-rest encryption statement.
- **RESOLVED by D15/D16 — no longer listed here:** multi-currency/FX (USD only)
  and tax reporting (replaced by the step-27 analytics surface, which gives
  consumers the facts to run their own workflows).
- **`read balance` is still an abstraction mismatch for fiat** — Stripe exposes
  the *platform* balance, not a payer balance. Resolve in step 12a.
- **Operator funding / treasury.** Algorand fee-payer balances, Stripe payout
  balance sufficiency, and reconciling `NodeTreasuryManager` holdings against
  outstanding obligations appear nowhere.

---

## 8. Session log

### 2026-08-21 — Execution session (Phases 0, 1, 2, 4)

**Shipped:** Phase 0 (honesty fixes) · Phase 1 (Identomat KYC + AML + `KycIdentity` +
`HostedSessionFlow`) · Phase 2 step 9 (EthereumProvider on Nethereum, 85 tests) ·
step 10 (Solana SPL, 24 tests) · chain-aware custody door (67 tests) ·
hosted-KYC unblock · Phase 4 steps 19/21/22 · first-ever `frontend/` test harness.
Backend peaked at **2145 passed / 1 skipped / 0 failed** before the step-8 refactor
opened the tree.

**Decisions settled:** D23–D26 (see §3).

#### ⚠ ACTION REQUIRED — pre-existing Ethereum wallet rows are hazardous

`Services/Signing/WalletKeyService.GenerateEthereumKeypair()` was a stub. Two
independent defects, and they were **coupled**:

1. The stored `wallet.Address` was derived with **HMAC-SHA256 + SHA-256**, not
   secp256k1 + keccak-256 — so the address was unrelated to the private key.
2. The "recovery phrase" was `idx = (seed[i]*256 + seed[i+1]) % 2048`. Because
   **2048 = 2¹¹**, each word discloses byte `seed[i+1]` in full plus the low 3
   bits of `seed[i]` — the 12 words leak **~99 bits of the private scalar** to
   anyone who sees the "backup", and restore the wallet in no wallet on earth.

Defect 2 was inert only while defect 1 made the address fake. **Fixing the
address alone would have turned a broken stub into a live key-disclosure
channel.** Both are now fixed (phrase returns `null`; derivation shares the exact
`EthECKey` call the provider and signer use, so keygen cannot drift from the
value path). Verified against published vectors — scalar `1` reproducing the
SEC 2 §2.4.3 generator point, which is what proves real point multiplication
rather than a hash.

**Any Ethereum wallet row created before this change must be deleted and
regenerated, not migrated** — its address does not match its key and its stored
phrase leaks that key. Pre-launch, so deletion is acceptable; see
`greenfield-prelaunch-no-compat`.

Related follow-ups, not yet done: `Managers/WalletAuthManager.cs:564` and
`Providers/Stores/Surreal/SurrealWalletAuthChallengeStore.cs:109,176` compare
addresses with `StringComparison.Ordinal` / raw SQL equality. Stored Ethereum
addresses are now mixed-case **EIP-55** where they were previously lowercase.
Nothing breaks today (those are external-wallet auth paths) but they should
normalize through `EthereumAddress.TryParse`.

#### ⚠ The frontend lesson — green gates proved nothing

Phase 4 steps 19/21/22 passed `tsc`, `eslint`, and `npm run build` — all three
green — and **the quest builder did not run at all**. An adversarial review built
a jsdom harness against the real `@xyflow/react` 12.11.0 and measured:

- **Infinite render loop on mount.** `onSelectionChange` was an inline arrow, and
  React Flow's `SelectionListenerInner` puts that callback in its effect deps
  while `<ReactFlow>` is `fixedForwardRef`, **not `memo`**. 301 parent renders +
  "Maximum update depth exceeded", **with and without StrictMode**.
- **Copy/paste was dead code.** A controlled-selection projection overwrote
  `.selected` from a list fed only by the event fed only by that same prop — a
  closed loop seeded at `[]`. Copy permanently disabled, no node ever showed a
  selection ring.

Root cause of *both* escaping: `frontend/` had **zero test dependencies**, so
nothing ever executed these components. A vitest + jsdom + testing-library
harness now exists (also closes a standing user request from
`open-requests-2026-06-16`). **Regression tests must be written to fail first** —
a test that never failed is precisely how these shipped.

Corrected in the same pass: the long-standing "never run frontend typecheck"
rule was **obsolete** — tsc is clean at this commit and is now a required gate,
not a forbidden one.

#### Pattern worth carrying forward

Every agent found real defects *outside* its brief, because each was told to
report contradictions rather than trust the brief. Highest-value examples: three
fabricated-success bugs on Solana value paths (including an unknown transaction
reported as `CONFIRMED` on the reconcile path); a `Providers/Kyc/AGENTS.md` claim
that would have leaked vendor session ids to tenants if trusted; a transient 503
that would have permanently denied a subject KYC; a custody chokepoint throwing
SDK exceptions that echo mnemonic words. **Briefs should keep asking "tell me
what here is stale against the real source."**

#### Two near-misses worth remembering

**The idempotency-key rename that almost broke replay protection.** `RailAmount`
replaced `ulong` on the Mint path, and `DeriveIdempotencyKey` read `.Amount
.ToString()`. `RailAmount.ToString()` renders `"1000000 base units"` — the
obvious mechanical port would have silently re-hashed **every** key, so an
operation already claimed under the old spelling would stop matching itself.
It now reads `.ToBaseUnitString()`, pinned by goldens computed from the
*pre-change specification in a separate script*, not by capturing what the new
build emits.

**Regenerating the schema golden was not sufficient.** A deployed database is
materialized from the **immutable `CompatibilityBaselines/`** plus forward
migrations, not from `Generated/Schemas/`. `CompatibilityBaselines/operation_log
.surql` still declared `amount TYPE option<int>` (SurrealDB `int` is i64 — half
what the interface already accepted), so shipping only the regenerated golden
would have left every deployment rejecting the widened column at the SCHEMAFULL
boundary. A forward migration now widens → converts → pins, and a test pins the
migration against both the baseline and the golden so this drift class fails
next time.

#### What remains, and why

- **Phase 3 (fiat)** — starts with design step 12a, and step 13 (custody) is a
  genuine decision, not a task: either implement KMS/HSM-backed custody or
  record an explicit gated acceptance of dev-grade custody for the first
  real-value milestone. `CustodialAccounts:CustodyMode` is still `"Disabled"` and
  no production KMS/HSM adapter exists. Steps 14–17 move real money and cannot
  honestly precede that call. **This is the owner's decision.**
- **Phase 5 (consumer primitives, steps 24–27)** — now unblocked. Step 27's
  dependency is satisfied: USD-at-time stamping landed on `operation_log` this
  session, with `IValueRateSource` left as a deliberately unwired seam (there is
  no price oracle in this codebase, and an absent rate is stored as explicitly
  null, distinguishable from a genuine zero).
- **Phase 4 steps 20 + 23** — need Phase 3's econ nodes to exist before the
  palette can be reframed around them. Gated, not skipped.
- **Still unowned from §7's "not yet scheduled"**: per-tenant KYC quota is now
  implemented, but **operator funding/treasury reconciliation** and the
  `read balance`-for-fiat abstraction mismatch remain open for step 12a.

### 2026-08-20 18:40 — Readiness assessment + three-workstream scoping

- **Done:** Evidence-based completeness assessment (build 0 errors / 1823 unit
  tests green / frontend audit clean). Enumerated real-vs-stubbed state for all
  chains by reading provider source. Confirmed **no** working KYC adapter exists
  and Identomat has zero code. Researched the Identomat API across 4 doc pages
  and captured it to `.omc/research/identomat-kyc-api-2026-08-20.md`. Reviewed
  ArdaNova + Taawun econ surfaces and user types.
- **Reviewed:** Assessment claims verified against source, not docs — found 3
  doc-drift defects and 1 false-capability defect (Ethereum).
- **In flight:** nothing — no code was changed this session.
- **Blocked:** real value on every chain, by custody + KYC + conformance.
- **Amended same session:** fiat re-scoped from bespoke node types to a **value
  rail reached through provider logic** (D7 revised; D9–D12 added). Verified the
  two facts that decide it: `fiat-sidecar/src/azoa.ts` holds the Stripe secrets
  (so AZOA must reach fiat *through the sidecar*, per contract §3), and no rail
  abstraction exists yet. Step 8 grew to include `IValueRailProvider`; Phase 3
  now depends on it; Phase 4 renumbered 17–21.
- **Reviewed (2nd pass, same session):** fable adversarial critic on the whole
  plan. Verdict **not safe to execute through Phases 2–4 as written**. Its
  central refutation was verified independently before acceptance:
  `TransferNodeHandler.cs:31` moves an NFT by Guid (not amount+currency+dest);
  `BlockchainProviderFactory.cs:31,41` is typed `Func<IBlockchainProvider>` so a
  non-chain rail **cannot register**; `stripe.ts:159-160` discards every dispute
  event; handler count is 43 not 44. All confirmed. Plan corrected accordingly —
  D7's "zero new node types" retracted, D12 deleted, D13/D14 added, Phase 3
  gained a design step (12a) plus custody (13) and reversal-pipeline (14) steps.
- **Amended (3rd pass, same session) — consumer gap-analysis:** verified against
  source that soulbound **minting** exists (`AlgorandProvider.cs:901`) but
  **revocation** does not (`RefundNodeHandler.cs:26-34`); the Tinyman DEX adapter
  **is real** (318 ln, live HTTP) so the swap leg works via
  `SwapNodeHandler → ISwapManager`; there is **no** governance/voting node type;
  and OAuth is unimplemented front to back (decorative buttons at
  `register/page.tsx:139-155`, no backend scheme at `Program.cs:162-167`).
  Scope pinned to **USD only** and **analytics-not-tax**. Added D15–D21,
  Phase 1B (sign-in) and Phase 5 (consumer primitives, steps 24–27).
- **Amended (4th pass, same session) — bridge generics.** Verified the capability
  seam already exists and is in use (`BlockchainProviderFactory.cs:142`;
  `AlgorandProvider.cs:31` and `SolanaProvider.cs:15` declare modules). Added
  **D22**: bridge generic over capability + protocol, never over provider type
  (the chain pair is runtime data, so provider-typed generics would need
  reflection). Folded into step 8 (seam) and step 11, which now lands as
  **module opt-in with `SupportsBridging` deleted**, letting Algorand ship
  source-only.
- **Open scope question, not yet settled:** ArdaNova is Algorand-native and
  Taawun's product doc names no chain at all — so Phase 2 steps 9/10/11
  (Ethereum provider, Solana SPL, full bridge) may serve **neither** named
  consumer. Confirm Taawun's chain needs before step 8; Algorand + fiat +
  the 37 pure-DB nodes may cover both, which would be the single largest
  available scope reduction.
- **Next:** §7 step 1. Then settle §4 assumption 0 (uniqueness scope) before
  step 4, and do step 12a before any Phase 3 code.

---

## 9. Recommended invocations

Lowest rung that does the job, each with the clause that selected it.

1. **Phase 0 (steps 1–3): inline edits.** Three small, well-specified changes in
   files already identified — no orchestration warranted.
2. **Phase 1 (steps 4–7): one `oh-my-claudecode:executor` (opus).** Single
   coherent adapter across ~4 files with an external API contract already
   researched — one agent holding the whole contract beats a fan-out.
3. **`/security-review` after step 7 — non-negotiable.** The KYC callback is an
   unauthenticated-by-default inbound endpoint carrying a body-borne shared
   secret on an identity path; the standing rule requires a security review on
   auth/user-input/secrets work.
4. **Phase 2 (steps 8–12): `/slice`.** The three provider directories are
   naturally disjoint (`Ethereum/`, `Solana/`, `Algorand/`). Per the review's
   dependency correction, **steps 9 and 10 may run in parallel with step 8** —
   only step 11 (bridge completion) and all of Phase 3 are gated on it. Slicing
   step 11 before step 8 lands would produce divergent bridge implementations.
5. **Phase 3 starts with a design session (12a), not a `/slice`.** The review
   established that the fiat seam is not yet known; fanning out against an
   unknown contract is how the retracted D7 happened in the first place.
6. **`/security-review` after step 16 AND after step 18 — both non-negotiable.**
   Step 16 adds a **payout** route: money *outbound* is the most attack-worthy
   fiat surface and must not ship un-reviewed until step 18. Step 18's escrow
   release across a reversible fiat leg and an irreversible chain leg is the
   money-printer path (D10), a bug class this codebase has shipped twice.
