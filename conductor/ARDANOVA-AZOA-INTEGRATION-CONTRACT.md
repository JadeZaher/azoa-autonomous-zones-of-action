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
fungible tokens, swaps, transfers) and a **quest engine** for orchestrating
multi-step blockchain workflows as DAGs. It is not bespoke to any one consumer.

The contract is written in terms of three neutral roles. **ArdaNova is the first
reference consumer**, used only as a concrete example.

| Role | Who | What it does |
|---|---|---|
| **Node operator** | whoever hosts the AZOA node | Runs the service. **Custody follows the node operator** (§2). |
| **Consumer / tenant** | an application integrating AZOA (e.g. ArdaNova) | **Authors quest definitions** and reads/writes via the standard API. Does **not** custody users' assets by virtue of being a consumer (§3, §5). |
| **Avatar** | an end identity on the node | Holds its own identity; **runs quests as itself** under its own consent (§4, §5). |

> **The key correction this revision encodes:** a consumer that authors a quest
> object does **not** thereby take custody of the avatars that interact with it.
> The consumer *publishes* a workflow definition; **any avatar runs its own run
> against that definition, as itself**. Custody is a property of *who hosts the
> node*, not of *who authored the quest*. A consumer only acts *as* a user
> through an explicit, revocable **consent grant** (the acting-tenant path) —
> typically when that consumer is also the node operator.

### Boundary rules (non-negotiable)
1. **No brand leak into AZOA.** No AZOA source may contain the string `ArdaNova`.
   AZOA knows only "a consumer," "a node operator," "an avatar." (H5 grep → CI.)
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
| KYC document capture + the investment/escrow record | **Consumer** |
| Deciding *how much* of *which* asset to allocate / reward | **Consumer** |
| Webhook signature verification (fiat / payment provider) | **Consumer** |
| Authoring quest **definitions** (templates / DAGs) | **Consumer** |
| Hosting the node; **custody of avatar keys** (per node operator) | **Node operator** |
| Provisioning a wallet for an avatar (if absent) | **AZOA node** |
| Minting / transferring the on-chain (or simulated) asset | **AZOA node** |
| Deduping a redelivered trigger so an asset moves exactly once | **AZOA node** |
| KYC **gate** (fail-closed) on the value-bearing move | **AZOA node** |
| Reconcile-before-retry so a bounty/grant never double-mints | **AZOA node** |
| Quest engine that orchestrates the project→fund→work lifecycle | **AZOA node** |
| Starting / driving a quest **run** (as the acting identity) | **Avatar** (self) or **consumer under a consent grant** |

---

## 2. Custody follows the node operator

Custody is determined by **who hosts the AZOA node**, not by who authored a quest
or who triggered a value move:

- **Self-hosted consumer node** (e.g. ArdaNova runs its own AZOA instance): that
  operator custodies the keys of the avatars on it. The avatars are still
  first-class self-sovereign identities; the operator is the custodian of record.
- **Shared / public node**: the node operator running that instance is the
  custodian for its avatars. A consumer integrating against someone else's node
  holds **no** custody — it can only act through standard API calls and (for
  acting-as-a-user) an explicit consent grant.

Mechanism (shipped): the single audited custody chokepoint is
`KeyCustodyService` (`Managers/KeyCustodyService.cs`) — ownership-checked
resolve, JIT decrypt, `byte[]` zeroing. Acting *as* an avatar that you do not own
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

- `nft:mint` / `wallet:manage` — author quests, mint/transfer assets, allocate.
- `tenant:provision` — **only** needed if the consumer provisions and acts for a
  *fleet of avatars it owns* (the custodial / acting-as path of §5.2). A
  publish-only consumer that lets avatars self-run does **not** need it.

The consumer's AZOA API key is a **deploy-time secret** (`AZOA_TENANT_API_KEY`,
bound by the consumer's secret store to `Azoa:TenantApiKey` / `AZOA__TenantApiKey`).
**Never commit or document the key value.** AZOA holds **no** payment-provider
secret — webhook verification is the consumer's job.

---

## 4. Identity & how an avatar runs a quest (the corrected core)

**Quests have an author and a runner, and they are different roles.**

- **Authoring** (consumer): publish a reusable quest **definition** —
  `POST /api/quest` (a concrete DAG) or `POST /api/quest/templates` with
  `is_public: true` (a template any avatar may instantiate). The author does not
  own anyone's run.
