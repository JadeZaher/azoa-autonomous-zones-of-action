---
type: runbook
---

# AZOA — Session Runbook

**Last updated:** 2026-09-04 · **Branch:** `feat/kyc-aml-providers-value-rail` ·
**Last commit:** `9f4e026 feat: KYC/AML, three chain providers, value-rail seam, OAuth, builder rebuild` (unpushed, 286 files)

> **Two files are named RUNBOOK.md — they are different documents.**
> This one (`conductor/RUNBOOK.md`) is the *session/continuation* runbook: goal,
> state, decisions, and the next agent's plan. The root
> [`RUNBOOK.md`](../RUNBOOK.md) is the *operational* reference (dev-up/down,
> deploy, diagnostics) and is linked from `tracks.md` + `DEVELOPMENT.md`.
> Do not merge them; do not let this file restate ops procedures.

> **Pruned 2026-09-04** (1333 lines → ~620, since grown again). Executed
> step-by-step instructions for the shipped phases were removed; their verdicts
> survive in the §2 review ledger and their lessons in §8. The pre-prune text is
> in git history at `9f4e026:conductor/RUNBOOK.md`.

---

## 1. Goal

Take AZOA from "deployable fail-closed node" to a **single stable node serving two
real application consumers**, each driving its own avatars. Federation is
explicitly **future state** and out of scope.

Workstreams as scoped by the 2026-08-20 grilling gate:

1. **Identomat KYC** — API-driven, backend-handled, certifies an avatar and binds
   it to a real person. **SHIPPED.**
2. **Full E2E provider pattern** — Ethereum, Algorand, Solana including the
   complete bridge lifecycle, plus the pure-DB DAG operation set. **SHIPPED.**
3. **Frontend workflow builder** — all four improvement axes, optimized for the
   two consumers below, whose end users **should barely feel that a blockchain
   is involved.** Steps 19/21/22 shipped; 20/23 gated on Phase 3.
4. **Google + GitHub sign-in** (Phase 1B). **SHIPPED.**
5. **Fiat as a first-class workflow primitive** — pay fiat to prove ownership,
   receive a payout; not an out-of-band sidecar. **Open, gated on custody.**
6. **Consumer primitives** (Phase 5) — soulbound revocation, governance/voting,
   principal-only loan tracking, DEX-style analytics. **Open, unblocked.**

**Scope pinned:** USD only (D15). Finance-focused, not tax/regulator-focused —
AZOA exposes analytics facts and consumers run their own tax workflows (D16).

### The two consumers (these define "done" for the builder)

| | **ArdaNova** (`C:\Users\atooz\Documents\Escherbridge\ardanova`) | **Taawun** (`C:\Users\atooz\.codex\worktrees\6e53\tawun`) |
|---|---|---|
| What it is | Worker-owned cooperative gig platform — "Upwork where work earns equity" | Community app builder (mosques, charities, founders) |
| Stack | Node/JS, game SDK, MCP backend, contracts | Go, Datastar, MCP control plane, CRDT/IndexedDB local-first |
| Econ surfaces | Per-project equity ASAs; ARDA token bridging project tokens → stablecoin **USD payouts**; treasury-backed deterministic rates; Stripe on/off-ramp; soulbound credentials (BRONZE→DIAMOND); task bounties; guild bidding | **Escrow quests** (settle to creator+platform only on buyer confirmation); creator revenue **splits**; donation drives; **principal-only loan** trackers; volunteer stipends |
| Users | Innovator, Contributor, Supporter, Guild Member | Architect, Maintainer, Viewer (congregant/donor) |
| Hard constraint | Equity/valuation math stays in ArdaNova; AZOA receives already-decided amounts | **No interest-bearing primitive** may exist in a flow. **Ambiguity must fail closed and refund** |

Taawun's product doc states *"anything touching money runs on AZOA"* and splits
state into **convergent** (CRDT, theirs) vs **transactional** (balances,
payments, escrows, splits, pledges — **AZOA action graphs**). That split is the
contract the builder must serve.

⚠ **D28 renames the platform's nouns** (`Avatar`→`User`, `Quest`→`Action`). This
table intentionally keeps each consumer's own vocabulary — de-branding applies to
AZOA's primitives, not to how consumers describe themselves.

---

## 2. State

### Verified green — measured 2026-08-21, unchanged at `9f4e026`

| Gate | Result |
|---|---|
| `dotnet build azoa.sln --no-incremental` | **0 errors**, 23 warnings (19 real + 4 MSBuild doc-file notices) |
| `dotnet test tests/AZOA.WebAPI.Tests` | **2431 passed**, 1 skipped, **0 failed** |
| `cd frontend && npx tsc --noEmit` | **exit 0, zero errors** |
| `cd frontend && npm test` | **65 passed**, 11 files |

⚠ **Always use `--no-incremental` when quoting a warning count.** An incremental
build reports 0 simply because nothing recompiled; that mistake was made twice
and propagated into two agent briefs.

**Still not run, status genuinely unknown:** integration tests (need live
SurrealDB) and the SDK vitest suite. Do not report these as green.

### Chain reality (verified by reading provider source, not docs)

| Chain | Provider | State |
|---|---|---|
| **Algorand** | `AlgorandProvider.cs` | Balance/Mint/Burn/Transfer/faucet real, Ed25519 signer real, non-retrying broadcast + pending marker. Bridge: **source-only** (module opt-in per D22). Exchange/Swap need Tinyman/Pact; contract deploy/call need TEAL |
| **Solana** | `SolanaProvider.cs` | Signer real (Solnet); SPL pipeline shipped Phase 2 step 10 (24 tests) |
| **Ethereum** | `EthereumProvider.cs` (1860 ln) | Shipped Phase 2 step 9 on Nethereum (85 tests). Was previously **absent while advertised** — a boot guard now diffs `Blockchain:Chains` against the registry so that class of lie fails a test |
| **Simulated** | `SimulatedBlockchainProvider.cs` | Complete; hard-blocked in Production by `GuardAgainstSimulatedModeInProduction` |

**Pure-DB DAG ops:** 43 node handlers; only **7** set `RequiresChainCapability`
(Back, Bridge, FungibleTokenCreate, Grant, Refund, Swap, Transfer). The other
**37 run chain-free** against SurrealDB. The Tier-1/Tier-2 seam is
`IActionNodeHandler.RequiresChainCapability`, enforced at both dispatch points via
`Services/Actions/ChainCapabilityGate.cs` (now rail-aware, per D9).

### Cross-cutting blockers to real value

1. ~~No KYC adapter works~~ — **CLOSED.** Identomat KYC + AML shipped Phase 1.
2. **No production custody. STILL OPEN — this is the gate on all of Phase 3.**
   `CustodialAccounts:CustodyMode = "Disabled"` in `appsettings.json:207`
   (`"DevelopmentOnly"` in Development). No production KMS/HSM adapter exists.
   Keys remain config-derived. See D27 and §7 step D.
3. ~~Conformance harness does not exist~~ — **CLOSED.** 77 tests, auto-enrolling,
   13 declared exemptions, zero silent skips.

### Review ledger

Load-bearing: D30 chose a single commit, so this table plus §8 is the **only**
map from a regression back to the workstream that caused it.

| Phase | Checkpoint | Findings | Verdict |
|---|---|---|---|
| Readiness assessment (2026-08-20) | evidence-based | 3 doc-drift defects + Ethereum false-capability | **Assessed** — no code changed |
| Identomat API research | 4 doc pages → `.omc/research/identomat-kyc-api-2026-08-20.md` | callback is notification-only; body-borne shared secret; 600 req/min IP cap | **Captured** |
| **Plan review (2026-08-20)** | fable adversarial critic, prompted to refute; 5 claims re-verified against source | **3 CRITICAL** (fiat dispatch path did not exist as specified; D10 did not close the money-printer; custody blocker had no step), 4 HIGH, 3 MEDIUM/LOW | **REFUTED as written** → D7/D9/D10 rewritten, D12 deleted, D13/D14 added |
| **Phase 0 — honesty fixes** | build + unit sweep | boot guard diffing config against the registry (deliberately NOT via `GetAllEnabledProviders`, which silently skips unregistered chains); Ethereum disabled everywhere; 1 nullable-deref fixed | **SHIPPED** — one guard test loads the *real shipped* `appsettings.json`, so re-enabling a chain without a provider fails a test |
| **Phase 4 step 19 — builder decomposition** | opus adversarial review, separate context | `quest-canvas.tsx` 1109 → 118 lines, 20 files. Reviewer diffed warnings logic, every `className`, the full ReactFlow prop set (identical, 17 props, same order). 2 MEDIUM + 5 LOW, no regression | **COULD NOT BREAK IT** |
| **Phase 1 — Identomat KYC + AML** | opus security review, separate context, adversarial | **3 HIGH, 6 MEDIUM, 1 SUSPECTED-HIGH, 3 SUSPECTED-MEDIUM, 3 LOW**; 16 items verified sound | **REFUTED → REMEDIATED.** SUSPECTED-HIGH refuted empirically against live SurrealDB 3.1.4. HIGH-1's twin found in a second gate (`RealValueKycGate`) the review never named |
| **Phase 1B — OAuth** | built to D17/D18 with structural proofs | no-email-linking enforced by *absence of a lookup method*, not a condition; `(tenant_scope, provider, subject)` modelled on `kyc_identity` | **SHIPPED** |
| **Phase 2 step 8 — value-rail seam** | orchestrator-verified | `IValueRailProvider` + `RailAmount` + rail-aware gate + Ethereum DI; fixed a failing test at root rather than relaxing it | **SHIPPED** |
| **Phase 2 steps 9 + 10 — Ethereum, Solana SPL** | per-provider suites (85 + 24 tests) | **3 fabricated-success bugs** found outside brief, incl. an unknown tx reported `CONFIRMED` on the reconcile path | **SHIPPED** |
| **Phase 2 step 11 — bridge modules** | D22 capability seam | `SupportsBridging` deleted repo-wide; Algorand ships **source-only**, which the boolean could not express; every precondition proven against Algod before signing, incl. the rekey check | **SHIPPED** |
| **Phase 2 FR-6 — conformance harness** | 77 tests, auto-enrolling | 13 declared exemptions, zero silent skips; a `NotApplicable` with no compensating control **throws**. VAA fail-open removed — `WormholeConfig` now has zero booleans | **SHIPPED** |
| **Phase 4 steps 21 + 22 — builder** | opus adversarial review that **built a jsdom harness and executed the code** | **2 CRITICAL** — infinite render loop on mount (301 renders, with *and* without StrictMode); copy/paste dead code — plus 1 HIGH and 9 lower | **REFUTED → REMEDIATED** |
| **Phase 4 — governance-vote-of-record, W9.2 implementer pass** | proposal-rewrite + electorate audit, 29 new tests | decisive question ("can a vote that never took a claim still be counted?") answered NO with source evidence; **3 NEW holes** found behind it (proposal rewrite after a ballot exists, node-wide electorate, unaudited node-global scope resolution), all CLOSED | **REFUTED-AS-TOO-NARROW → REMEDIATED**; independent review still owed, the three node types stay in `NotEnabledActionNodeTypes`. Full record with evidence: `Services/Governance/AGENTS.md` §phase-4-verdict |

**Nothing in §7 "Next session" has been reviewed or executed.**

### Three defect *classes* this codebase keeps reproducing

Recorded because each recurred; check for them in every new pass.

1. **"The adapter exists" is not "the gate consults it."** The AML decision was
   INERT — `ScreenAsync`/`RegisterMonitorAsync` had **zero production callers**
   and `KycScreeningResult.Decision` was **read by nothing**, so a sanctioned
   person passing identity verification got full value access with a `HIT` row
   sitting inert in the database. *Always check the read side.*
2. **Every new table with a natural key needs the tenant dimension reviewed
   explicitly — getting it right once does not propagate.**
   `kyc_screening_monitor` was `UNIQUE(query_full_name, query_birthday_time_from)`
   with **no tenant in the key**, so tenant B registering a colliding name+DOB
   received **tenant A's row**. The identity table got this right
   (`(tenant_scope, person_key)`); the screening table, written in the same pass,
   did not. **Third instance of this class.**
3. **A concurrency test that never runs concurrently proves nothing.**
   `ResumeAsync` ran *before* the version CAS in `HostedSessionFlow`, so two
   concurrent deliveries both resumed and one lost silently — leaving
   `hosted_session = FAILED` while the KYC submission read `APPROVED`, opening
   value access against a session the node had recorded as failed. The existing
   idempotency test ran its five redeliveries **sequentially** and passed
   trivially.

**Settled fact, load-bearing for the D28 rename:** a SurrealDB UNIQUE index does
**not** collide `NONE` components — verified empirically against 3.1.4, and
explicit SQL `NULL` is unreachable under `SCHEMAFULL option<T>`. Pinned by
`tests/AZOA.WebAPI.Tests/Kyc/SurrealUniqueIndexNoneSemanticsTests.cs`. The old
contradictory "NULL-equals-NULL collision semantics" wording is corrected
everywhere. This is why `hosted_session`'s nullable `provider_session_id` UNIQUE
cannot brick KYC node-wide.

