---
type: contract
---

# AZOA Blockchain-Node Integration Contract

**Status:** Phase-1 keystone (consolidation + extension). This is the single
canonical contract for integrating with **AZOA as a generic, self-hostable
blockchain-operations node**. A copy lives in both repos:

- AZOA: `azoa/conductor/ARDANOVA-AZOA-INTEGRATION-CONTRACT.md`
- ArdaNova: `ardanova/conductor/ARDANOVA-AZOA-INTEGRATION-CONTRACT.md`

Both copies are byte-identical and must be kept in sync. Each repo then authors
its **own** conductor tracks against this contract. When a clause changes, change
it in both copies in the same commit window.

---

## 0. What AZOA is (the mental model)

**AZOA is a generic blockchain microservice — a node that anyone can run.** It
exposes a standard surface for blockchain operations (wallets, NFTs/holons,
fungible tokens, swaps, transfers) and a **action engine** for orchestrating
multi-step blockchain workflows as DAGs. It is not bespoke to any one consumer.

The contract is written in terms of three neutral roles. **ArdaNova is the first
reference consumer**, used only as a concrete example.

| Role | Who | What it does |
|---|---|---|
| **Node operator** | whoever hosts the AZOA node | Runs the service. **Custody follows the node operator** (§2). |
| **Consumer / tenant** | an application integrating AZOA (e.g. ArdaNova) | **Authors action definitions** and reads/writes via the standard API. Does **not** custody users' assets by virtue of being a consumer (§3, §5). |
| **User** | an end identity on the node | Holds its own identity; **runs actions as itself** under its own consent (§4, §5). |

> **The key correction this revision encodes:** a consumer that authors an action
> object does **not** thereby take custody of the users that interact with it.
> The consumer *publishes* a workflow definition; **any user runs its own run
> against that definition, as itself**. Custody is a property of *who hosts the
> node*, not of *who authored the action*. A consumer only acts *as* a user
> through an explicit, revocable **consent grant** (the acting-tenant path) —
> typically when that consumer is also the node operator.

### Boundary rules (non-negotiable)
1. **No brand leak into AZOA's mechanisms.** AZOA's *identifiers, config, node
   types and economic logic* know only "a consumer," "a node operator," "a user."
   ⚠ **Corrected 2026-09-07 — the previous wording was false in both halves.** It
   read "no AZOA source may contain the string `ArdaNova` … (H5 grep → CI)". No such
   CI grep exists, and AZOA source contains 15 occurrences — so the rule asserted an
   enforcement that was absent and a state that was untrue. What is actually in
   force, and IS tested (`tests/…/BrandLeakGuardTests`), is narrower: handler,
   predicate and node-config CODE must contain no tenant economic brand or
   instrument token (`ArdaNova`, `project-token`, `equity`, `vesting`). Naming the
   canonical example consumer in PROSE to explain intent is explicitly permitted by
   that guard and accounts for 14 of the 15. The 15th is one sanctioned wire alias,
   `[JsonPropertyName("ardanovaUserId")]` in `TenantCustodialAccountResponses.cs`,
   kept for the first integration's payload compatibility.
2. **AZOA owns blockchain primitives + key custody only.** All domain economics —
   token valuation, treasury splits, equity math, funding-gate *valuation*, scrum
   state, the investment/escrow records of truth — stay in the consumer.
3. **AZOA receives already-decided amounts.** It never computes economics.
4. **IDOR rule everywhere.** The target is a route value or a claim, never a
   redirectable body field. Cross-owner targets return **404**, never 403.

---

## 1. Division of responsibility