- **Running as self** (avatar — the default): an avatar starts its **own** run.
  The shipped engine scopes the run to the *calling* identity:
  `POST /api/quest/{id}/start-workflow` sets `Run.avatar_id` = the authenticated
  caller (`QuestController.StartWorkflow`). `advance` and `signal` are likewise
  caller-scoped. **The avatar drives its own run, under its own consent and keys.**
- **Running on behalf** (consumer — the exception, consent-gated): a consumer
  starts/drives a run *for* an avatar only when an `act_as_tenant` consent claim
  is present; it is stamped onto the run (`StartWorkflowRunAsync(..., GetActingTenantId())`)
  and re-checked at every Tier-2 (value-bearing) node. Null for a plain self-run
  → no behavior change. The avatar can revoke the grant at any time.

So a consumer that "creates the quest objects" is **publishing definitions any
avatar can interact with** — it does not custody those avatars. Custody enters
only via §2 (node operator) and acting-as enters only via an explicit grant.

### 4.1 Optional fleet mapping (only for the custodial / acting-as path)

If (and only if) a consumer owns a fleet of avatars (self-hosted operator, or a
consent-delegated relationship), it maps its users to avatars via the tenant
surface. Each child avatar stores `OwnerTenantId` (self-FK, server-set from the
key claim, never a body) and `ExternalUserId` (the consumer's own user id, unique
per owner — the lookup key).

| Step | Call | Notes |
|---|---|---|
| provision | `POST /api/tenant/avatars` `{ "externalUserId": "...", "externalRef": "..." }` | Idempotent on `externalUserId`. Requires `tenant:provision`. |
| act-as | `POST /api/tenant/avatars/{avatarId}/credential` `{ "scopes": [...] }` | 15-min child JWT; scopes = intersection with the key's scopes, minus `tenant:provision`. |
| resolve | `GET /api/tenant/avatars/{externalUserId}` | userId → avatar; 404 if not this owner's. |

(Full prose: `conductor/tracks/tenant-onboarding/ONBOARDING.md`. A
publish-only/self-run consumer skips this section entirely.)

---

## 5. The project lifecycle as a Quest DAG

The consumer authors a quest **definition** for the project lifecycle; an avatar
(the project creator, a contributor, a supporter) runs its **own** run against it.
Each **phase is a node**; **navigation between phases is a gated edge**. The AZOA
durable engine advances the run, parking at gates for signals.

### 5.1 Domain → node-type mapping (ArdaNova example)

| Concept | State machine | Quest DAG representation | Who runs it |
|---|---|---|---|
| **Create project** | `Project: DRAFT→PUBLISHED` | `HolonCreate` (Project holon) → `Emit`(`project.created`) | the creator avatar |
| **Seek support / fund** | `SEEKING_SUPPORT→FUNDED` | `GateCheck`(funding goal met — bool injected via `reads`) → `FungibleTokenCreate` (ProjectShare ASA) and/or `Grant` | supporter avatar(s); consumer signals the gate |
| **Start work** | `FUNDED→IN_PROGRESS` | `GateCheck`(`status == "FUNDED"`) → `Emit`(`sprint.started`) | the creator/lead avatar |
| **Task / PBI / Bounty** | `TODO→…→COMPLETED`; escrow `NONE→FUNDED→RELEASED/REFUNDED` | `GateCheck`(submission accepted) → `Transfer`/`Grant`(reward) → `Emit`(`task.completed`); reject branch → `Refund`/`Emit` | the contributor avatar |
| **Membership credential** | soulbound ASA | `Grant` (soulbound: total=1, decimals=0, frozen) → credential Holon | the member avatar |

**Tier note.** `GateCheck`/`Emit`/`HolonCreate` are Tier-0/1 (no chain). `Grant`,
`Transfer`, `Refund`, `Swap`, `FungibleTokenCreate` are **Tier-2** — they require
the run's **actor avatar** (the self-runner, or the consented act-as target) to
have a wallet bound (`ChainCapabilityGate`, fail-closed). Because the run is
self-scoped, the wallet and keys are the avatar's own — the consumer never needs
custody to let the avatar transact.

### 5.2 Node config shapes (authoritative; `Models/Quest/NodeConfigs.cs`)

- `GateCheckNodeConfig` → `{ "predicate": "<bool expr>", "reads": { "<name>": <json> } }`
- `EmitNodeConfig` → `{ "payload": <opaque consumer json> }`
- `GrantNodeConfig` → `{ "request": <NftMintRequest>, "holonId": "<guid?>" }` (actor avatar from run context, NOT the body)
- `TransferNodeConfig` / `RefundNodeConfig` → `{ "nftId": "<guid>", "request": <NftTransferRequest> }`
- `FungibleTokenCreateNodeConfig` → `{ "chainType":"Algorand", "name":"", "unitName":"", "total":<ulong>, "decimals":<int>, "holonId":"<guid?>" }` (total/decimals consumer-authoritative; AZOA derives no economic meaning)