**Verification bar (changed 2026-08-21, still in force).** The long-standing "do
not run frontend typecheck" rule is **obsolete and has been deleted from §5**:
`npx tsc --noEmit` exits 0 and `npm run build` exits 0. `frontend/` now has a
vitest + jsdom harness (65 tests). tsc + build + eslint + vitest are **required**
gates there, not forbidden ones.

⚠ **Open finding, not closed** (re-verified 2026-09-04):
`CanvasToolbar` — ✅ **CLOSED 2026-09-06 as a NON-ISSUE. The finding was wrong,
not unfixed.** It claimed the toolbar is "ungated by `readOnly`" so Clear /
Presets / Auto-layout "destroy a displayed graph in read-only mode". Verified
against source: **there is no read-only mode for the builder to be ungated in.**
`frontend/src/components/action-builder/AGENTS.md:356-365` records that
`readOnly` was **deliberately removed** because neither `ActionCanvas` call site
ever passed `true` — both (`actions/page.tsx:432` and `:596`) are create-new
flows. The read-only render is a **separate component**, `dag-flow.tsx`, which
has **no toolbar at all**. The removal also promoted `onAddNode` and the
edge-inspector callbacks from optional to **required**, so a caller that forgets
to wire one gets a compile error rather than a silently dead control — stronger
than the gate this finding asked for.
*Lesson:* the earlier note asserted "`AGENTS.md` documented the gate as complete,
which is worse than silence." It did not — it documented the **removal**, and
accurately. **The runbook was the stale document.** Re-read the primary source
before recording another file as wrong.

✅ **Closed since:** the `dag-warnings.ts` OnFailure fan-out gap — the client now
mirrors `Services/ActionDagValidator.cs` (`dag-warnings.ts:103-110`).

---

## 3. Decisions

Settled by the grilling gates. **Do not re-litigate.** Verification forensics that
motivated each decision were pruned once the code embodied them; what remains is
the decision plus the invariant a future session must not break.

### Identity (2026-08-20 / 08-21)

**D1 — One real person MAY hold many verified avatars.** Store a salted hash of
`document_number` + `issuing_state` as the person key; do **not** enforce global
one-person-one-avatar. *Rationale:* the model is "two consumers driven by their
own avatars" — forcing one avatar per human would make a person using both
ArdaNova and Taawun share a single avatar across two tenants, contradicting the
shipped cross-tenant isolation and self-sovereignty work. Compartmentalized
per-dapp avatars are the feature. See the adversarial note in §5.

**D2 — The person→avatars link is node-operator-only.** Recorded so
Sybil-sensitive operations can opt in to uniqueness, **never** readable by a
consumer — otherwise ArdaNova could discover its user is also a Taawun user.

**D3 — Uniqueness is enforced per-operation, not globally.** Operations that
genuinely need one-human-one-claim (airdrops, per-person quotas, one-vote
governance) declare it and check the person key at that point.

**D8 — Callback is an untrusted trigger; `/result/` is the only authority.**
Identomat session-status callbacks carry only a status; AZOA always re-fetches
server-side.

**D23 — Person-key uniqueness is per-tenant by default; global scope is an
explicit per-operation opt-in.** The person key is HMAC'd under a **per-tenant
salt**, so the same human resolves to an unlinkable key in ArdaNova versus
Taawun. A single node-wide salt would make the person key a cross-tenant
correlation oracle — exactly the leak D2 forbids, and a class this repo has
shipped three times. *Residual risk accepted:* an operator who forgets to opt a
Sybil-sensitive operation into global scope gets no cross-tenant protection.
Denials must be **indistinguishable from ordinary ineligibility** in any
consumer-visible response.

**D24 — AML/sanctions screening ships with KYC, not deferred.** Research:
`.omc/research/identomat-aml-screening-2026-08-21.md`. Four facts that constrain
any future change:
- All 5 screening endpoints are on `external-api.identomat.com` (camelCase
  `companyKey`) — **not** the `widget.identomat.com` host serving `/result/`.
  The adapter spans two hosts.
- **Monitoring has no callback channel at all.** Ongoing monitoring is
  **cold-poll only** against `list-screening-monitoring-records` with a `date`
  high-water-mark.
- The 600 req/min per-IP cap is **one shared budget** across KYC *and* screening.
  The monitor poll must be a single batched paginated call, never one per
  identity.
- There is **no APPROVED/REJECTED enum** on screening. Risk decisioning is
  entirely AZOA's, derived from `score` + `topics`. Persist the `scoreThreshold`
  queried with — it is not echoed per hit and the result set cannot otherwise be
  explained or reclassified.

⚠ **Negative finding, unresolved product/compliance escalation:** Identomat
exposes **no company/entity screening endpoint** (confirmed against the full 40+
endpoint listing). Future KYB counterparty screening cannot be built on it.

**D25 — `KycIdentity` carries a retention period and an erasure path from day
one.** Erasure tombstones the retained personal fields (`first_name`,
`last_name`, `birthday`, `citizenship`) while **preserving the salted person
key**, so GDPR Art. 17 erasure and KYC record-keeping both hold, and uniqueness
checks plus the audit trail survive an erasure.

### Authentication (2026-08-20)

**D17 — OAuth is an authentication factor bound to an avatar, never an identity
authority.**
- `AvatarExternalLogin`: `(provider, subject) → avatarId`. Subject is the
  provider's stable id, **never the email**.
- Bind **per tenant**, not globally — a global binding reintroduces the
  cross-tenant collapse D1 prevents.
- **A Google or GitHub account never satisfies KYC.** OAuth proves control of an
  account; Identomat proves a person. Never conflated on the value gate.
- ⚠ **Never auto-link an OAuth identity to an existing account by matching
  email** — the classic account pre-hijacking vector. Linking requires an already
  authenticated session and an explicit user action. *(Enforced structurally: the
  lookup method does not exist.)*

**D18 — OAuth flow is backend-driven so client secrets never reach the browser.**
`GET /auth/oauth/{provider}/start` → provider redirect (`state` + PKCE) →
`GET /auth/oauth/{provider}/callback` → validate → issue the **same AZOA JWT the
password path issues** → hand the frontend a short-lived exchange code.

### Chains & bridge (2026-08-20)

**D4 — Full bridge lifecycle is in scope for every chain**, behind a
strategy-pattern abstraction living in provider code.

**D5 — Ethereum is built on Nethereum** — matches the per-chain-SDK precedent
(Algorand2, Solnet) and supplies RLP, EIP-1559 typed transactions and ABI
encoding rather than hand-rolling them on a value-bearing path. Signing still
sits behind `ITransactionSigner`.

**D22 — The bridge is generic over *capability* and *protocol*, never over the
provider type.**
- **Axis 1, capability modules** (runtime-resolved, on the existing
  `IBlockchainProviderModule`/`TryGetModule<T>` seam): `IBridgeSourceLeg` /
  `IBridgeTargetLeg`. A provider that cannot bridge **simply does not implement
  the module — absence *is* the fail-closed**, structurally. You cannot fake what
  you do not implement, and there is no refusal body left to regress into an `Ok`.
  This is why `SupportsBridging` was deleted: one boolean for both directions
  could not express Algorand's actual source-only state.
- **Axis 2, protocol** (statically known, so the generic earns its keep):
  `IBridgeProtocol<TProof>`, e.g. `WormholeProtocol : IBridgeProtocol<WormholeVaa>`.
  The lifecycle varies by **protocol**, not chain pair — Algorand→Solana and
  Ethereum→Solana over Wormhole share one proof type and one state machine.
- *Why not generic over provider type:* the chain pair is **runtime data**, so
  `IBridgeStrategy<TSource, TTarget>` cannot bind without `MakeGenericType`
  reflection — forfeiting the compile-time safety that motivated generics. It is
  a dictionary lookup with extra steps.
- **Boundary:** does **not** extend to fiat. `IBridgeProtocol<TProof>` presumes a
  proof artifact; fiat has none. Cross-rail stays a saga; cross-chain is this.

### Value model & fiat (2026-08-20, post-refutation)

**D7 — Fiat is a value rail, but "zero new node types" is RETRACTED.** The claim
that quest handlers already dispatch value through
`IBlockchainProviderFactory.GetProvider(...)` was **false**. Ground truth at the
time: `TransferNodeHandler` moved **an NFT by Guid**; `Grant` minted an NFT;
`Refund` reversed a prior NFT transfer; **none could express amount + currency +
destination**, which is what a fiat payout is. Chain dispatch happened *inside
managers* off `wallet.ChainType`, and handlers defaulted a missing chain type to
`"Algorand"` — so an unstamped fiat op would reconcile against Algorand. Fiat
requires fiat-specific node types **or** a genuine value-model refactor. Do not
carry the "zero new node types" claim forward.

**D9 — `IValueRailProvider` is the right seam, and introducing it was a factory +
gate refactor, not a re-parenting.** *(Shipped: `Interfaces/IValueRailProvider.cs`.)*
The invariants it encodes:
- The factory must be **rail-aware** — it was typed `Func<IBlockchainProvider>`,
  making a non-chain rail type-impossible to register.
- `ChainCapabilityGate` must be **rail-aware**. "Actor has ANY wallet bound" was
  wrong in both directions: a user with an Algorand wallet passed for a fiat
  payout with no fiat destination, and a fiat-only Taawun congregant — exactly the
  persona who should never see a blockchain — **failed** the gate for a pure fiat
  payout.
- Network resolution required Devnet/Testnet/Mainnet; Stripe is live/test.
- ⚠ **SUPERSEDED by D43/D45 (2026-09-07).** ~~Fiat reaches Stripe **only through
  `fiat-sidecar/`**, never directly (integration contract §3).~~ Reversed: Stripe
  checkout, webhook signature verification, and the dispute/refund event stream now
  terminate **inside AZOA** through a `FiatRailProvider` implementing this same
  `IValueRailProvider` seam, and `fiat-sidecar/` retires once the port lands. The
  seam this decision built is exactly what makes that port possible — see D43.

**D10 — `SettlementProfile` is necessary but NOT sufficient; the real primitive
is a reversal-event pipeline.** A release-time check relocates the money-printer
rather than closing it: Taawun escrow releases on buyer confirmation (day 3), a
chargeback lands day 90. Reading a ~120-day window at release either blocks all
fiat-legged escrow for four months — commercially dead — or eats the post-release
reversal. Reversals were **silently discarded**
(`sdk/azoa-wallet/src/orchestration/stripe.ts` returned `{status:"ignored"}` for
everything but `checkout.session.completed`, dropping `charge.dispute.created`,
`charge.refunded`, `charge.dispute.funds_withdrawn`). Required: sidecar forwards
dispute/refund events; AZOA records them as **first-class ledger facts** and runs
a **compensation saga**; plus a per-flow risk policy. Partial refunds and
multi-recipient Split clawback are part of this primitive, not edge cases.

⚠ **Status (re-checked 2026-09-07): still NOT implemented.** A repo-wide search
for `charge.dispute`, `ChargebackEvent`, `ReversalEvent`, and "compensation saga"
returns zero hits outside `Interfaces/IValueRailProvider.cs`'s own doc-comment
example naming `SettlementProfile`. No reversal-event pipeline, no first-class
ledger fact for a dispute/refund, and no compensation saga exist anywhere in the
tree yet. D43 makes this **easier to build** — with Stripe terminating in AZOA,
`charge.dispute.created`/`charge.refunded`/`charge.dispute.funds_withdrawn` land
directly in the backend instead of being discarded by a separate sidecar process
— but D43 does **not build it**. D10 remains open and still gates any
escrow-release step (D14); see §7 step F.

**D11 — Fiat-in and KYC-begin share one `HostedSessionFlow` abstraction.** Both
are begin → hosted redirect → callback → **authoritative server-side re-fetch**.
Named as an explicit deliverable so the reuse claim is real. *(Shipped.)*

**D12 — DELETED.** The bridge strategy is cross-*chain*, not cross-rail. Fiat has
no lock (a Stripe capture is a settlement with a long reversal tail — the
opposite of a lock), no proof artifact, no wrapped asset, no redeem. "Pay fiat to
prove ownership" is a payment-then-issuance **saga**, which the durable engine
already models.

**D13 — Amounts carry currency, not `ulong`.** `ulong` overflows at ~18.4 ETH in
wei — the interface **could not represent ordinary Ethereum amounts** — and that
signature is where fiat's missing currency dimension belongs. *(Shipped as
`RailAmount`. See the idempotency near-miss in §8.)*

**D14 — Production custody is a scoped workstream, not an assumption.** Escrow
additionally needs a **hold**, which on Algorand is either a TEAL contract
(deploy/call unimplemented) or a platform-custodial holding account (custody
disabled) — so "layer Escrow on the existing primitives" is unbuildable.

**D15 — USD only for now.** Multi-currency/FX out of scope, **but keep the
currency dimension in the type** (pinned to `USD`) so adding a second currency is
a value change, not a schema break.