| Concern | Owner |
|---|---|
| Domain (Project/Sprint/Epic/PBI/Task/Opportunity lifecycle + records of truth) | **Consumer** |
| Treasury split, equity math, funding-gate *valuation*, token economics | **Consumer** |
| KYC document capture (AZOA-hosted Identomat session) | **AZOA node** (§1.1, D46/D48) |
| Investment / escrow record of truth | **Consumer** |
| Deciding *how much* of *which* asset to allocate / reward | **Consumer** |
| Webhook signature verification (fiat / payment provider) | **AZOA node** (§3, D45) |
| Authoring action **definitions** (templates / DAGs) | **Consumer** |
| Hosting the node; **custody of user keys** (per node operator) | **Node operator** |
| Provisioning a wallet for a user (if absent) | **AZOA node** |
| Minting / transferring the on-chain (or simulated) asset | **AZOA node** |
| Deduping a redelivered trigger so an asset moves exactly once | **AZOA node** |
| KYC **gate** (fail-closed) on the value-bearing move | **AZOA node** |
| Reconcile-before-retry so a bounty/grant never double-mints | **AZOA node** |
| Action engine that orchestrates the project→fund→work lifecycle | **AZOA node** |
| Starting / driving an action **run** (as the acting identity) | **User** (self) or **consumer under a consent grant** |

### 1.1 KYC policy is node-level, not per-tenant (D46, D48)

AZOA hosts the KYC document-capture flow (Identomat is the sole implemented
hosted-session provider today — `IdentomatKycProviderService`, the only
`IHostedSessionProvider`). **Policy — vendor, trust profile, assurance level,
AML screening — is a node-operator decision, not a per-tenant choice** (D46,
D48): the operator sets it once for the whole node. The consumer still owns
the investment/escrow record of truth (above); AZOA never becomes a system of
record for the underlying investment.

> **Deploy-step note.** `PUT /api/tenant/kyc/provider`
> (`TenantKycConfigurationController`) lets a tenant self-select its provider
> today with no operator allow-list — that is the wrong-custodian premise in a
> smaller form, and is scheduled to become an operator-gated override rather
> than the way policy is set.

---

## 2. Custody follows the node operator

Custody is determined by **who hosts the AZOA node**, not by who authored an action
or who triggered a value move:

- **Self-hosted consumer node** (e.g. ArdaNova runs its own AZOA instance): that
  operator custodies the keys of the users on it. The users are
  tenant-custodial child users; the operator is the custodian of record
  (§11.2, ratified decision #1).
- **Shared / public node**: the node operator running that instance is the
  custodian for its users. A consumer integrating against someone else's node
  holds **no** custody — it can only act through standard API calls and (for
  acting-as-a-user) an explicit consent grant.

Mechanism (shipped): the single audited custody chokepoint is
`KeyCustodyService` (`Managers/KeyCustodyService.cs`) — ownership-checked
resolve, JIT decrypt, `byte[]` zeroing. Acting *as* a user that you do not own
requires an `act_as_tenant` consent claim, checked live before key decrypt
(`tenant-consent-delegation` / `consent-gate-architecture`). A revoked grant
fails closed.

> **Production custody note.** The key STORE behind `KeyCustodyService` is still a
> config-derived data key (DEPLOY-STEP **B3**); a KMS/HSM-backed store is owed
> before a node moves real value. This is a node-operator deploy concern, not a
> consumer concern.

---

## 3. Trust & credentials (consumer → node)

A consumer authenticates to an AZOA node with an `X-Api-Key` header carrying an
AZOA API key (SHA-256 hashed at rest); controllers cannot distinguish it from a
JWT. Scopes gate capability:

- `nft:mint` / `wallet:manage` — author actions, mint/transfer assets, allocate.
- `tenant:provision` — **only** needed if the consumer provisions and acts for a
  *fleet of users it owns* (the custodial / acting-as path of §5.2). A
  publish-only consumer that lets users self-run does **not** need it.

The consumer's AZOA API key is a **deploy-time secret** (`AZOA_TENANT_API_KEY`,
bound by the consumer's secret store to `Azoa:TenantApiKey` / `AZOA__TenantApiKey`).
**Never commit or document the key value.**

⚠ **Corrected 2026-09-07 (D45) — this clause used to say the opposite.** It
read "AZOA holds no payment-provider secret — webhook verification is the
consumer's job." That is reversed: **AZOA owns the `Fiat.USD` rail, scoped to
the ARDA on-ramp and off-ramp; ArdaNova owns the funding intent and the
rate.** AZOA terminates the payment provider's webhook and holds its signing
secret; webhook signature verification moves to the **AZOA node** (line 75,
§1). The consumer never holds that secret and never re-derives a settled
amount — it declares the rate (a `TenantDeclared` rate source) and the
funding intent; AZOA settles. **As-built today:** the rail seam
(`Interfaces/IValueRailProvider.cs` — a fiat rail names itself, e.g.
`"Fiat.USD"`) exists; no fiat provider implements it yet, so Stripe
termination inside AZOA is scheduled, not shipped.

---

## 4. Identity & how a user runs an action (the corrected core)

**Actions have an author and a runner, and they are different roles.**

- **Authoring** (consumer): publish a reusable action **definition** —
  `POST /api/action` (a concrete DAG) or `POST /api/action/templates` with
  `is_public: true` (a template any user may instantiate). The author does not
  own anyone's run.
- **Running as self** (user — the default): a user starts its **own** run.
  The shipped engine scopes the run to the *calling* identity:
  `POST /api/action/{id}/start-workflow` sets `Run.user_id` = the authenticated
  caller (`ActionController.StartWorkflow`). `advance` and `signal` are likewise
  caller-scoped. **The user drives its own run, under its own consent and keys.**
- **Running on behalf** (consumer — the exception, consent-gated): a consumer
  starts/drives a run *for* a user only when an `act_as_tenant` consent claim
  is present; it is stamped onto the run (`StartWorkflowRunAsync(..., GetActingTenantId())`)
  and re-checked at every Tier-2 (value-bearing) node. Null for a plain self-run
  → no behavior change. The user can revoke the grant at any time.

So a consumer that "creates the action objects" is **publishing definitions any
user can interact with** — it does not custody those users. Custody enters
only via §2 (node operator) and acting-as enters only via an explicit grant.

### 4.1 Optional fleet mapping (only for the custodial / acting-as path)

If (and only if) a consumer owns a fleet of users (self-hosted operator, or a
consent-delegated relationship), it maps its users to users via the tenant
surface. Each child user stores `OwnerTenantId` (self-FK, server-set from the
key claim, never a body) and `ExternalUserId` (the consumer's own user id, unique
per owner — the lookup key).

| Step | Call | Notes |
|---|---|---|
| provision | `POST /api/tenant/users` `{ "externalUserId": "...", "externalRef": "..." }` | Idempotent on `externalUserId`. Requires `tenant:provision`. |
| act-as | `POST /api/tenant/users/{userId}/credential` `{ "scopes": [...] }` | ⚠ **Disabled.** Unconditionally returns `503` — "Delegated child credentials are unavailable until every accepting endpoint has an explicit scope policy" (`Controllers/TenantController.cs:158-165`). Documents the intended shape only; issues no JWT today. |
| resolve | `GET /api/tenant/users/{externalUserId}` | userId → user; 404 if not this owner's. |

(Full prose: `conductor/tracks/tenant-onboarding/ONBOARDING.md`. A
publish-only/self-run consumer skips this section entirely.)

---

## 5. The project lifecycle as an Action DAG

The consumer authors an action **definition** for the project lifecycle; a user
(the project creator, a contributor, a supporter) runs its **own** run against it.
Each **phase is a node**; **navigation between phases is a gated edge**. The AZOA
durable engine advances the run, parking at gates for signals.

### 5.1 Domain → node-type mapping (ArdaNova example)

| Concept | State machine | Action DAG representation | Who runs it |
|---|---|---|---|
| **Create project** | `Project: DRAFT→PUBLISHED` | `HolonCreate` (Project holon) → `Emit`(`project.created`) | the creator user |
| **Seek support / fund** | `SEEKING_SUPPORT→FUNDED` | `GateCheck`(funding goal met — bool injected via `reads`) → `FungibleTokenCreate` (ProjectShare ASA) and/or `Grant` | supporter user(s); consumer signals the gate |
| **Start work** | `FUNDED→IN_PROGRESS` | `GateCheck`(`status == "FUNDED"`) → `Emit`(`sprint.started`) | the creator/lead user |
| **Task / PBI / Bounty** | `TODO→…→COMPLETED`; escrow `NONE→FUNDED→RELEASED/REFUNDED` | `GateCheck`(submission accepted) → `Transfer`/`Grant`(reward) → `Emit`(`task.completed`); reject branch → `Refund`/`Emit` | the contributor user |
| **Membership credential** | soulbound ASA | `Grant` (soulbound: total=1, decimals=0, frozen) → credential Holon | the member user |

**§5.1 as-built note.** The **Create project** row above is **target-state,
not built**. A presented project lives only in the consumer's own database
today; AZOA learns of it only through the `allocation`/funding calls in §6 —
there is no `HolonCreate`/`Emit(project.created)` call at publish, and no
Project holon mirror. This is the accepted status quo (consistent with §1 —
domain lifecycle stays consumer-owned) and is revisited only when a
project's share token needs a holon anchor at `FUNDED`.

**Tier note.** `GateCheck`/`Emit`/`HolonCreate` are Tier-0/1 (no chain). `Grant`,
`Transfer`, `Refund`, `Swap`, `FungibleTokenCreate` are **Tier-2** — they require
the run's **actor user** (the self-runner, or the consented act-as target) to
have a wallet bound (`ChainCapabilityGate`, fail-closed). Because the run is
self-scoped, the wallet and keys are the user's own — the consumer never needs
custody to let the user transact.

### 5.2 Node config shapes (authoritative; `Models/Actions/NodeConfigs.cs`)

- `GateCheckNodeConfig` → `{ "predicate": "<bool expr>", "reads": { "<name>": <json> } }`
- `EmitNodeConfig` → `{ "payload": <opaque consumer json> }`
- `GrantNodeConfig` → `{ "request": <NftMintRequest>, "holonId": "<guid?>" }` (actor user from run context, NOT the body)
- `TransferNodeConfig` / `RefundNodeConfig` → `{ "nftId": "<guid>", "request": <NftTransferRequest> }`
- `FungibleTokenCreateNodeConfig` → `{ "chainType":"Algorand", "name":"", "unitName":"", "total":<ulong>, "decimals":<int>, "holonId":"<guid?>" }` (total/decimals consumer-authoritative; AZOA derives no economic meaning)

### 5.3 Run orchestration surface

| Call | Purpose | Scoped to |
|---|---|---|
| `POST /api/action` / `POST /api/action/templates` (`is_public`) | Author a definition / publish a template. | the **author** |
| `POST /api/action/templates/{id}/instantiate` | A user materializes a public template into its own action. | the **instantiating user** |
| `POST /api/action/{id}/validate` | Structural DAG validation (Kahn + entry/terminal/orphan/reachability). | — |
| `POST /api/action/{id}/start-workflow` | Start a durable run. `Run.user_id` = caller. | the **running user** |
| `POST /api/action/runs/{runId}/advance` | Resume a `Suspended` run. | the **running user** |
| `POST /api/action/runs/{runId}/signal` | Un-park an `AwaitingSignal` gate (consumer pushes "goal met" / "task approved"). | the **running user** (or consumer via consent) |
| `GET /api/action/runs/{runId}/execution-state` | Poll node states for the board. | reader |

**Parking states:** `AwaitingSignal`, `AwaitingTimer`, `AwaitingReconciliation`
(§6), `Suspended`; terminal `Succeeded`/`Failed`/`Cancelled`/`Forked`.

---

## 6. Value movement — allocation (the consumer-driven value door)

When the consumer settles fiat / decides a reward outside an action run, it can
move value directly (provision-if-absent + idempotent mint/transfer):

### `POST /api/allocation/{userId}`
- `{userId}` = the AZOA user that receives the asset (route only; IDOR-safe).
- `X-Api-Key` (must hold `nft:mint`/`wallet:manage`); `Idempotency-Key` = a
  **stable** per-event key (e.g. PaymentIntent id, `reward:{taskId}`).
- Body `kind: "Mint"` (with `name`/`assetId`/`metadata`) or `kind: "Transfer"`
  (with `assetRecordId`); `amount` is an opaque string.
- `200` → `walletId`, `walletAddress`, `walletProvisioned`, `operationId`,
  `replayed`. Same key again → original result, `replayed: true`, no second move.
- `403` if scope missing **or** target KYC not `APPROVED` (`KYC_FORBIDDEN:`
  prefix, fail-closed); `429` financial rate-limit.

Idempotency is partitioned by API key (`alloc:<apiKeyId>:<your-key>`); absent
header ⇒ deterministic content key (never random). (Full prose:
`conductor/tracks/fiat-stripe-bridge/docs/INTEGRATION-CONTRACT.md`.)

> Value can flow **two ways**: inside a self-run action via Tier-2 nodes (the
> user acts), or out-of-band via `POST /api/allocation` (the consumer acts on
> an already-decided amount). Both are exactly-once and KYC-gated.

---

## 7. Reconcile-before-retry — the double-mint clause (P7 — DONE)

**Hazard.** A Tier-2 node (or allocation) that broadcasts then times out waiting
for confirmation surfaces as an error indistinguishable from "never broadcast." A
blind retry **re-mints** — double-paying a bounty.

**Guarantee AZOA provides — shipped, not owed.**
1. Record the broadcast `TxHash` on the `ActionNodeExecution` before confirmation
   resolves (`Models/Actions/ActionNodeExecution.cs`).
2. On failure, probe chain truth (`ChainConfirmation`: Confirmed / FailedOnChain /
   Pending / Unknown) before any retry (`ChainActionRecovery.Decide`,
   `Services/Actions/Workflow/ChainActionRecovery.cs`).
3. `ChainActionRecovery` branches: **Confirmed** → advance reconciled (no
   re-broadcast); **FailedOnChain** → safe retry; **Pending** or **Unknown**
   (or no tx hash recorded) → park `AwaitingReconciliation` — resolved by
   `POST /api/action/runs/{runId}/reconcile` (user-scoped) or the
   operator-only `POST /api/action/runs/reconcile-sweep`
   (`Controllers/ActionController.cs`, `ActionManager.ReconcileRunAsync` /
   `SweepReconciliationAsync`); **never** auto-re-broadcast.

**Consumer obligations:** send a **stable `Idempotency-Key`** on every allocation;
treat `AwaitingReconciliation` as a non-terminal, non-error board state ("pending
settlement") and do not re-trigger — call the per-run reconcile endpoint (or
wait for the operator sweep) instead of retrying. Bounty automation on real
value is no longer blocked by this clause; `Blockchain:Mode=Simulated`
(deterministic `sim:` ids) remains the recommended mode for end-to-end dev
regardless.

---

## 8. Smarter gates — navigation rules between phase-holons (AZOA extension)

`GateCheck` today evaluates a closed-grammar boolean over upstream outputs
(`upstream.<node>.<path>`) and injected reads (`reads.<name>`) — solid for value
comparison, but it can't yet express two things the lifecycle DAG wants:

1. **Holon-state predicates.** A `holon.<id>.<field>` resolver so a gate reads a
   phase-holon's *current* lifecycle field directly
   (`holon.<projectId>.status == "FUNDED"`) instead of threading it through an
   upstream `HolonGet`. Fail-closed on missing holon/field.
2. **Transition-legality validation.** A semantic validator that knows the legal
   phase transitions (e.g. `IN_PROGRESS` may not follow `DRAFT` without `FUNDED`)
   and rejects an authored DAG whose gated edges encode an illegal transition.
   Structural (Kahn) validation stays; this is an added layer.

**Optional authorization gate** (require role / KYC-level / credential to
traverse an edge) — modeled via `reads.kyc`/`reads.role` today; promote to
first-class if it recurs. Both extensions are additive; existing predicates keep
working.

---

## 9. The consumer-side adapter (replaces self-custodial signing)

ArdaNova's `AlgorandService` (`ArdaNova.Infrastructure/Algorand/AlgorandService.cs`)
is today a self-contained custodial signer (platform mnemonic signs everything),
behind `IAlgorandService`: `MintSoulboundASAAsync`, `BurnASAAsync`,
`GetASAInfoAsync`, `VerifyOwnershipAsync`, `BuildARC19MetadataAsync`,
`CreateFungibleASAAsync`, `TransferASAAsync`, `GetASABalanceAsync`,
`ClawbackASAAsync`.

**Contract:** introduce `AzoaBackedAlgorandService` implementing the same surface
by calling an AZOA node:

| `IAlgorandService` method | AZOA call |
|---|---|
| `MintSoulboundASAAsync` | `Grant` action node (soulbound) **or** `POST /api/allocation` kind=`Mint` |
| `CreateFungibleASAAsync` | `FungibleTokenCreate` action node (or a direct mint endpoint — §10 Q2) |
| `TransferASAAsync` | `POST /api/allocation/{userId}` kind=`Transfer` |
| `GetASABalanceAsync` / `GetASAInfoAsync` | AZOA wallet/portfolio read — **chain is source of truth; AZOA stores no balance** |
| `VerifyOwnershipAsync` | AZOA NFT/holon ownership read |
| `BurnASAAsync` / `ClawbackASAAsync` | deferred — soulbound clawback-revoke is H2 (mint shipped; revoke follow-up) |

The platform mnemonic + direct Algod/Indexer HttpClient calls are **removed** from
ArdaNova once cut over; ArdaNova keeps only the ARC-19 metadata *shape* (domain),
passed to AZOA. **Custody moves to whoever operates the AZOA node** (§2): if
ArdaNova self-hosts, it remains custodian via `KeyCustodyService` rather than its
own mnemonic; if it points at a shared node, users are custodied there. Cutover
is **feature-flagged** so simulated/legacy paths coexist during migration.

---

## 10. Track decomposition (Phase-2 — each repo authors its own)

### AZOA (`azoa/conductor/tracks/`)

⚠ **Corrected 2026-09-07 — this was a Phase-2 PROPOSAL, not a record.** None of the
five slugs below was ever created; they exist in neither `conductor/tracks/` nor
`conductor/tracks/_archive/`. The work itself mostly landed under different track
names (e.g. the fungible-mint endpoint under `_archive/fungible-token-node`), so
reading this list as a status board — as a sibling repo's catalog did — produces
confident, wrong claims about what AZOA has open. Treat it as the original
decomposition intent and consult `conductor/tracks.md` for actual status.
- `azoa-node-integration-contract` — this doc + contract/seam tests; neutral "consumer/operator/user" framing; the publish-vs-run distinction made explicit in API docs.
- `action-reconcile-retry-wiring` — close **P7** (§7). The double-mint blocker. Highest priority.
- `scrum-lifecycle-action-presets` — the create→fund→work→tasks definition(s) + public templates a user can instantiate and self-run (§5).
- `smart-gates-holon-state` — holon-state predicates + transition-legality validator (§8).
- `fungible-mint-and-render-model` — the dedicated `POST /api/nft/fungible-mint` endpoint (KYC-gated, idempotent) + render-ready balance/portfolio DTO; SDK methods + path constants; a frontend page driving both against the live backend (§11.3, §11.5). Wallet-generate stays ungated; value seams stay gated (§11.4).
- (pre-prod, in DEPLOY-STEPS: **B3** KMS custody. Note: **P5 is now resolved** — wallet-generate is intentionally NOT KYC-gated; the gate lives at the value seams.)

### ArdaNova (`ardanova/conductor/tracks/`)
> **Locked decisions (§11):** ArdaNova integrates a **shared/managed AZOA
> node** (node operator custodies — ArdaNova owns no B3/P3 concern, §11.1)
> and its users are **tenant-custodial AZOA child users**, provisioned via
> `POST /api/tenant/users` under `tenant:provision` (§11.2, ratified decision
> #1) — never self-registered, self-sovereign AZOA identities. This makes
> §4.1's fleet-mapping table the ArdaNova path.
>
> ⚠ **Corrected 2026-09-07.** This previously read "self-register + self-run
> users … no fleet map, no `tenant:provision`, no acting-as path" — a model
> the code never built and the ratified decision rejects. §11.1's
> node-hosting split is unchanged.

- `azoa-user-onboarding` — link each ArdaNova user to a **tenant-custodial** AZOA child user, provisioned via `POST /api/tenant/users` under `tenant:provision`; wallet-bound check before Tier-2 (§4, §4.1, §5.1). ⚠ Corrected 2026-09-07 (ratified decision #1) — previously said "self-sovereign … No `tenant:provision`."
- `azoa-provider-adapter` — `AzoaBackedAlgorandService` via the shared node's standard API, feature-flagged, platform mnemonic removed (§9).
- `azoa-action-authoring` — publish the scrum-lifecycle action definitions/templates; **users self-run**; map board events → gate signals (§5).
- `treasury-reward-to-azoa-allocation` — funding + task-reward → `POST /api/allocation` with stable idempotency keys (§6, §7).

### Ordering constraint
AZOA `action-reconcile-retry-wiring` (P7) is **upstream** of ArdaNova
`treasury-reward-to-azoa-allocation` going live on real value. Because ArdaNova
uses a **shared node**, the node operator owns B3 (KMS custody) + P3 (fee-funding)
— these are **not** ArdaNova tracks. Dev/integration runs use
`Blockchain:Mode=Simulated` until P7 + B3 close on the chosen node.

---

## 11. Contract decisions

### Locked (drive the Phase-2 specs)
1. **Node hosting (§2): shared / managed node.** ArdaNova integrates against a
   shared AZOA node; **that operator custodies** the users. ArdaNova is a pure
   API consumer and owns **no** B3 (KMS) / P3 (fee-funding) deploy concern.
2. **User model (§4): tenant-custodial, not self-register/self-run.** Each
   ArdaNova user is a **tenant-custodial AZOA child user**, provisioned by
   ArdaNova via `POST /api/tenant/users` under its `tenant:provision` scope
   (§4.1) — never a self-registered, self-sovereign AZOA identity. A
   consumer person never holds an AZOA credential, seed phrase, or export
   path; claim-to-self-sovereign is a future, explicitly-designed opt-in, not
   the default. §4.1's fleet-mapping table **does** apply. ⚠ **Corrected
   2026-09-07 (ratified decision #1) — this reverses the prior lock**, which
   read "self-register + self-run … no fleet map, no `tenant:provision` … do
   not apply"; ArdaNova never built that path (its self-sovereign avatar
   service is being deleted as dead code, not extended). The act-as
   credential leg of §4.1 stays **disabled** pending an explicit
   per-endpoint scope policy — see §4.1.

### Locked (resolved)
3. **Dedicated fungible-mint endpoint — YES.** A one-shot
   `POST /api/nft/fungible-mint` (no DAG required) is added alongside the
   `FungibleTokenCreate` action node, sharing the same manager path + KYC gate +
   idempotency. It MUST be exposed in the **TypeScript SDK**
   (`sdk/azoa-wallet/src/api/client.ts` + a path constant in `api-version.ts`)
   and driven through the **frontend, which IS the live test harness for the
   backend** — the Next.js app exercises the API end-to-end via the SDK (there is
   no separate vitest/playwright suite for the app; a frontend page/flow drives
   fungible-mint against the running backend). The action node stays for in-DAG
   launches; the endpoint serves direct launches.
4. **Wallet-generate is allowed pre-KYC; *use* is KYC-gated.** A wallet may be
   created (`POST /api/wallet/generate`) by any user without KYC. **Any
   value-bearing action is gated**: mint (incl. fungible-mint), transfer,
   allocation, and all Tier-2 action nodes require the actor's KYC to be
   `APPROVED` (fail-closed, `KYC_FORBIDDEN:` → 403). "You can make a wallet, but
   to participate you must be KYC-approved." `WalletManager.GenerateWalletAsync`
   is therefore NOT gated; the gate stays at the value seams (already at
   `NftManager.MintAsync`; extend to fungible-mint + confirm allocation path).
5. **Balance read-model ships render-ready data.** The balance/portfolio read
   returns **everything the frontend needs to render in one call** — per-asset:
   id, symbol/unit-name, name, decimals, raw + display-formatted amount, chain,
   asset kind (NFT vs fungible), and any metadata/icon ref — so the frontend SDK
   renders without a second round-trip. Chain stays source of truth (AZOA stores
   no balance); the read aggregates chain truth into a render-model DTO. Surfaced
   through the SDK portfolio/balance methods and rendered by a frontend page (the
   live test harness) against the running backend.