### 5.3 Run orchestration surface

| Call | Purpose | Scoped to |
|---|---|---|
| `POST /api/quest` / `POST /api/quest/templates` (`is_public`) | Author a definition / publish a template. | the **author** |
| `POST /api/quest/templates/{id}/instantiate` | An avatar materializes a public template into its own quest. | the **instantiating avatar** |
| `POST /api/quest/{id}/validate` | Structural DAG validation (Kahn + entry/terminal/orphan/reachability). | — |
| `POST /api/quest/{id}/start-workflow` | Start a durable run. `Run.avatar_id` = caller. | the **running avatar** |
| `POST /api/quest/runs/{runId}/advance` | Resume a `Suspended` run. | the **running avatar** |
| `POST /api/quest/runs/{runId}/signal` | Un-park an `AwaitingSignal` gate (consumer pushes "goal met" / "task approved"). | the **running avatar** (or consumer via consent) |
| `GET /api/quest/runs/{runId}/execution-state` | Poll node states for the board. | reader |

**Parking states:** `AwaitingSignal`, `AwaitingTimer`, `AwaitingReconciliation`
(§6), `Suspended`; terminal `Succeeded`/`Failed`/`Cancelled`/`Forked`.

---

## 6. Value movement — allocation (the consumer-driven value door)

When the consumer settles fiat / decides a reward outside a quest run, it can
move value directly (provision-if-absent + idempotent mint/transfer):

### `POST /api/allocation/{avatarId}`
- `{avatarId}` = the AZOA avatar that receives the asset (route only; IDOR-safe).
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

> Value can flow **two ways**: inside a self-run quest via Tier-2 nodes (the
> avatar acts), or out-of-band via `POST /api/allocation` (the consumer acts on
> an already-decided amount). Both are exactly-once and KYC-gated.

---

## 7. Reconcile-before-retry — the double-mint clause (AZOA owes the wiring, P7)

**Hazard.** A Tier-2 node (or allocation) that broadcasts then times out waiting
for confirmation surfaces as an error indistinguishable from "never broadcast." A
blind retry **re-mints** — double-paying a bounty.

**Guarantee AZOA provides** (owner `blockchain-recovery-and-portable-wallets`):
1. Record the broadcast `TxHash` on the `QuestNodeExecution` before confirmation
   resolves.
2. On failure, probe chain truth (`ChainConfirmation`: Confirmed / NotFound /
   Indeterminate) before any retry.
3. `ChainActionRecovery` branches: **Confirmed** → advance reconciled (no
   re-broadcast); **NotFound** → safe retry; **Indeterminate** → park
   `AwaitingReconciliation` (sweep / manual re-probe; **never** auto-re-broadcast).