**D16 — Expose DEX-style analytics; do not compute obligations.** AZOA supplies
*facts*; consumers compute their own tax workflows — the read-side mirror of
"AZOA receives already-decided amounts, never computes economics." Shape: a
normalized transaction/position history (timestamp, operation type, asset,
amount, counterparty, **USD value at time of transaction**, tx reference, fee)
plus realized/unrealized position primitives, queryable per avatar and date range.
⚠ **USD-at-time must be stamped at write time** — reconstructing historical
prices is lossy and a tax workflow built on them is not defensible. *(Landed on
`operation_log`, with `IValueRateSource` a deliberately unwired seam: there is no
price oracle in this codebase, and an absent rate is stored as explicitly null,
distinguishable from a genuine zero.)*

### Consumer primitives (2026-08-20)

**D6 — All four builder axes are in scope:** decompose `quest-canvas.tsx`,
authoring UX, run observability, chain-capability clarity.

**D19 — Soulbound revocation uses Algorand's native clawback.** ASAs carry a
`clawback` address and this codebase **already sets it**
(`Managers/FungibleTokenManager.cs:155`). Revocation is an
`AssetTransferTransaction` with `AssetSender` set. Shape: a `CredentialRevoke`
node plus a revoke route authorized to the credential's **issuer** (not holder),
emitting the same operation record as any other value move so it lands in D16's
analytics. Unblocks ArdaNova's BRONZE→DIAMOND tiers, which today can promote but
never demote.

**D20 — Governance is tallied off-chain, anchored on-chain only if publicly
verifiable voting is actually required.** Ballots on chain are slow, cost fees per
vote, and make voter choices permanently public — usually the opposite of what a
co-op wants. A proposal is a Holon with typed vote children; tally runs in a
**pure-DB (Tier-1) node**, needing no chain capability and no custody. Anchor the
**tally hash** at close if public verifiability is required — one transaction per
proposal, not per vote. Matches Taawun's convergent-vs-transactional split.

**D21 — The principal-only loan tracker is a tracker, not a new value primitive.**
A Holon carrying a disbursement and a repayment ledger; value moves reuse
Grant/Transfer; balance projection is a pure-DB node. **No interest-accrual node
may ever exist** — the absence is the feature. Partial repayment and write-off
are ordinary states, not error paths.

**D26 — Phase 2 shipped in full: no feature skips.** An evidence sweep found that
**neither consumer requires** Ethereum, Solana SPL, or a cross-chain bridge: zero
EVM dependency in either tree; `@solana/spl-token` appears solely as an optional
peerDependency of AZOA's own `azoa-sdk` in ArdaNova's lockfile, imported by
nothing; neither repo uses "bridge" in the literal blockchain sense. **The owner
reviewed this evidence and directed that all three ship anyway.** Recorded so a
future session does not "rediscover" the scope reduction and re-litigate a
decision already made against the evidence.

### Settled 2026-08-22 (handoff gate) — these drive §7

**D27 — Production KMS/HSM custody is built BEFORE any Phase 3 fiat step.** The
alternative on offer was an explicit gated acceptance of dev-grade config-derived
custody for a first capped milestone; **the owner rejected it.** So
`CustodyMode = "Disabled"` and the absent production adapter are a **scheduled
workstream, not a documented risk**, and Phase 3 stays blocked until it lands.
*Foundation in place:* `Services/Custody/PlatformKeyDerivationRegistry`,
`IPlatformKeyDerivation`, per-chain `TryDerive` — a KMS/HSM adapter plugs in there
and replaces the *derivation*, not the chokepoint. Escrow additionally needs a
**hold**, which exists on neither path.

**D28 — Terminology neutralization + domain renames.** AZOA is the neutral
value/workflow substrate; consumer-specific vocabulary belongs in the consumer.
Three strands, one sweep:
1. **`Avatar` → `User`** and **`Quest` → `Action`** across C# models and managers,
   SurrealDB tables + regenerated schemas, REST routes, the TypeScript SDK, and
   the frontend.
2. **Strip loaded religious language from AZOA's own primitives** — *the
   constraints stay, the vocabulary goes.* "Qard Hasan tracker" → **principal-only
   loan tracker** whose defining property is that **no interest-accrual primitive
   exists**; "riba-free" → "no interest-bearing primitive may exist in a flow";
   "anti-gharar" → "ambiguity must fail closed and refund"; Zakat is a
   consumer-side calculation over AZOA facts, not an AZOA node. Taawun keeps its
   own vocabulary in its own product.
3. **Strip residual OASIS-project vocabulary** wherever it survives. Extends the
   branding rule in `project-renamed-azoa` (never "Sleek").

⚠ **Breaking change to DB tables, REST routes, and the SDK.**
`greenfield-prelaunch-no-compat` applies — prefer a clean rename over compat
shims — **but the node is live on Railway**, so it needs the same reconciliation
treatment as any schema change. Sequencing consequence in D29.

⚠ **Three-repo change.** D28 breaks **Taawun's `pkg/financial/azoa.go`**
(`CreateQuestRequest`) and **ArdaNova's integration contract**. Plan the consumer
updates as part of the same change, not as follow-up drift.

**D29 — Deployment reconciliation, split by urgency.** The live Railway
deployment diverges on three axes: new tables (`hosted_session`, `kyc_identity`,
`kyc_screening_monitor`, `kyc_screening_poll_state`, `kyc_screening_result`,
`user_external_login`, `oauth_exchange_code`), the forward migration widening
`operation_log.amount`, and hazardous Ethereum wallet rows. The owner chose full
reconciliation. **Split it, because the halves have different clocks:**
- **Urgent, first, independent:** delete and regenerate every Ethereum wallet row.
  Treat them as **compromised, not merely broken** — see §8. Should not wait
  behind the rename.
- **Deferred until after D28:** the schema/migration apply. Doing it now means
  doing it twice, because D28 renames tables. One combined apply after the rename
  is strictly less risky than two.

### Settled 2026-09-04 (D28 execution gate) — how the rename actually runs

**D31 — The record-ID remap migration is DROPPED; the 8 live rows are wiped and
re-seeded.** SurrealDB record IDs embed the table name, so a rename invalidates
every stored `avatar:<id>` / `quest:<id>` value across **60 `record<avatar>` FK
columns in 35 tables and 9 `record<quest>` columns in 7 tables** (~69 fields).
Authoring forward migrations for all of them would be the single most
error-prone artifact in the sweep. Measured against the live node it would also
be near-pointless: the database holds **6 `avatar` rows and 2 `api_key` rows and
nothing else** (§7 step A). `greenfield-prelaunch-no-compat` applies — wipe and
re-seed. ⚠ **This relaxes the DATA migration only.** The `.surql` schema must
still be correct, and a wrong UNIQUE index is still wrong on an empty table.

**Re-seed manifest** (captured 2026-09-04 before any destructive step; full rows
in the session scratchpad `live-rows-preD28.json` — **not committed, it carries
personal data**):

| What | Detail | Re-seed action |
|---|---|---|
| `node_operator` | the owner's own account; holds **both** API keys | Recreate — this is the working account |
| `node-operator` | id `avatar:a20a…0001`, `is_verified: true`, structured deterministic id | **Regenerates itself** via the admin bootstrap path (`admin_bootstrap_state` table). Do not hand-seed; verify it reappears |
| 4 demo accounts | `gamer1`, `gaming`, `MaxG`, `gamer` | Throwaway — but **one is a real external person's account** (a `nuvalabs.com` address), so this is a real, if small, user-visible deletion. Decide whether to notify |
| 2 × `api_key` | both named `first-node-demo`, bound to `node_operator`, scopes `dapp:develop` + `dapp:manage`, expiring **2026-10-09** | ⚠ **CANNOT be re-seeded with the same secret.** Keys are stored SHA-256-hashed; the plaintext is unrecoverable. Wiping means **issuing new keys**, and any integration holding `first-node-demo` breaks. Given those scopes, ArdaNova is the likely holder — coordinate the swap with the D28 cross-repo wave rather than discovering it as an outage |

**D32 — The rename covers `conductor/` historical track specs too.** The owner
chose full vocabulary consistency over preserving each shipped track's
as-written wording (~4,229 additional occurrences). *Consequence accepted:* a
2026-06 spec will read as though it described building "Action", which is not
what it was called at the time; the git history remains the record of what was
actually written.

**D33 — Execution is a scripted transform reviewed by agents, not a hand-edit
fan-out.** ~28,000 occurrences across ~900 files makes per-site hand-editing both
expensive and error-prone. One validated transform script runs per partition;
agents then review the **generated diff** for the cases a script cannot know —
wire-contract string literals, derived idempotency keys, route templates,
third-party identifiers. *Authoring and review stay separate passes:* the script
is adversarially reviewed before it is ever run.

**D35 — `Quest` becomes `ActionDefinition` in C# only; the wire stays `action`.**
Adversarial review proved the literal `Quest`→`Action` rename produces **136
compile errors**: `AZOA.WebAPI.csproj` sets `<ImplicitUsings>enable</ImplicitUsings>`,
so `global using System;` reaches every file and the bare entity collides with
`System.Action` (132 × CS0104 across 12 files), while namespaces `Models.Quest` /
`Services.Quest` / `Tests.Quest` → `*.Action` **shadow the delegate**
(CS0118 in 2 test files). Resolution:
- C# entity class → **`ActionDefinition`**; namespaces → **plural** (`Models.Actions`,
  `Services.Actions`, `Tests.Actions`). `Services.Avatar` → `Services.Users` for
  the same reason: a namespace `Services.User` beside the type `Models.User` is
  ambiguous in any file importing both.
- **DB table stays `action`, REST route stays `/api/action`, SDK type stays
  `Action`.** D28's externally-visible vocabulary is untouched — the constraint
  is C#'s alone. A real SurrealDB 3.x container was probed: `DEFINE TABLE action`,
  `DEFINE TABLE user`, `record<action>`, `record<user>` all accepted.
- The rejected alternative was a global `<Using … Alias="Action"/>` in the csproj.
  It compiles, but hijacks `Action` codebase-wide so every future
  `System.Action<T>` needs full qualification, permanently.
- ⚠ Consequence to accept: the type-and-property same-name idiom
  `public Quest Quest { get; }` becomes `public ActionDefinition ActionDefinition
  { get; }`, so call sites read `context.ActionDefinition`. Both sides must use
  one name or they desynchronise (measured: 6 × CS1061). Polishing the property
  to `.Action` is 8 sites of naming, not correctness — a diff-review item.

**D34 — ~~Taawun is deferred~~ SUPERSEDED 2026-09-06.** The deferral rested on
the repo being unfindable: its recorded checkout
(`C:\Users\atooz\.codex\worktrees\6e53\tawun`) had been pruned. **The repo does
exist, at `c:\Users\atooz\Programming\tawun`**, and its AZOA client lives at
**`src/pkg/financial/`** — `azoa.go`, `azoa_federation.go`, `azoa_store.go`,
`azoa_test.go`. Note `src/pkg/`, **not** `pkg/` as every earlier note recorded.
Taawun is therefore **in scope for the cutover**, alongside ArdaNova.

#### Rename hazards proven against source 2026-09-04 (supersede the earlier guesses)

- ⚠ **The earlier "three UNIQUE indexes carry the old noun" claim is REFUTED.**
  Checked all 31 UNIQUE indexes across `Generated/Schemas/` and
  `CompatibilityBaselines/` by their **FIELDS clause**: `KycIdentity.avatar_ids`
  is an unindexed `option<array<string>>`; `hosted_session_admission` is
  `(flow_kind, provider_key, admission_key)`; `avatar_external_login_scope_provider_subject`
  is `(tenant_scope, provider, subject)`. **None contain the noun in a field
  name.** Exactly one index qualifies repo-wide:
  `wallet_avatar_chain_address UNIQUE(avatar_id, chain_type, address)`.
  The real hazard was never index *fields* — it is record-link **values** (D31).
- ✅ **The `quest`/`request` split is exact, not approximate.** Repo-wide
  `quest` (ci) = 21,340; `request` = 6,575; `question` = 45; and
  21,340 − (6,575 + 45) = **14,720**, matching the true-positive count exactly —
  proving no third false-positive family exists (no `bequest`, etc.).
  **Canonical regex: `(?<!re)quest(?!ion)`.**
- ⚠ **`AvatarNFT` is IN scope, not a false positive.** It carries `Guid AvatarId`
  and a nav property FK'd to Avatar (soulbound identity NFT) → `UserNFT`/`UserId`.
- ✅ **Only one genuine false positive:** `frontend/src/components/ui/avatar.tsx`,
  a wrapper around the third-party `@base-ui/react/avatar` profile-picture
  primitive (`Avatar`, `AvatarImage`, `AvatarFallback`, `AvatarBadge`,
  `AvatarGroup`, `AvatarGroupCount`), used only by `layout/header.tsx`. **Do not
  rename it.**
- ✅ **Framework name collisions checked and cleared — but they are traps for
  future code.** `Models/Avatar.cs` lives in `AZOA.WebAPI.Models`, which **30 of
  33 controllers import**, and **18 controllers + `Program.cs` +
  `Core/Networking/RateLimitPartitionKey.cs` use ASP.NET's `ControllerBase.User`**
  (73 sites: `User.Identity`/`.Claims`/`.FindFirst`/`.IsInRole`). This compiles
  **only because zero controllers currently reference the model type by simple
  name** (verified: 0 sites) — inherited members outrank imported types in simple-name
  lookup, so `User` keeps meaning the `ClaimsPrincipal`. ⚠ **Any future controller
  code wanting the model type must qualify it (`Models.User`) or it hits CS0118.**
  Similarly `Models.Quest` → `Models.Action` would shadow `System.Action`, but
  **no file imports `AZOA.WebAPI.Models.Quest` and uses `Action<…>`** (verified: 0),
  and the only two `Action<>` users (`Services/Blockchain/BlockchainOperationBuilder.cs`,
  `Core/Idempotency/IdempotencyReplay.cs`) do not import it. **Treat CS0104 and
  CS0118 as the expected collision signature in the post-rename build.**

#### Partition rules (the fan-out is NOT free-form)

1. **Never split by noun.** `Program.cs` alone carries 41 `Avatar` + 102 `Quest`
   hits, and **28 of ~30 `Interfaces/` files reference both nouns**, because
   `avatarId` is the ambient caller-identity parameter on nearly every
   Quest-touching signature. Partition by **disjoint file lists doing both
   renames together**.
2. **`Generated/Schemas/` and `Generated/Flowcharts/` are a downstream
   regeneration step, not a parallel partition.** Hand edits there are silently
   discarded on the next generate. Sequence: `Models/` → build →
   `surrealforge generate-from-assembly` + `flowcharts-from-assembly`.
3. **`CompatibilityBaselines/` (6 hand-maintained files) moves in lockstep with
   the goldens**, not independently — all 6 contain `record<avatar>` columns.
4. **`sdk/azoa-wallet/src/api/api-version.ts` is downstream of Controllers**, and
   the SDK must be `tsup`-rebuilt before any `frontend/` work — the frontend
   resolves the **built** package.
5. **A directory's `AGENTS.md` belongs to whichever partition owns its code**, or
   prose will describe pre-rename identifiers beside post-rename code.
6. **Directory renames** (`Services/Quest/`, `Services/Avatar/`) are a separate
   mechanical `git mv` step — combining `git mv` with content edits on the same
   files is a known merge-noise source.

#### Schema regeneration — the documented golden path (`Persistence/SurrealDb/CONVENTION.md`)

```
surrealforge generate-from-assembly bin/Debug/net8.0/AZOA.WebAPI.dll
surrealforge flowcharts-from-assembly bin/Debug/net8.0/AZOA.WebAPI.dll
surrealforge up --connection <endpoint> --user <u> --pass <p> --namespace azoa --database azoa
```
Apply is two-phase: `Generated/Schemas/*.surql` (lexical, idempotent
`IF NOT EXISTS`) overlaid by `CompatibilityBaselines/*.surql`, then
`Migrations/*.surql` (timestamp order). ⚠ `CONVENTION.md:319-321` — hand-editing
a golden is silently overwritten; edit the POCO instead.

#### ArdaNova coupling (the one consumer that could be scoped)

- `ardanova-client/package.json:61` pins **`azoa-sdk: "^0.1.0"`** — behind the
  published line; version drift to flag separately.
  `ardanova-client/src/lib/azoa-sdk/{client,queries,types}.ts` import
  `AzoaApiClient`/`AzoaApiConfig`, call `API_PATHS.HOLON_GET` and
  `API_PATHS.QUEST_RUN_EXECUTION_STATE`, and use types `QuestResult`,
  `QuestNodeResult`, `QuestStatus`, `QuestNodeState`. Cache keys embed the literal
  string `"quest"` (`azoaKeys.questRunState`).
- ⚠ **The silent-breakage path:** ArdaNova's own .NET backend
  (`ardanova-backend-api-mcp`) is a **string-based HTTP consumer with no shared
  types** — `AzoaAvatarDtos.cs`, `AzoaAvatarService.cs`, `IAzoaAvatarService.cs`,
  an `"Azoa"` appsettings section, and `avatar.AvatarId` (`AzoaAvatarService.cs:70,129,154`)
  persisted as `ArdaNova.Domain.Entities.User.azoaAvatarId`. A route/JSON rename
  breaks this **at runtime, not at compile time**. Name it explicitly in the
  cross-repo step.
- **No `CreateQuestRequest` reference exists anywhere in ArdaNova** — that symbol
  appears to be Taawun-only and remains unverified (D34).

### Settled 2026-09-06 (post-cutover gate)

**D36 — The KMS vendor is deferred; the custody seam is built provider-agnostic
with an environment-backed fallback.** No vendor was named anywhere in the repo,
and the node runs on Railway which has no native KMS. So D27's adapter is built
against `IPlatformKeyDerivation`, with a concrete implementation that pulls key
material from the environment/secret store as the interim custody mode.
⚠ **Wording corrected 2026-09-07.** This decision previously described that
interface as an abstract "**sign-without-export**" interface. It is not one, and
the phrase overstated what was built: `IPlatformKeyDerivation.TryDerive` returns
the raw private-key bytes into process memory (zeroed after use by the caller) so
a chain SDK can sign with them — the key is exported into the process, exactly
what a real sign-without-export design (the key material never leaving a KMS/HSM
boundary at all) is meant to avoid. **That design does not exist yet** in this
codebase; what exists is a clean per-chain derivation seam a real adapter can
later plug into. Consequence stated plainly: this does NOT clear D27's gate. An
env-backed key is still config-derived custody — it is a better-shaped seam, not
an HSM. Per **D48**, closing this gap is **the operator's clock**, not a scheduled
workstream in this program: Phase 3 fiat remains gated until a real vendor lands
behind the same interface, but procuring that vendor is explicitly not this
program's concern. Record the interim mode explicitly rather than letting
`CustodyMode` imply more assurance than exists.

**D37 — Authorized: publish `azoa-sdk@0.3.0`, and wipe/re-seed the live
database.** Both are irreversible or outward-facing and were explicitly approved.
Also authorized: retiring legacy implementations as needed, adding/updating
integration tests to match, and **pushing to online staging**.
*Ordering, which matters:* the npm publish goes first because ArdaNova's CI is
red against an unpublished pin; the database reconciliation goes **last**, after
the Phase 5 primitives land, so the schema applies **once** — D29's own reasoning
that doing the apply twice is pure risk.

**D38 — All four Phase 5 consumer primitives ship, including soulbound
revocation.** D19 (revocation via Algorand clawback), D20 (off-chain governance
tally), D21 (principal-only loan tracker), D16 (analytics read surface).
⚠ **Revocation is the one that touches a value path.** The clawback address is
already set (`FungibleTokenManager.cs:155`), so the primitive is modest — but
this codebase has shipped **fabricated-success bugs on exactly these paths**
three times (§2, §8). Its brief must forbid reporting success without a real
broadcast confirmation, and it earns an adversarial review its siblings do not.

### Settled 2026-09-06 (Phase 5 design gate) — these drive the two design tracks

**D39 — The uniqueness claim IS the vote of record.** Governance tallies from
`person_operation_claim`, never from holons. The claim table already stores
exactly one row per person per operation, written atomically inside a single
statement, so a forged vote holon counts for nothing **by construction** — the
tally never reads holons at all. This replaces the shipped-but-broken tally,
which counted raw child holons and let one verified user `POST /api/holon` 500
ballots that all counted. *Chosen over a claim-joined holon tally because that
one stays safe only while nobody drops the join; this one has no join to drop.*

**D40 — Votes are mutable until close, last write wins.** Real deliberation
changes minds. This is normally the expensive option — it needs an ordering rule
the tally must trust, and the shipped code's ordering key was read from
**attacker-controlled holon metadata**. Under D39 it costs nothing: there is
exactly **one claim row per person**, so a change is an in-place update of that
row's option with a **server-set** timestamp. There is no "which ballot is
latest" question, so the ordering attack does not merely get defended — it
**ceases to exist**. After close the row is frozen.

**D41 — Soulbound credentials are issued by a third party to a recipient.**
D19 authorizes revocation to the credential's **issuer**, but the only production
mint path stamps issuer = minting caller = holder, making that check **vacuous**
and the whole rail inert. The mint API must take a recipient distinct from the
caller. This is what ArdaNova's BRONZE→DIAMOND tiers actually need — an
organization issues to a member and can later demote — and it is the premise D19
was written against. *Consequence to accept:* this is a real API change to the
soulbound mint path, not a patch to the revoke path.

**D42 — The tally stays off-chain and Tier-1.** No chain capability, no custody
exposure, no per-proposal fee — and it can therefore ship while Phase 3 custody
is still gated. The tally hash is still computed and stored, so anchoring later
is an added step rather than a redesign. D20's conditional ("anchor only if
publicly verifiable voting is actually required") resolves to: not yet.

### Settled 2026-09-07 (owner decision during the production-readiness review)

**D43 — Fiat is baked into AZOA as a first-class rail. The sidecar is retired.**
The owner's call, made against the product thesis: ArdaNova is a social
platform whose users must never have to know a chain exists, and a separately
deployed Stripe bridge is one more thing that can be absent — which it currently
is (the census found `fiat-sidecar/` has **no deployment target and no CI job**,
so the entire fiat program had an unowned prerequisite).

*What changes:*
- A `FiatRailProvider` implementing `IValueRailProvider` lives **inside the .NET
  backend**, with `RailId = "Fiat.USD"` and a `SettlementProfile` carrying the
  chargeback reversal window. The seam was built anticipating exactly this —
  `IValueRailProvider`'s own doc-comment names `"Fiat.USD"` as the example rail.
- Stripe checkout, webhook signature verification and the dispute/refund event
  stream terminate **in AZOA**, and the secrets live in AZOA's secret store.
- `fiat-sidecar/` (210 lines of Express around the SDK's `AzoaStripeOrchestrator`)
  is retired once the port lands. The SDK orchestration helper stays as a
  consumer-side convenience only if a consumer still wants to run its own bridge.

*What this SUPERSEDES — amend, do not silently contradict:*
- **D9's bullet** "Fiat reaches Stripe **only through `fiat-sidecar/`**, never
  directly (integration contract §3)" is reversed.
- **Integration contract line 75** assigns "Webhook signature verification (fiat /
  payment provider)" to the **Consumer**. It moves to AZOA. Both copies of the
  contract must change together and stay byte-identical.
- **D27's gate is now correctly SCOPED.** It said custody lands "before any
  Phase 3 fiat step". Fiat-IN (collection → allocation) already runs through the
  sidecar today with no custody at all, because a Stripe capture is not a key
  operation. What custody actually gates is **fiat-OUT** (payouts) and **escrow
  holds** (D14). The port of fiat-IN into AZOA is therefore NOT custody-gated and
  can start now; payouts remain behind D27/D36.

*What must be PORTED, not re-derived* — the sidecar's `AGENTS.md` records three
hard-won invariants, each the fix for a shipped defect:
1. **§pricing** — the minted quantity is derived at webhook time from the
   Stripe-confirmed `amount_total` times an operator price schedule, **never from
   client metadata**. This closed the CRITICAL-1 money-printer where a client
   could mint an arbitrary amount for a trivial payment. Fiat cents and AZOA base
   units are two units, never conflated.
2. **§auth** — checkout is bearer-authenticated with a constant-time compare that
   fails closed when the secret is unset; routing values are allowlisted; CORS is
   an origin allowlist.
3. **§webhook-mapping** — the allocation seam returns a RETRYABLE in-progress
   failure before the chain tx is recorded; mapping that to a plain error would
   make Stripe retry forever, give up, and leave a customer charged with no
   tokens. Transient and terminal outcomes map to different HTTP statuses.

*What this makes EASIER:* D10's reversal pipeline. With Stripe terminating in
AZOA, `charge.dispute.created`, `charge.refunded` and
`charge.dispute.funds_withdrawn` land directly in the backend as first-class
ledger facts instead of being discarded by an orchestrator in another process.

*⚠ A fact discovered while recording this, which the decision must now absorb:*
**ArdaNova already runs its own Stripe integration and uses neither the sidecar
nor the SDK orchestrator.** Verified: `StripeWebhookController.cs`,
`IStripeWebhookInbox.cs`, `StripeDtos.cs`, and `FundingIntentService.cs` →
`AzoaAllocationService.cs` in `ardanova-backend-api-mcp`; zero references to
`fiat-sidecar` or `AzoaStripeOrchestrator` anywhere in its tree. The contract's
line 75 was recording what ArdaNova built, not an aspiration. So "bake fiat into
AZOA" is not a port of the sidecar — it is a **choice about two live money paths**:

| Option | AZOA owns | ArdaNova owns | Consequence |
|---|---|---|---|
| **A. AZOA is the rail; ArdaNova calls it** | Stripe checkout, webhook verification, dispute stream, settlement → allocation | the funding intent (which project, valuation, what the sponsor holds) | ArdaNova's `StripeWebhookController` retires and `FundingIntentService` calls AZOA's fiat rail. Cleanest split under the thesis — economics stays consumer-side, plumbing platform-side — but it is a migration of working ArdaNova code and a **second Stripe account/config to cut over**. |
| **B. Both coexist** | a fiat rail for consumers that want one | its own Stripe as today | Two Stripe integrations in the product, two webhook secrets, two dispute streams. D10's reversal pipeline would have to exist twice. |
| **C. AZOA rail only for payouts (fiat-OUT)** | payouts, which nothing does today | collection, as today | Smallest change; leaves collection where it works. But the thesis-level reason for D43 — one fewer thing that can be absent — is only half served. |