**Consumer obligations:** send a **stable `Idempotency-Key`** on every allocation;
treat `AwaitingReconciliation` as a non-terminal, non-error board state ("pending
settlement") and do not re-trigger. **Until P7 lands**, bounty automation must
not run on real value — use `Blockchain:Mode=Simulated` (deterministic `sim:`
ids) for end-to-end dev.

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
| `MintSoulboundASAAsync` | `Grant` quest node (soulbound) **or** `POST /api/allocation` kind=`Mint` |
| `CreateFungibleASAAsync` | `FungibleTokenCreate` quest node (or a direct mint endpoint — §10 Q2) |
| `TransferASAAsync` | `POST /api/allocation/{avatarId}` kind=`Transfer` |
| `GetASABalanceAsync` / `GetASAInfoAsync` | AZOA wallet/portfolio read — **chain is source of truth; AZOA stores no balance** |
| `VerifyOwnershipAsync` | AZOA NFT/holon ownership read |
| `BurnASAAsync` / `ClawbackASAAsync` | deferred — soulbound clawback-revoke is H2 (mint shipped; revoke follow-up) |

The platform mnemonic + direct Algod/Indexer HttpClient calls are **removed** from
ArdaNova once cut over; ArdaNova keeps only the ARC-19 metadata *shape* (domain),
passed to AZOA. **Custody moves to whoever operates the AZOA node** (§2): if
ArdaNova self-hosts, it remains custodian via `KeyCustodyService` rather than its
own mnemonic; if it points at a shared node, avatars are custodied there. Cutover
is **feature-flagged** so simulated/legacy paths coexist during migration.

---

## 10. Track decomposition (Phase-2 — each repo authors its own)

### AZOA (`azoa/conductor/tracks/`)
- `azoa-node-integration-contract` — this doc + contract/seam tests; neutral "consumer/operator/avatar" framing; the publish-vs-run distinction made explicit in API docs.
- `quest-reconcile-retry-wiring` — close **P7** (§7). The double-mint blocker. Highest priority.
- `scrum-lifecycle-quest-presets` — the create→fund→work→tasks definition(s) + public templates an avatar can instantiate and self-run (§5).
- `smart-gates-holon-state` — holon-state predicates + transition-legality validator (§8).
- `fungible-mint-and-render-model` — the dedicated `POST /api/nft/fungible-mint` endpoint (KYC-gated, idempotent) + render-ready balance/portfolio DTO; SDK methods + path constants; a frontend page driving both against the live backend (§11.3, §11.5). Wallet-generate stays ungated; value seams stay gated (§11.4).
- (pre-prod, in DEPLOY-STEPS: **B3** KMS custody. Note: **P5 is now resolved** — wallet-generate is intentionally NOT KYC-gated; the gate lives at the value seams.)

### ArdaNova (`ardanova/conductor/tracks/`)
> **Locked decisions (§11):** ArdaNova integrates a **shared/managed AZOA node**
> (node operator custodies — ArdaNova owns no B3/P3 concern) and uses
> **self-register + self-run avatars** (each user is a self-sovereign avatar; **no
> fleet map, no `tenant:provision`, no acting-as path** by default). This drops the
> avatar-mapping and node-hosting tracks entirely.

- `azoa-avatar-onboarding` — link each ArdaNova user to a **self-sovereign** AZOA avatar (the user holds keys / consents); thin reference + wallet-bound check before Tier-2 (§4, §5.1). No `tenant:provision`.
- `azoa-provider-adapter` — `AzoaBackedAlgorandService` via the shared node's standard API, feature-flagged, platform mnemonic removed (§9).
- `azoa-quest-authoring` — publish the scrum-lifecycle quest definitions/templates; **avatars self-run**; map board events → gate signals (§5).
- `treasury-reward-to-azoa-allocation` — funding + task-reward → `POST /api/allocation` with stable idempotency keys (§6, §7).

### Ordering constraint
AZOA `quest-reconcile-retry-wiring` (P7) is **upstream** of ArdaNova
`treasury-reward-to-azoa-allocation` going live on real value. Because ArdaNova
uses a **shared node**, the node operator owns B3 (KMS custody) + P3 (fee-funding)
— these are **not** ArdaNova tracks. Dev/integration runs use
`Blockchain:Mode=Simulated` until P7 + B3 close on the chosen node.

---

## 11. Contract decisions

### Locked (drive the Phase-2 specs)
1. **Node hosting (§2): shared / managed node.** ArdaNova integrates against a
   shared AZOA node; **that operator custodies** the avatars. ArdaNova is a pure
   API consumer and owns **no** B3 (KMS) / P3 (fee-funding) deploy concern.
2. **Avatar model (§4): self-register + self-run.** Each ArdaNova user is a
   **self-sovereign** AZOA avatar that runs quests as itself. **No fleet map, no
   `tenant:provision`, no acting-as/consent-delegation path** by default. §4.1 and
   the `azoa-avatar-mapping` track do **not** apply.

### Locked (resolved)
3. **Dedicated fungible-mint endpoint — YES.** A one-shot
   `POST /api/nft/fungible-mint` (no DAG required) is added alongside the
   `FungibleTokenCreate` quest node, sharing the same manager path + KYC gate +
   idempotency. It MUST be exposed in the **TypeScript SDK**
   (`sdk/azoa-wallet/src/api/client.ts` + a path constant in `api-version.ts`)
   and driven through the **frontend, which IS the live test harness for the
   backend** — the Next.js app exercises the API end-to-end via the SDK (there is
   no separate vitest/playwright suite for the app; a frontend page/flow drives
   fungible-mint against the running backend). The quest node stays for in-DAG
   launches; the endpoint serves direct launches.
4. **Wallet-generate is allowed pre-KYC; *use* is KYC-gated.** A wallet may be
   created (`POST /api/wallet/generate`) by any avatar without KYC. **Any
   value-bearing action is gated**: mint (incl. fungible-mint), transfer,
   allocation, and all Tier-2 quest nodes require the actor's KYC to be
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