The sidecar is retired under all three. **This is the owner's call**; the vertical
review's sponsorship lane is tracing this exact path and its findings should
inform it. Until it is made, the fiat port is scoped to the rail primitive
(`FiatRailProvider` + `SettlementProfile`) and NOT to Stripe endpoints, so no
duplicate money path is created by default.

*⚠ A second question, not answered by the decision:* the sidecar's
`TOKENS_PER_CENT` price schedule is a rate. D7/D16 say AZOA receives
already-decided amounts and never computes economics. Is an operator-configured
tokens-per-cent schedule a **rail parameter** (fine inside AZOA) or **consumer
economics** (must stay in ArdaNova, with AZOA receiving the already-computed
mint quantity)? The sidecar treated it as the former. Decide before the port
fixes it in place.

### Settled 2026-09-07 (owner gate on the production-readiness review) — these re-sequence the waves

**D44 — The economy is token-native and chain-first. Fiat is only the on-ramp and
off-ramp for the platform token.** In the owner's words: *"fiat is there for
people to buy the ArdaNova platform token to use to buy and fund the projects;
people cash out with project tokens and get project tokens they can trade for the
platform token which they can sell for fiat."* So:
- **IN:** fiat → ARDA (platform token) → spend ARDA to fund a project → receive
  **project tokens**, which are real per-project ASAs (D47).
- **OUT:** project tokens → trade for ARDA → sell ARDA for fiat.
- Sponsorship is **not** a Stripe checkout for a project. It is spend-ARDA,
  receive-project-ASA, **on chain**. Fiat never touches a project directly.
*Consequences, each of which reverses a review recommendation:*
- The review's **fiat-first ordering is REJECTED**. Its waves put sponsorship (W3)
  before custody (W8) and the chain anchor (W9) on the theory that fiat could carry
  value while KMS was procured. Under D44 nothing of value moves without the chain,
  so **custody, ARDA genesis, per-project ASA issuance, and an ARDA↔project-token
  exchange are on the CRITICAL PATH for the first sponsorship.** The waves are
  being re-sequenced chain-first.
- Decisions-to-ratify **#2** (ArdaNova talks to Stripe directly) and **#8** (ARDA is
  internal-only, never shown to users) are **REJECTED**. ARDA is the user-facing
  currency people buy, hold and spend.
- ⚠ **ARDA has no genesis** — the review found circulating supply is always 0 and
  every USD conversion preview fails. Under D44 that is no longer a cosmetic
  defect; it is the first thing on the critical path.
- ⚠ **The ARDA↔project-token exchange mechanism is undesigned.** The review found
  Tinyman/AMM swaps are off ArdaNova's user path and `ExchangeService` is
  treasury-rate. D44 requires *some* exchange for cash-out. Treasury-rate vs AMM is
  an open design question the re-sequenced W0 must surface.

**Cross-links — how D44 resolves into D45-D51:** the Stripe rail scoped to
on/off-ramp only is **D45**; uniform KYC + wallet-before-value is **D46**; the
per-project ASA shape for a stake is **D47**; the operator/custody posture that
lets all of this proceed without waiting on a KMS vendor is **D48**; the ARDA
genesis answer is **D49**; the exchange-mechanism answer is **D50**; the
deployable-composition rename the exchange is built from is **D51**.

**D45 — D43 stands as stated: AZOA owns the Stripe rail.** The review's
alternative (ArdaNova owns Stripe in+out, AZOA as a later anchor) was put to the
owner with its evidence and **rejected**. Under D44 the rail's job is precisely
scoped: **fiat→ARDA on-ramp and ARDA→fiat off-ramp** — not project checkout.
ArdaNova's `StripeCheckoutGateway`/`StripeWebhookController` migrate to call the
AZOA `Fiat.USD` rail; contract line 75 (webhook verification → Consumer) moves to
AZOA; the sidecar retires. *Accepted cost:* thesis moments wait on the port rather
than shipping on ArdaNova's existing Stripe.

**D46 — KYC is uniform: full document KYC AND a provisioned custodial wallet
before any value action, sponsor and recipient alike.** The review's tiering
(AML-only for small sponsors, full KYC for recipients) was **rejected**. This is
consistent with D44 — every value action is on chain, so every actor needs a
wallet — and the friction is accepted as policy. *What the thesis promise now
means, precisely:* the **wallet stays invisible** (custodial, tenant-provisioned,
never a seed phrase or address on a user screen) but **KYC does not**. "Never has
to know a chain exists" holds; "never does an identity check" does not. The
custodial wallet this decision requires up front is provisioned under the same
tenant-custodial posture **D48** assumes present; the KMS/HSM gate that would make
that wallet's custody real is **D27/D36**, unaffected by this decision.

**D47 — A stake is a real per-project ASA from day one.** The review's
ledger-first option (units in ArdaNova's `TokenBalance`, one ASA per project
later) was **rejected**. *Consequences:*
- **`AllocationKind.Mint`-per-reward is REJECTED** on both callers. A sponsor with
  12 pledges must hold 12 units of ONE project asset, not 12 unrelated assets that
  cannot be summed, swapped or paid out. Shape: **one ASA per project minted into a
  platform reserve; funding and rewards are `IssueUnits` from that reserve.**
- The three coexisting representations (`equityReward`, `awardAmount`,
  `TokenAllocation`) collapse to one, and it is on chain.
- Requires custody, holder opt-in, a **HOLD** primitive (D14 — still absent on
  every path) and a reserve wallet **before the first sponsor**. All critical path.

**D48 — CORRECTED 2026-09-07. The owner runs the AZOA node and is the operator,
custodian of record, and owner of compliance and completeness. ArdaNova is a
TENANT on that node.** In the owner's words: *"assume we have custody; I run the
AZOA node and will own compliance and completeness; ArdaNova is a user on the
AZOA node I manage."*
- ⚠ **The first version of this decision was WRONG** and stood for under an hour.
  It named *ArdaNova* as operator and custodian, following the review's inference
  that the same person deploying AZOA on Railway made ArdaNova the operator. It
  does not. The **contract's §11.1 ("ArdaNova owns no KMS") and its
  operator/consumer split were correct as written**; the review's item to amend
  them is **withdrawn**. What the contract *does* need is the D45 change (webhook
  verification → AZOA) and nothing about custodianship.
- **Custody is ASSUMED PRESENT for planning.** It is the operator's
  responsibility, handled outside this program, and **no wave gates on it**. The
  D27/D36 procurement question is the operator's to close on their own clock; the
  capped-pilot posture I had inferred is no longer a planning assumption at all —
  it is simply not this program's concern.
- *What this does NOT remove from the critical path:* the **HOLD primitive** (D14)
  and the **platform reserve wallet** (D47). Those are value primitives the node
  must expose for escrow and `IssueUnits` to work, not custody-assurance
  questions. They stay.
- *What this changes for the tenant boundary:* ArdaNova provisions its users as
  **tenant-custodial child users** with a `tenant:provision` key (which its code
  already does) and never holds a key, a seed phrase or a payment-provider secret.
  Compliance policy — D46's uniform KYC, AML screening, the Identomat trust
  profile — is **set by the operator on the node**, not chosen per tenant.

*Not asked this round — taken as assumptions from the review's recommendations,
each reversible at its wave:* **Q5** hold window on stake until the Stripe
reversal window elapses, clawback only as the exceptional fraud/dispute path,
disclosed in user terms. **Q6** governance franchise decided before any adapter
is written; AZOA governance stays gated meanwhile. **Q7** the orphaned posts stack
is kept as a project-scoped "field notes" stream and hardened, not deleted.

**D49 — ARDA genesis is an Algorand ASA, minted through the primitive that already
exists; devnet faucet is the test path.** Owner's direction: use Algorand on
ArdaNova to facilitate genesis, with the devnet faucet for testing.
*Verified against source before recording — this needs no new chain integration:*
- The mint path exists end to end: `ActionNodeType.FungibleTokenCreate` →
  `Managers/FungibleTokenManager.cs:147` → `IAlgorandASAModule.CreateASAAsync`
  with real total and decimals (`Interfaces/IBlockchainProviderModule.cs:20-34`).
- The faucet exists and is Algorand-only among the enabled providers:
  `AlgorandProvider.cs:142 SupportsFaucet => true` (Solana also true; Ethereum
  false). It is guarded to dev/test networks by the interface contract
  (`IBlockchainProvider.cs:36-48`), which is exactly the intended use.
- So **ARDA genesis is a configuration and operations task, not a build**: decide
  total supply, decimals, and which account holds the reserve, then mint on devnet
  through the existing node and promote the same definition to mainnet.
⚠ *Still open and NOT decided by this:* the **on-ramp price** — how many ARDA one
US cent buys. The retired sidecar treated its `TOKENS_PER_CENT` schedule as an
operator-configured rail parameter; D7/D16 say AZOA never computes economics. Under
D48 the node operator sets compliance policy, which makes an operator-set ARDA
price defensible as a **rail parameter** — but it is the owner's call and it is the
last unanswered piece of the on-ramp.

**D50 — The ARDA↔project-token exchange is an Action DAG plus an Action Graph Bundle, built on
AZOA's existing primitives.** Owner's direction, and it is a deliberate rejection
of the AMM path the review assumed.
*What this composes from, verified:*
- `ActionGraphBundle` (`Models/ActionGraphBundle.cs`) is the deployable unit: it carries a
  `TargetChain`, `GeneratedCode`, `DeploymentConfig` and `BoundHolonIds`, with
  `/generate` and `/deploy` routes already on `ActionGraphBundleController`.
- The Action DAG supplies the orchestration and the value nodes it needs already
  exist in some form (`Swap`, `Grant`, `Transfer`, `FungibleTokenCreate`).
- **This retires Tinyman from ArdaNova's user path**, which the review wanted
  anyway: it found the AMM off the user path and carrying a decimals defect on a
  money path. Exchange is platform-operated, not an external DEX.
⚠ *Consequences to hold onto:*
- The existing `Swap` node delegates to DEX adapters. The ARDA↔project-token
  exchange is **not** that node; it is a new composition. Do not wire it through
  `ISwapManager` without deciding to.
- Rate policy is still ArdaNova's under D7/D16 — AZOA moves already-decided
  amounts. A treasury-rate exchange means **ArdaNova computes the rate** and the
  Action Graph Bundle executes the move.
- An Action Graph Bundle is deployable code with a `PrivateKeyHash` and a deploy route. Whatever
  it does with value runs under the operator's node and the D48 custody posture,
  so it inherits the HOLD requirement rather than escaping it.

**D30 — One commit on a branch off `main`.** The owner chose a single commit over
themed ones. **Consequence accepted knowingly:** this work is **not bisectable** —
a regression traced into it lands on one ~8,900-line commit covering seven
workstreams. Mitigation: the §2 review ledger maps each workstream to its verdict
and test counts, and §8 names each phase's tests.

---

## 4. Assumptions

`<assumption> · default taken · to reverse`

1. **D3's uniqueness check is itself a side channel, and D2 does not stop it.** ·
   default: assume deny/allow outcomes are consumer-visible · to reverse: an
   eligibility denial ("this person already claimed via another avatar") tells one
   tenant its user exists on another — the exact correlation D2 forbids. Denials
   must be **indistinguishable from ordinary ineligibility**, and `KycIdentity`
   must live in a table structurally excluded from `Search`/`HolonQuery` (this
   repo has leaked cross-tenant through generic read surfaces three times).
2. **Escrow/Split/Payout/Pledge/Stipend cannot simply layer on the existing
   primitives** · default: **REFUTED by review** — Grant/Transfer/Refund are
   NFT-by-Guid moves (D7) and none can *hold*, which escrow requires (D14) · to
   reverse: cheap now, very expensive after the builder UI is designed around the
   current 43-node catalog.
3. **The fiat chargeback reversibility window is ~120 days.** · default: an
   estimate for card schemes, **not verified against Stripe's docs or the
   acquirer's terms** · to reverse: cheap, it is a config constant. The window's
   value matters far less than the structural absence of reversal handling, which
   D10 addresses.
4. **Soulbound credential issuance is not simply `NftMint`.** · default: assume
   BRONZE→DIAMOND credentials need a distinct non-transferable node with a revoke
   path · to reverse: re-model after ArdaNova has minted live credentials.
5. **The no-interest constraint forbids any interest-bearing primitive in AZOA
   flows.** · default: no time-value/interest node is introduced; the loan tracker
   is principal-only · to reverse: requires scholar review, not just code.
6. **Identomat has one set of base URLs (no separate sandbox host).** · default:
   drive environment separation by key + `configId`, not by URL · to reverse:
   config-shape change across appsettings + deploy secrets.
7. **`clientUserId` is the avatar↔person binding carrier.** · default: send the
   AZOA avatar id · to reverse: re-issue sessions; existing sessions lose their
   binding. ⚠ **D28 renames this concept — check the wire contract, not just the
   C# name.**
8. **Integration + SDK vitest suites are green.** · default: assumed from last
   known state, **never verified since 2026-08-21** · to reverse: run them before
   trusting any "done". *Highest-value cheap check available.*

*Resolved and removed:* the former assumption 0 (per-tenant vs global uniqueness
scope) — settled by **D23**. The former assumption 5 (two Identomat hosts) —
confirmed true by D24 research and now implemented.

---

## 5. Environment & gotchas

- **Branch `feat/kyc-aml-providers-value-rail` @ `9f4e026`, clean, unpushed**
  (286 files). Main branch for PRs is `main`.
- **Local stack:** `./dev-up.sh` / `dev-up.ps1`. Frontend :3000, WebAPI :5000,
  SurrealDB :8000. Full procedures live in the **root** [`RUNBOOK.md`](../RUNBOOK.md).
- **Podman/WSL gotchas** (recurring, see `dev-stack-live-run-gotchas`): the WSL
  socket dies → `wsl --shutdown`; a Flask process squats port 5000; you **must**
  rebuild the image after schema/backend changes or the API crash-loops.
- ✅ **Frontend gates are REQUIRED, not forbidden.** `npx tsc --noEmit`,
  `npm run build`, eslint and `npm test` all pass and must be run. *(The old
  "no-frontend-typecheck" rule is dead — see §2.)*
- **Reaching the live database** (verified 2026-09-04): it is internal-only
  (`http://surrealdb-3x--latest.railway.internal:8000`), so query it from inside
  the network — `railway ssh --service azoa-api`, then `POST $SurrealDb__Endpoint/signin`
  with `{ns,db,user,pass}` read from the container's own env, and use the returned
  token as `Authorization: Bearer`. ⚠ **HTTP Basic (`curl -u`) is rejected** for
  the database-scoped `azoa_runtime` user on SurrealDB 3.2.0. Credentials never
  need to leave the container. Live version is **3.2.0** (the runbook elsewhere
  cites 3.1.4 for the empirical NONE-semantics test — that test's conclusion still
  holds, but note the version gap if you re-run it).
- ⚠ **CORRECTED 2026-09-06 — the statement that stood here was inverted, and it
  had been repeated across this runbook all session.** It claimed a deployment
  materializes from `CompatibilityBaselines/` "NOT from `Generated/Schemas/`".
  The actual apply path is `docker-entrypoint.sh:67-73` (`prepare_schema_directory`):
  it copies **`Generated/Schemas/*.surql` first — 60 files, the base layer — then
  overlays `CompatibilityBaselines/*.surql` — 6 files — on top**, overwriting only
  those six names, and passes the merged directory as `--schemas-dir` alongside
  `--migrations-dir Migrations` (10 files).
  **So `Generated/Schemas/` IS the deployed artifact for 54 of 60 tables.**
  The overlay covers exactly six: `admin_bootstrap_state`, `bridge_tx`, `holon`,
  `kyc_submission`, `operation_log`, `swap_state`. Regenerating a golden **is**
  sufficient for every other table; for those six the baseline wins and a
  golden-only change is silently discarded. That is precisely the §8 near-miss —
  and it bit `operation_log`, one of the six, which is why the lesson looked
  general when it was not. On the live node the applier is a **per-file checksum
  ledger** (`schema_migration`).
- **Never hand-edit `.surql`** — regenerate through the documented golden path.
- **Renaming a column inside a UNIQUE index needs a forward migration**, not a
  golden regeneration.
- **Identomat credential names** (values live in the host secret store, never in
  repo): `Kyc__Identomat__CompanyKey`, `Kyc__Identomat__ConfigId`,
  `Kyc__Identomat__CallbackApiKey`.
- **Identomat rate limit: 600 requests/minute per IP; exceeding it blocks the IP
  for one hour** with HTTP 529 — one shared budget across KYC *and* AML screening.
  A naive polling loop locks the production node out of its own KYC provider.
- **Identomat callbacks authenticate with a shared secret in the JSON body**
  (`callbackApiKey`), not a signature header. Constant-time compare,
  independently rate-limited, never echoed into logs or error responses.
- **`Kyc:Identomat:ReplicaCount` must be raised before any horizontal scale-out.**
- **`Program.cs:1378-1382`** carries a comment describing a Wormhole config flag
  that no longer exists.
- **Research already captured — do not re-fetch:**
  `.omc/research/identomat-kyc-api-2026-08-20.md`,
  `.omc/research/identomat-aml-screening-2026-08-21.md`.

### Adversarial note on D1 (asked for explicitly)

The case *for* one-person-one-avatar is Sybil resistance: if KYC gates value, one
human minting N verified avatars multiplies every per-identity allocation,
airdrop, quota and vote — and AML regimes prefer one traceable natural person.

That case loses here for a reason specific to this architecture: **the stated
goal is two consumers each driving their own avatars.** A single human using both
ArdaNova and Taawun would, under a uniqueness rule, be forced onto one shared
avatar spanning two tenants — collapsing exactly the isolation boundary the
cross-tenant sweep and consent-gate work exist to defend. Uniqueness would also
make the person key a cross-tenant correlation oracle.

So the Sybil concern is real but solved at the **wrong layer** by a global
constraint. D3 moves it to the operations that need it. **Residual risk to
accept:** any *new* value operation that implicitly assumes one-avatar-per-human
is now wrong by default and must opt in to the person-key check. Flag that in
review of every new Tier-2 node.

---

## 6. Key files

**Custody — the Phase 3 gate (D27)**
- `Services/Custody/PlatformKeyDerivationRegistry.cs`, `IPlatformKeyDerivation.cs`,
  `PlatformKeyDerivations.cs` — the seam a KMS/HSM adapter plugs into.
- `Services/Custody/KeyCustodyService.cs` — the single custody chokepoint.
- `Services/Custody/KeyRotationService.cs`, `FilePendingRotationKeyStore.cs`.
- `appsettings.json:207` — `CustodialAccounts:CustodyMode = "Disabled"`.

**Rename surface (D28) — highest-risk spots**
- `Persistence/SurrealDb/Models/` — decorated POCOs; schemas regenerate from these.
- `Persistence/SurrealDb/CompatibilityBaselines/` — a **6-file overlay**
  applied *on top of* the goldens at deploy; it wins for those six tables only.
- `Persistence/SurrealDb/Generated/Schemas/` — goldens; regenerate, never edit.
- ⚠ `KycIdentity.avatar_ids`, `hosted_session`'s admission key, and the
  `(tenant_scope, provider, subject)` index all carry the old noun **inside UNIQUE
  indexes** → forward migration, not regeneration.
- `sdk/azoa-wallet/` — must be rebuilt (`tsup`) or the frontend cannot resolve
  renamed types (`sdk-renamed-oasis-sdk`).
- Consumers: Taawun `pkg/financial/azoa.go` (`CreateQuestRequest`), ArdaNova's
  integration contract.

**Value rail / providers**
- `Interfaces/IValueRailProvider.cs`, `Interfaces/IBlockchainProvider.cs`.
- `Providers/Blockchain/BlockchainProviderFactory.cs` — registration, boot guard,
  `TryGetModule<T>`.
- `Providers/Blockchain/Algorand/AlgorandProvider.cs` — reference quality; copy its
  `BuildSignSubmitAsync` discipline.
- `Providers/Blockchain/Ethereum/EthereumProvider.cs` (1860 ln),
  `Solana/SolanaTransactionSigner.cs`.
- `Services/CrossChainBridgeService.cs` (1544 ln).
- `tests/AZOA.WebAPI.Tests/Conformance/ProviderConformanceHarness.cs` — 77 tests,
  auto-enrolling.

**KYC / AML**
- `Interfaces/Providers/IKycProviderService.cs`, `IKycProviderRegistry.cs`.
- `Services/Kyc/KycGateService.cs`, `ValueAccessService.cs`, `RealValueKycGate`.
- `tests/AZOA.WebAPI.Tests/Kyc/SurrealUniqueIndexNoneSemanticsTests.cs` — pins the
  NONE-collision semantics the rename depends on.

**Action (Quest) / DAG**
- `Services/Actions/Handlers/` — 44 handlers; 7 chain-gated.
- `Services/Actions/ChainCapabilityGate.cs` — rail-aware Tier-1/Tier-2 enforcement.
- `Services/ActionDagValidator.cs` — server rules the client must mirror.

**Fiat**
- `fiat-sidecar/src/routes/checkout.ts`, `webhook.ts` — fiat **in** only.
- `sdk/azoa-wallet/src/orchestration/stripe.ts` — discards reversal events (D10).

**Frontend builder**
- `frontend/src/components/action-builder/` — `action-canvas.tsx` now **144 lines**,
  plus `panels/`, `hooks/`, `run/`, `__tests__/`.
- `panels/canvas-toolbar.tsx` — ⚠ the open `readOnly` gap.
- `dag-warnings.ts` — mirrors the server validator; keep it in sync.

---

## 7. Continuation plan

> Phases 0, 1, 1B, 2 and 4 (steps 19/21/22) **SHIPPED** — do not re-execute.
> Their verdicts are in the §2 review ledger; their lessons in §8; their
> step-by-step instructions were pruned 2026-09-04.

**D51 — STAR/STARODK becomes ActionGraphBundle; no forward migration.**
Shipped `10f4c16` (126 files, 21 file renames so each filename matches the type
it declares). `star_odk` was a real SurrealDB table with `record<star_odk>` FKs on
three SCHEMAFULL tables (`ecosystem` REQUIRED, `ecosystem_node` discriminator,
`dapp_series`), so the rename looked like the avatar/quest case that forced
wipe-and-reseed. It is not: the live node holds **0 rows** in `star_odk`,
`ecosystem` and `dapp_series`, so regenerating the goldens is sufficient and the
table rename rides the D31 wipe already scheduled. Re-verify the row counts if
D31 slips past a seeding run. **This rename is what D50 builds the ARDA↔project-
token exchange on** — "an Action DAG plus an ActionGraphBundle" only parses once
this decision has landed the type; see D50's own note distinguishing the old
`STAR`/`ActionGraph` field-name collision from this class name.

⚠ **Sixth instance of the recurring class, and the first where the *guard itself*
was the claim.** `AttributePocoByteEquivalenceTests` auto-authored a golden on a
MISSING file, so a table rename made it write the new golden and pass 60/60 while
three tables still carried `record<star_odk>` and the node-type enum still read
`"StarGenerate"`. A drift guard that creates what it fails to find cannot detect a
rename — the exact drift it exists to catch. A missing golden is now RED; only
`AZOA_REGENERATE_GOLDENS=1` or a zero-byte file authors. Proven by hiding
`ecosystem.surql` and watching the suite fail. Corollary for §6: **when a guard
has a self-healing path, ask what it heals *over*.**

Two traps the proof phase caught before any edit: `StarTable` lowercases to
"startable" and would have collided with the `LoadStartableActionAsync` family;
the lucide-react `<Star>` icon is an unrelated third-party symbol. Both intact.
### ▶ RESUME HERE (state as of 2026-09-07)

**Branch `feat/kyc-aml-providers-value-rail`, tree CLEAN, 20 commits ahead of
`9f4e026`, none pushed.** ArdaNova at `03ab6d2`, Taawun at `94ab2e4` (its first
CI ever). All three repos clean apart from ArdaNova's ~81 uncommitted
security-hardening files, which are deliberately untouched.

**Gates:** build 0 errors / 23 warnings · backend **2688 passed, 1 skipped, 0
failed** · SDK tsc 0 + **204** · frontend tsc 0 + **78 across 13 files** + build
**43 routes**.

**Re-entry:**
```
python scripts/d28_rename.py verify     # expect: 0 files left to change
dotnet build azoa.sln --no-incremental  # expect: 0 errors, 23 warnings
```

#### The one lesson this session kept re-teaching

**A claim in prose is not a control in code.** It recurred five times, twice by
my own hand:
1. The AML decision was inert — `ScreenAsync` had zero production callers.
2. `GetByGlobalPersonKeyAsync` sat with zero callers; D3's uniqueness opt-in was
   designed and never wired. **Now fixed** — `IPersonUniquenessGate`.
3. Governance's tally reads raw holons, so the gate protects the write path while
   the *consumer* of its result reads an unprotected one. **Open** — track P1.
4. "Not enabled" was asserted in the runbook, both track specs and a builder
   badge while every handler was registered and dispatchable. **Now fixed** —
   `Services/Actions/NotEnabledActionNodeTypes` refuses at publish and at BOTH
   dispatch seams, with 13 tests including two that fail if a seam stops
   consulting it.
5. The integration contract's boundary rule 1 asserted a CI-enforced no-brand-leak
   rule that was enforced nowhere and violated 15 times. **Now corrected.**

When writing "X is gated/disabled/enforced", grep for the enforcement first.

#### Next: the two design tracks are ready to implement

- **P1 [`governance-vote-of-record_20260906`](tracks/governance-vote-of-record_20260906/spec.md)**
  — D39/D40/D42. Live Sybil hole; blocks Phase 5 closure.
- **P2 [`credential-third-party-issuance_20260906`](tracks/credential-third-party-issuance_20260906/spec.md)**
  — D41. Inert, not dangerous. ⚠ Has ONE open question that does not follow from
  D41 and must be answered first: **who may issue a credential to another user**
  (role-gated, recipient-consent-gated, or open)? Open issuance lets a griefer
  spam credentials onto an identity.

Both plans open by writing the attack test and requiring it to FAIL on current
code, because every defect they correct came from a guard nobody wrote.

#### Blocked on the operator, not on work

- ⛔ **`npm publish`** — `~/.npmrc`'s token 401s on `whoami` (expired/revoked).
  Registry reads work; writes do not. `npm login` needs a TTY, and this session
  has no browser tool and no authorized mail connector. **Interim:** both
  consumers build from SDK source by symlink — AZOA's frontend via
  `file:../sdk/azoa-wallet` (permanent and correct for a monorepo sibling),
  ArdaNova via `npm link` (manifest left honest at `^0.3.0`). ⚠ ArdaNova's link is
  machine-local and invisible to git, so its CI stays red until the publish lands.
  ⚠ **Never re-copy the SDK into a consumer's `node_modules`** — a copy is what
  went 7 weeks stale and hid two `bridge/page.tsx` defects; a link cannot drift.
- ArdaNova's leaked DB credential in history (`85ba62e`, `61cc743`) — owner
  deferred explicitly; not true production yet.

---

### Next session — ordered by dependency

**A. ✅ DONE 2026-09-04 — CONFIRMED NO-OP. No compromised rows exist.**
Measured directly against the live Railway `azoa` database (SurrealDB **3.2.0**,
service `SurrealDB 3.x (Latest)`, project `ardanova`):

| Query | Result |
|---|---|
| `SELECT count() FROM wallet GROUP ALL` | **0** |
| `SELECT chain_type, count() FROM wallet GROUP BY chain_type` | **empty** |
| `… WHERE string::lowercase(chain_type) CONTAINS "eth"` | **0** |

The `wallet` table exists with correct schema but **has never held a row**, so the
`GenerateEthereumKeypair` defect (§8) never reached production data. Nothing to
delete, nothing to regenerate. The §8 record stays as the reason the code was
fixed; the *live-data* half of D29 is closed.

**Access recipe** (the internal endpoint is not reachable from a workstation):
`railway ssh --service azoa-api` → `POST $SurrealDb__Endpoint/signin` with
`{ns,db,user,pass}` from the container's own env → use the returned token as
`Authorization: Bearer`. ⚠ **HTTP Basic (`curl -u`) is rejected** for the
database-scoped `azoa_runtime` user on 3.2.0 — `/signin` + Bearer is the working
path. Credentials never leave the container.

**B. ✅ THE TERMINOLOGY SWEEP (D28) IS DONE — all four waves, 2026-09-05.**
Every gate green; `verify` is now idempotent (**0 files left to change**).
Working tree: **327 renames, 693 modified files**, uncommitted.

| Gate | Before | After |
|---|---|---|
| `dotnet build --no-incremental` | 0 errors, 23 warnings | **0 errors, 23 warnings** |
| `dotnet test` (backend) | 2431 / 1 skipped / 0 failed | **2431 / 1 skipped / 0 failed** |
| SDK `tsc` + vitest | *never measured* | **exit 0 · 191 passed, 16 files** |
| frontend `tsc` + build + vitest | exit 0 · 65 passed | **exit 0 · build OK · 65 passed** |

Residual old-noun tokens across `Controllers Services Models Interfaces Managers
Persistence tests sdk frontend conductor`: **zero**. Routes now build as
`/actions`, `/users`, `/user-nfts`. The third-party
`frontend/src/components/ui/avatar.tsx` and its `<Avatar>` importer are
untouched, as designed.

⚠ **The frontend's `tsc` gate had been passing vacuously.** `frontend/node_modules/azoa-sdk`
is a **copy**, not a symlink (npm `file:` dependency), and it was **7 weeks
stale** — dated Jul 18. Refreshing it to the rebuilt SDK exposed two
**pre-existing** defects that the stale copy had been hiding, both now fixed in
`frontend/src/app/(dashboard)/bridge/page.tsx`:
- `azoa.api.completeBridge(bridgeId)` — **this method has never existed**, in the
  SDK or at HEAD, and there is no `complete` route on `BridgeController` (the
  lifecycle is `initiate → fetch-vaa → redeem`). Corrected to `redeemBridge`.
- `amount: Number(form.amount)` — the SDK types bridge amounts as **`string`**
  ("unsigned base-unit amount in 1..2^64-1"); coercing through JS `number` is
  precision-destroying **on a value path**. Corrected to pass the string.

*Lesson, and it generalises past this rename:* a green typecheck against a stale
copied dependency proves nothing. This is the same class as the §8 "three green
gates and the builder did not render" finding. **After any SDK change, refresh
`frontend/node_modules/azoa-sdk` before trusting a frontend gate.**

**Baselines were re-based, not edited.** The 6 `CompatibilityBaselines/*.surql`
checksums pinned in `SurrealDeploymentLeastPrivilegeContractTests` changed
because the rename touched the files. Re-pinning was legitimate *only* because
D31 wipes the live database; the generation-1 checksums are retained in that
file's comment so the re-base is auditable. **Had real data existed, the rename
would have had to ship as a forward migration and those checksums stay frozen.**

**Goldens regenerate via `AZOA_REGENERATE_GOLDENS=1 dotnet test`** — the in-repo
escape hatch in `AttributePocoByteEquivalenceTests`. The `surrealforge` CLI is
still not installed and `.config/dotnet-tools.json` is still missing; the env-var
path is what actually works today.

**📁 Archived as a conductor track:**
[`terminology-neutralization-cutover_20260905`](tracks/terminology-neutralization-cutover_20260905/spec.md)
— spec, wave-ordered plan, and `metadata.json` carrying the verified partitions,
the measurement set, the never-rename list, and the review verdict. Registered in
`tracks.md` under **In flight**. *That track is now the detailed record; this
section stays as the operational summary.*

**✅ Consumer cutover DONE 2026-09-06** (track phase 5 has the detail):

- **ArdaNova** — build 0 errors, **1050 .NET tests**, client `tsc` 0 and
  **210 tests**. A **CRITICAL silent break** was found and fixed: its
  `StatusWire.AvatarId` had no `[JsonPropertyName]`, so post-rename it would
  deserialize `null` without error and fail-close **every value action for every
  user** through `AzoaValueEligibilityService`. Routes, the credential-scoping
  allowlist prefix, doc-comments, tests and the e2e harness all updated.
- **Taawun** — **no change required, verified.** Zero AZOA HTTP calls, `AzoaClient`
  inert (`ErrLegacyAzoaDisabled`), **zero `Avatar` references in any Go file**.
  Its `Quest*` vocabulary is local orchestration state behind its own REST
  surface; per D28's own principle it stays.
- **Integration contract** — now **byte-identical** across both repos. ⚠ They had
  **already drifted before this work** (ArdaNova's copy still said `oasis-sleek/`),
  so the file's own sync invariant was being violated silently.

⛔ **STILL BLOCKING: `azoa-sdk@0.3.0` is unpublished, and this machine cannot
publish it.** `~/.npmrc` holds a literal `_authToken` for `registry.npmjs.org`
that returns **401 on `npm whoami`** — expired or revoked. Registry *reads* work
(`npm view azoa-sdk version` → `0.2.2`); writes do not. `npm login` needs an
interactive TTY, and this session has no browser tool and no authorized mail
connector, so the token cannot be renewed from here. **`npm login` in a terminal,
or a fresh `NPM_TOKEN`, is the only unblock.**

✅ **Interim, both consumers build from SDK source via symlink — not from the
registry.** This is deliberate and was authorized as a testing measure:

| Consumer | Mechanism | Manifest |
|---|---|---|
| AZOA `frontend/` | `"azoa-sdk": "file:../sdk/azoa-wallet"` → npm links it; lockfile records `"link": true` | **changed** — correct permanently for a monorepo sibling |
| ArdaNova `ardanova-client/` | `npm link azoa-sdk` (global link, no manifest edit) | **unchanged**, still `^0.3.0` — honest about what CI needs |

⚠ **Two consequences to hold onto:**
1. **ArdaNova's link is machine-local and invisible to git.** A fresh clone, a
   different machine, or CI still resolves `^0.3.0` from the registry and fails
   until the publish lands. Green locally ≠ green in CI here.
2. **Never re-copy the SDK into a consumer's `node_modules`.** A *copy* is what
   silently went 7 weeks stale and hid two `bridge/page.tsx` defects; a *link*
   cannot drift. If a gate needs a refresh, re-link — do not `cp -r`.

Verified against the linked source: AZOA frontend tsc 0 / 65 tests / 42 routes
built; ArdaNova client tsc 0 / 210 tests across 33 files.

**Still owed on B (track phases 6–7):**

1. **Strands 2 and 3** — `report-vocab` locates them; they are sentence-level
   rewrites, not token swaps.
2. **Deployment reconciliation** — step C below (D29/D31).

---

*Original plan, retained for the record:*

**The transform is `scripts/d28_rename.py`** (D33). It is the keystone artifact:
one bug corrupts ~25,646 sites in one pass, so it carries its own proof and is
reviewed before it ever runs.

```
python scripts/d28_rename.py verify        # self-check, must pass before apply
python scripts/d28_rename.py plan  <part>  # per-file counts, writes nothing
python scripts/d28_rename.py apply <part>  # rewrites in place
python scripts/d28_rename.py report-vocab  # locates strand 2/3, never rewrites
```

⚠ **`verify` is a gate, not a formality**, and its *first* form was worthless.
It asserted `quest − request − question == guarded-matches`, which is a
**tautology** — algebraically it reduces to "no substring `requestion` exists"
and is blind to a third false-positive family. `bequest` is counted on both
sides and sails through; the tree contains `bequest` exactly once, in the
runbook line that claimed it didn't. The gate now does real work:

- classifies every distinct `quest`-bearing token and protects a **denylist** of
  non-domain English words (`bequest`, `conquest`, `inquest`, …);
- reports `...reQuest` **over-blocks**, where the `re` lookbehind refuses a real
  domain token (found: `BuildOnFailureQuest`, pre-edited);
- asserts **every file the sweep would change is claimed by exactly one
  partition** — the check that caught `Helpers/` being in no partition at all.

Measured 2026-09-05: 577 domain tokens · 1 over-block (handled) · **958 files
changed, 958 claimed, 0 unclaimed** ✅

**Validated end to end on wave 1** (applied to the real tree, built, tested,
reverted): `dotnet build --no-incremental` → **0 errors, 23 warnings — exactly
the pre-rename baseline**, so the rename introduces no new warnings. `dotnet
test` → **2387 passed, 44 failed**, and all 44 are accounted for:

| Failures | Cause | Status |
|---|---|---|
| 36 | `AttributePocoByteEquivalenceTests` — POCOs renamed, goldens not yet regenerated | **Expected**; resolves at the between-waves regeneration |
| 6 | `CompatibilityBaseline_RetainsTheShippedChecksum` — the baselines were deliberately renamed, changing their pinned checksums | **Expected**; the immutability contract firing. Re-pin explicitly, never by copying whatever the build emits |
| 1 | `BrandLeakGuardTests` — `Path.Combine("Services","Quest","Handlers")` is a **directory** literal that got the *type* name | **FIXED** via pre-edit → `"Actions"` |
| 1 | `NodeOperatorSecurityContractTests` — `ReadRepositoryFile(…, "SurrealAvatarStore.cs")` is a **filename** literal | **Not a bug**; renames correctly, needs `git mv` to land |

**Waves (dependency-ordered, disjoint file sets — never split by noun):**

| Wave | Partitions | Files / sites | Gate before advancing |
|---|---|---|---|
| 1 | `models`, `backend`, `backend-root`, `baselines`, `tests` | ~625 / 18,157 | `dotnet build --no-incremental` — expect **CS0104/CS0118** as the collision signature; then regenerate goldens |
| 2 | `sdk` | 19 / 954 | `tsc` + **`tsup` rebuild** — the frontend resolves the built package |
| 3 | `frontend` | 73 / 1,011 | `tsc --noEmit`, `npm run build`, `npm test` (65) |
| 4 | `docs`, `misc` | ~162 / 5,528 | none (no build dependency) |

**Strands 2 and 3 are NOT auto-rewritten.** "riba-free" → "no interest-bearing
primitive may exist in a flow" is a sentence rewrite; a token swap yields
garbage. `report-vocab` locates every site; an agent rewrites the prose.

**Between waves 1 and 2:** regenerate goldens from the renamed POCOs via the
documented path, never by hand — `surrealforge generate-from-assembly` +
`flowcharts-from-assembly` (see the D28 block in §3).

**Directory/file renames are a real step, not a tidy-up.** `python scripts/d28_rename.py paths`
emits the **347** `git mv` commands, deepest-first. Two reasons it cannot be
skipped or deferred past the build:
- **Next.js route directories are load-bearing.** The sweep rewrites hrefs
  `/quests` → `/actions`, but `frontend/src/app/(dashboard)/quests/` only moves
  here. Skip it and every rewritten link 404s.
- **35 frontend import specifiers dangle** until the move
  (`@/components/action-builder/action-canvas`, `./action-node` ×11, …), and
  two C# contract tests read source files by path.

⚠ **`surrealforge` is NOT on PATH and `.config/dotnet-tools.json` does not
exist** (it was deleted at some point — §5 flagged the deletion long ago and it
was never resolved). The goldens *were* regenerated during the validation runs,
so an in-repo mechanism exists, but **the exact regeneration command is
unconfirmed**. Establish it before wave 1's closing gate rather than discovering
it with 51 renamed POCOs in the tree.

**Then the consumer repos:** ArdaNova (scoped — see §3; note its .NET backend
breaks at **runtime**, not compile time). Taawun is deferred per D34.

**C. One combined deployment reconciliation (D29)** — after B. Apply the new
tables, the `operation_log.amount` forward migration, and the rename migration
together. Mind the real layering: the goldens deploy for 54 of 60 tables and
`CompatibilityBaselines/` overlays only six of them.

**Live-state recon done 2026-09-04 — the divergence is now measured, not assumed:**

- **All 7 Phase 1/1B tables are absent from the live database**, confirming D29:
  `hosted_session`, `kyc_identity`, `kyc_screening_monitor`,
  `kyc_screening_poll_state`, `kyc_screening_result`, `user_external_login`,
  `oauth_exchange_code`. *(Earlier KYC tables **are** present:
  `kyc_authority_guard`, `kyc_control_audit`, `kyc_document`,
  `kyc_provider_profile`, `kyc_submission`, `tenant_kyc_provider_selection`.)*
- **`operation_log.amount` is live as `none | int`** — the un-widened i64 the §8
  near-miss predicted. The forward migration is genuinely required.
- **The live database is nearly empty: `avatar` 6 rows, `api_key` 2 rows,
  everything else 0** (`quest`, `holon`, `operation_log`, `wallet`,
  `wallet_auth_challenge` all zero). ⚠ **This materially lowers step C's risk** —
  there is essentially no data to convert. It does **not** relax the schema
  discipline (a wrong UNIQUE index is still wrong on an empty table), but it does
  mean a data-loss incident is close to impossible, and it strengthens the
  `greenfield-prelaunch-no-compat` case for a clean rename.
- **`schema_migration` is a per-file checksum ledger**, applied by
  `azoa-api/docker-entrypoint` and `surrealforge/reset`; newest entry
  **2026-07-19**. **`data_migration` is EMPTY — no data migration has ever run on
  this node.**
  ⚠ **Open question step C must answer first:** the ledger keys on
  `file_name` + `checksum`. D28 both *renames* schema files and *changes* their
  contents, so determine whether SurrealForge re-applies a file whose checksum
  changed, or skips it because the name already has a row — and what it does with
  a `avatar.surql` → `user.surql` rename (new row, or orphan?). Answer this
  against the tool before running anything.
- ⚠ **`wallet` carries a UNIQUE index on the renamed noun** that the D28 inventory
  did not list: `wallet_avatar_chain_address UNIQUE(avatar_id, chain_type,
  address)`, plus non-unique `wallet_avatar_chain(avatar_id, chain_type)`. Field
  `avatar_id` is typed `record<avatar>` — **a table rename changes the record-link
  target type, not just the column name.** Sweep for every `record<avatar>` /
  `record<quest>` typed field, not only for columns whose *name* contains the noun.

**D. Production KMS/HSM custody (D27)** — the gate on all of Phase 3. Plug into
`PlatformKeyDerivationRegistry`; it replaces the *derivation*, not the chokepoint.
Escrow also needs a **hold**, which exists on neither path today — scope it
explicitly rather than discovering it mid-implementation.
→ **`/security-review` is non-negotiable after D** (key-management path).

**E. Phase 5 consumer primitives (steps 24–27)** — unblocked; step 27's dependency
landed with USD-at-time stamping. Renamed per D28: step 26 is a **principal-only
loan tracker**. Steps 25/26 are pure-DB and carry no custody exposure, so they can
run in parallel with D.

**F. Phase 3 fiat (steps 12a, 14–20)** — gated on D. Start with the **12a design
session, not a fan-out**: the review established that fanning out against an
unknown fiat contract is how the retracted D7 happened.
→ **`/security-review` after the payout step and again after the escrow-release
step.** Money outbound is the most attack-worthy fiat surface, and escrow release
across a reversible fiat leg and an irreversible chain leg is the money-printer
path (D10) — a bug class this codebase has shipped twice.

### ⚠ Open: four sentences where `User` collided with the English word "user"

The rename mapped the *entity* `Avatar`→`User`, but prose in these four places
already used "user" as a **different, generic** concept alongside it. Both are
now the same word and the sentences read as nonsense. Fixing them means
**choosing replacement terminology** (candidates: "wallets", "identities",
"accounts", "principals") — a content decision, not a grammar fix, so the repair
pass deliberately left them:

- `docs/DEV-DOCS.md:63` — "Users own their users, authenticating…"
- `docs/DEV-DOCS.md:73` — "End-user users are self-sovereign; tenants can only
  act on behalf of a user…"
- `README.md:108` — "End users own their users (wallet-challenge login…)"
- `conductor/retros/HANDOFF-ardanova-provider-port.md:115` — "tenant User owns
  fleet of user Users"

*Why this class exists at all, worth carrying forward:* a rename whose target
noun is already a common word in the surrounding prose will silently collapse
two concepts. Grep the target noun in prose **before** the rename, not after.

### Outstanding, unscheduled

- **Run the integration + SDK vitest suites** — status genuinely unknown since
  2026-08-21 (assumption 8). Cheapest available risk reduction.
- ~~`CanvasToolbar` `readOnly` gating~~ — **closed as a non-issue**, see §2.
- **Operator funding / treasury** — Algorand fee-payer balances, Stripe payout
  balance sufficiency, and reconciling `NodeTreasuryManager` holdings against
  outstanding obligations appear nowhere. Resolve in 12a.
- **`read balance` is an abstraction mismatch for fiat** — Stripe exposes the
  *platform* balance, not a payer balance. Resolve in 12a.
- **KYB counterparty screening has no vendor path** (D24 negative finding) —
  product/compliance escalation.
- **`Kyc:Identomat:ReplicaCount`** before horizontal scale-out.
- **`Program.cs:1378-1382`** stale Wormhole flag comment.

---

## 8. Session log

Compressed 2026-09-04 to the findings that still change behavior.

### 2026-08-21 — Execution session (Phases 0, 1, 1B, 2, 4)

**Shipped:** Phase 0 honesty fixes · Phase 1 Identomat KYC + AML + `KycIdentity` +
`HostedSessionFlow` · Phase 1B OAuth · Phase 2 steps 8–11 + FR-6 conformance ·
chain-aware custody door (67 tests) · Phase 4 steps 19/21/22 · first-ever
`frontend/` test harness. **Decisions settled:** D23–D26.

#### ⚠ ACTION REQUIRED — pre-existing Ethereum wallet rows are hazardous (drives §7 step A)

`Services/Signing/WalletKeyService.GenerateEthereumKeypair()` was a stub with two
**coupled** defects:

1. The stored `wallet.Address` was derived with **HMAC-SHA256 + SHA-256**, not
   secp256k1 + keccak-256 — the address was unrelated to the private key.
2. The "recovery phrase" was `idx = (seed[i]*256 + seed[i+1]) % 2048`. Because
   **2048 = 2¹¹**, each word discloses byte `seed[i+1]` in full plus the low 3
   bits of `seed[i]` — the 12 words leak **~99 bits of the private scalar** to
   anyone who saw the "backup", and restore the wallet in no wallet on earth.

Defect 2 was inert only while defect 1 made the address fake. **Fixing the address
alone would have turned a broken stub into a live key-disclosure channel.** Both
are fixed (phrase returns `null`; derivation shares the exact `EthECKey` call the
provider and signer use, so keygen cannot drift from the value path). Verified
against published vectors — scalar `1` reproducing the SEC 2 §2.4.3 generator
point, which proves real point multiplication rather than a hash.

**Any Ethereum wallet row created before this change must be deleted and
regenerated, not migrated.** Pre-launch, so deletion is acceptable
(`greenfield-prelaunch-no-compat`).

*Related, not yet done:* `Managers/WalletAuthManager.cs:564` and
`Providers/Stores/Surreal/SurrealWalletAuthChallengeStore.cs:109,176` compare
addresses with `StringComparison.Ordinal` / raw SQL equality. Stored Ethereum
addresses are now mixed-case **EIP-55** where they were lowercase. Nothing breaks
today (external-wallet auth paths) but they should normalize through
`EthereumAddress.TryParse`.

#### The frontend lesson — three green gates proved nothing

Phase 4 steps 19/21/22 passed `tsc`, `eslint` and `npm run build` — all green —
and **the quest builder did not run at all.** An adversarial review built a jsdom
harness against the real `@xyflow/react` 12.11.0 and measured:

- **Infinite render loop on mount.** `onSelectionChange` was an inline arrow, and
  React Flow's `SelectionListenerInner` puts that callback in its effect deps
  while `<ReactFlow>` is `fixedForwardRef`, **not `memo`**. 301 parent renders +
  "Maximum update depth exceeded", with *and* without StrictMode.
- **Copy/paste was dead code.** A controlled-selection projection overwrote
  `.selected` from a list fed only by the event fed only by that same prop — a
  closed loop seeded at `[]`.

Root cause of both escaping: `frontend/` had **zero test dependencies**, so
nothing ever executed these components. **Regression tests must be written to fail
first** — a test that never failed is precisely how these shipped.

#### Two near-misses worth remembering

**The idempotency-key rename that almost broke replay protection.** `RailAmount`
replaced `ulong` on the Mint path, and `DeriveIdempotencyKey` read
`.Amount.ToString()`. `RailAmount.ToString()` renders `"1000000 base units"` — the
obvious mechanical port would have silently re-hashed **every** key, so an
operation already claimed under the old spelling would stop matching itself. It
now reads `.ToBaseUnitString()`, pinned by goldens computed from the *pre-change
specification in a separate script*, not by capturing what the new build emits.
**Directly relevant to D28:** a rename that touches any value used in a derived
key is not mechanical.

**Regenerating the schema golden was not sufficient.** A deployed database is
materialized from the goldens **overlaid by** `CompatibilityBaselines/`, then
forward migrations — and `operation_log` is one of the six tables that overlay
covers, so the regenerated golden alone never reached the deployment. `CompatibilityBaselines/operation_log.surql`
still declared `amount TYPE option<int>` (SurrealDB `int` is i64 — half what the
interface already accepted), so shipping only the regenerated golden would have
left every deployment rejecting the widened column at the SCHEMAFULL boundary. A
forward migration now widens → converts → pins, and a test pins the migration
against both the baseline and the golden.

#### Pattern worth carrying forward

Every agent found real defects *outside* its brief, because each was told to
report contradictions rather than trust the brief. Highest-value: three
fabricated-success bugs on Solana value paths (including an unknown transaction
reported `CONFIRMED` on the reconcile path); a `Providers/Kyc/AGENTS.md` claim
that would have leaked vendor session ids to tenants if trusted; a transient 503
that would have permanently denied a subject KYC; a custody chokepoint throwing
SDK exceptions that echo mnemonic words. **Briefs should keep asking "tell me what
here is stale against the real source."**

### 2026-08-20 — Readiness assessment + three-workstream scoping

Evidence-based completeness assessment; real-vs-stubbed state enumerated for all
chains by reading provider source; Identomat API researched and captured. Four
successive adversarial passes reshaped the plan before any code was written: a
fable critic **refuted the plan as written** (verified independently before
acceptance — `TransferNodeHandler.cs:31` moved an NFT by Guid; the factory was
typed `Func<IBlockchainProvider>` so a non-chain rail could not register;
`stripe.ts:159-160` discarded every dispute event; the handler count was 43, not
44). Result: D7 retracted and rewritten, D12 deleted, D13/D14 added, Phase 3 given
a design step, D15–D22 added, Phases 1B and 5 created.

**Carry-forward:** the plan was wrong in ways that only reading the source
revealed, and the review cost far less than the fan-out it prevented.

---

## 9. Recommended invocations

Lowest rung that does the job, each with the clause that selected it.

1. **Step A — inline.** One query plus one delete against the live database. No
   orchestration warranted; it must not wait behind anything else.
2. **Step B — `/slice`, wave-ordered (not a flat fan-out).** C#/schema, SDK, and
   frontend are naturally disjoint file sets, **but the SDK must be rebuilt before
   the frontend compiles**, so it needs wave ordering. The consumer repos
   (Taawun, ArdaNova) are a third wave. Route the schema/UNIQUE-index migration
   slice to **fable** — a rename inside a UNIQUE index on a live node is the one
   irreversible part.
3. **Step C — inline, with the live database open.** Verification is a reading
   task against a real deployment; a subagent cannot see the node.
4. **Step D — one high-tier agent, then `/security-review`.** Custody is a single
   coherent adapter behind an existing seam; the security review is
   **non-negotiable** per the standing rule on key-management paths.
5. **Step E — `/slice`.** Steps 25/26 are pure-DB, disjoint, and carry no custody
   exposure; they parallelize cleanly against D.
6. **Step F — design session first, never a fan-out.** Fanning out against an
   unknown fiat contract is exactly how the retracted D7 happened.
