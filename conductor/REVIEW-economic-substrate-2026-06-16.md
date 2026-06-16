# Review — orchestration gaps + Holon/Quest as ArdaNova's economic substrate

> Read-only architectural review (2026-06-16) following the ardanova-provider-port
> ship. Three lanes: (A) orchestration-gap audit of the 7 shipped commits, (B) Holon
> as economic-object substrate, (C) Quest as economic-workflow substrate. The locked
> constraint holds throughout: **the economic/token domain stays in ArdaNova; OASIS
> exposes mechanism-only primitives.** This doc is the recommendation; nothing here is
> implemented yet.

---

## Part A — Orchestration gaps in the shipped code

The shipped tracks are structurally sound (idempotency claim-ordering, broadcast/confirm
retry split, custody zeroing, tenant isolation all verified correct). But the **value
path is not yet wired end-to-end** — two Critical gaps gate any real flow, and they share
a root cause: the allocation path doesn't route through the real broadcast + reconciliation
machinery, and the custody resolver isn't called.

### 🔴 C1 — Custody service is unwired; every signed op uses the platform key
`AlgorandProvider.BuildSignSubmitCoreAsync` signs via `ResolveInterimKeyMaterial(signerAddress)`
(`AlgorandProvider.cs:654, 785-800`) which **ignores `signerAddress`** and always loads
`OASIS:Algorand:PlatformMnemonic`. `KeyCustodyService.WithSigningKeyAsync` (the real
IDOR-guarded per-user resolver, `KeyCustodyService.cs:73-115`) is DI-registered but its
**only callers are its own tests**. Consequence: a per-user `TransferAsync(from=userWallet)`
is signed with the platform key → sender mismatch → chain rejects, or moves the platform's
asset. The custody track's entire security property is bypassed at runtime.
**Fix:** inject `IKeyCustodyService` into the provider; route user-wallet ops through
`WithSigningKeyAsync(walletId, avatarId, …)` and platform/ASA-admin ops through
`WithPlatformSigningKeyAsync(true, …)`. Requires threading `avatarId` into the provider
call surface (`IBlockchainProvider.*Async` have no avatar param today). Until then, **block
per-user custodial transfers** rather than silently mis-sign them.

### 🔴 C2 — Allocation marks idempotency Completed though nothing goes on-chain
`AllocationManager.MintAsync` → `NftManager.MintAsync` (`NftManager.cs:53-93`) only upserts
a Holon + a `Pending` `BlockchainOperation` — it **never broadcasts** (no
`BlockchainOperationManager.ExecuteAsync`, no provider call, no `TxHash`). `AllocationManager`
then writes `CompleteAsync` (`AllocationManager.cs:124`) as if it settled. For a fiat bridge
("money already cleared"), this is a **silent zero-mint that reports success**, and a
redelivered webhook dedupes against a record of an effect that never happened.
**Fix:** drive allocation mints/transfers through the real broadcast path; only `Complete`
the idempotency key with the actual `TxHash`; stay InProgress until confirmed.

### 🟠 H1 — Allocation idempotency key is unrecoverable by reconciliation
The `alloc:{apiKeyId}:…` key is never persisted onto the op row, so a crash between
`TryClaimAsync` and `Complete/Fail` leaves a **permanently poisoned claim**
(`ReplayFromRecord` returns "in progress, retry later" forever). The bridge path avoids this
by persisting `Parameters["IdempotencyKey"]` (`ReconciliationService.cs:523, 552`).
**Fix:** thread the key into `op.Parameters["IdempotencyKey"]` so reconciliation can release
the orphaned claim from chain truth.

### 🟠 H2 — Allocation ops are invisible to reconciliation (no TxHash)
Resolved transitively by C2: once allocations broadcast and record a `TxHash`, the existing
reconciliation lane handles them. Documents that *current* shipped wiring makes allocation
ops un-reconcilable (`ReconciliationService.cs:380-389` dead-ends on blank TxHash).

### 🟠 H3 — NFT mint bypasses the KYC gate (the P5 hole, confirmed)
`RequireVerifiedAsync`'s only call site is `AllocationManager.cs:82`. `NftController.Mint` →
`NftManager.MintAsync` has no KYC check — a tenant can mint via `POST /api/nft/mint` and
sidestep the gate the allocation seam enforces.
**Fix (preferred):** move the gate into `NftManager.MintAsync` so both doors inherit it
(single choke point, matches the custody/decrypt single-choke-point philosophy).

### 🟠 H4 — `int amount` truncates large allocations at the provider boundary
`AllocationRequest.Amount` is `string` (arbitrary precision) but `AlgorandProvider.MintAsync/
TransferAsync` take `int amount` (`AlgorandProvider.cs:168, 203`; `(ulong)` cast at 224).
Latent today (amount only travels as metadata because of C2); becomes live silent-truncation
once C2 wires the amount to the chain call.
**Fix:** widen the provider value-amount surface to `ulong`/string + range-validate, as part
of the C2 wiring.

### 🟡 M1 — Confirm-timeout discards the txId → false-Failed + double-submit risk
`WaitForConfirmationAsync` (`AlgorandProvider.cs:713-750`) returns an error after 10 rounds;
`BlockchainOperationManager` records `Failed` **without** the `TxHash`, so a slow-but-valid
tx becomes permanently false-Failed and a retry could double-submit.
**Fix:** on timeout return "submitted, pending confirmation" carrying the txId; record
`Pending` + `Parameters["TxHash"]` so reconciliation settles it.

### 🟡 M2 — Allocation = two side effects (provision wallet + mint) with no compensation
A mint failure after wallet provisioning strands the wallet and leaves a terminal `Failed`
idempotency record that blocks legit retry. The `Sagas/*` layer (already DI-registered,
`Program.cs:539-556`) is the architecturally consistent home.
**Fix:** route multi-effect allocations through `ISagaCoordinator`, or split provisioning
into its own idempotency scope so a mint failure stays retryable.

### 🟡 M3 — Child Bearer credential can't reach the allocation endpoint
The child JWT lacks the `ApiKeyId` claim that `AllocationController` requires
(`AllocationController.cs:62-68`) → 401. Fail-closed (safe), but likely an unintended
functional gap in the tenant→child delegation story. **`AuthMethod` is read by no gate**, so
the missing-`AuthMethod` axis is benign.
**Fix:** decide the model. If children are allocation targets, derive the idempotency
partition from tenant identity (`OwnerTenantId`) not `ApiKeyId`; don't relax the `ApiKeyId`
check without replacing the partition source (else two tenants' identical client keys
collide).

### What's solid (verified, not just assumed)
Idempotency claim-before-side-effect ordering; `KeyCustodyService` internals (IDOR-before-
decrypt, zero-on-throw, honest P1 caveat); broadcast-vs-confirm retry separation; the `sim:`
marker discipline (rejection at validate/status boundaries); tenant 404-not-403 isolation +
route-sourced ownership; child-credential scope intersection (strips `tenant:provision`);
reconciliation's conservative "only act on positive chain signal" stance.

### Recommended remediation order
C2 first (it unblocks H1/H2/H4 and makes allocations real), then C1 (custody wiring +
avatar threading — the larger architectural change), then H3 (one-line choke-point gate),
then M1/M2 (reconciliation + saga robustness). C1+C2 are the only two that *gate real value
flow* and should land before any fiat points at the allocation endpoint — consistent with
keeping B6 (mainnet) gated.

---

## Part B — Holon as the economic-object substrate

**Verdict: closer to sufficient than expected.** The intended pattern works with near-zero
schema change.

**Each ArdaNova economic object = a `Holon` with a custom `asset_type`** ("project-token",
"allocation", "treasury-position", "membership-credential", "vesting-schedule"). `asset_type`
is free-form `option<string>`, already indexed (`Holon.cs:23`). NFT-as-Holon
(`NftManager` filters `asset_type=="NFT"`) is the sanctioned precedent — **zero schema change**.

Already sufficient:
- **Discriminator + metadata bag** — semantics live entirely in `metadata` + ArdaNova's
  logic; `HolonManager` does pure structural CRUD/graph/compose (no pricing/balance/accounting
  anywhere).
- **Graph edges** — `parent_holon_id` (containment: project→allocations, treasury→positions)
  + `peer_holon_ids` (lateral: token↔vesting-schedule), edited via `InteractAsync`
  (`POST /{id}/interact`), traversed via `GetChildren/Peers/Ancestors/Descendants` + the
  single-call `holon_traverse` MCP tool.
- **Membership credentials with access levels** — first-class already via `AvatarNFT` +
  `HolonNFTBinding` (role + permission-level + permissions map) + `VerifyHolonAccessAsync`.
- **Subtree rollup** — `HOLON_COMPOSE` (`ComposeAsync` → counts/asset-types/metadata-key
  frequency, no extra storage).
- **Bundling** — `DappSeries`/`DappManifest` reference `BoundHolonIds`.

Missing primitives (the build is small):
1. **Typed Holon ↔ on-chain-asset link (the material gap).** Today the only connection is
   `BlockchainOperation.Parameters["holonId"]` (stringly-typed); `operation_log` has **no**
   asset-id/tx-hash column, and `CreateSoulboundAsaAsync` returns the asset id but **persists
   it nowhere**. **Fix:** populate the existing `Holon.token_id`/`chain_id` from the mint
   result (they're already indexed via `(provider_name, chain_id)`), and add typed
   `asset_id`/`tx_hash`/`holon_id` columns to `OperationLog` (copy the existing
   `source_holon_id` `record<holon>` link pattern). Schema regen required (G6 SCHEMAFULL).
2. **Queryable metadata (optional).** `HolonQueryRequest` + `SurrealHolonStore.QueryAsync`
   filter only typed columns (`avatar_id`, `asset_type`, `chain_id`, `parent_holon_id`,
   `is_active`); no `WHERE metadata.x = y`. If ArdaNova needs server-side "all allocations
   where metadata.status=vested", add metadata-key filters + a SurrealQL clause (additive,
   low-risk). Otherwise fetch by `asset_type` and filter client-side.
3. **Typed metadata (optional).** `IHolon.Metadata` is `Dictionary<string,string>` though the
   DB column is `option<object>`. ArdaNova can store JSON-encoded strings, or widen to
   `JsonElement` (cross-cutting: touches `IHolon`, DTOs, `HolonManager`, store mappers, and
   `NftManager` since `Holon` implements both `IHolon` and `INft`).

Hazards: `PropagateAsync`/`CloneAsync` write reserved metadata keys (`propagated_*`,
`cloned_from`) — **namespace ArdaNova keys** to avoid collisions.

---

## Part C — Quest as the economic-workflow substrate

**Verdict: the DAG/composition/handler-SPI layer is an excellent fit; the execution model
and handler library are not — yet.** Two new handler types + one real engine change.

How it works today: a Quest is a generic DAG of typed nodes; each node has an opaque JSON
`Config`. Definition/runtime split (`Quest`/`QuestNode` vs `QuestRun`/`QuestNodeExecution`).
One handler per `QuestNodeType` (`IQuestNodeHandler` + registry, exactly-one invariant). Every
handler is the same shape: deserialize `Config` → call one OASIS manager → serialize result.
`ComposeOutputs` passes one node's output downstream. Fork lineage via `ParentRunId` +
copy-completed-history. **Templates + `{{param}}` substitution** is exactly the "tenant supplies
economic parameters into a node" shape.

Already there to build on (strong): tenant-composable DAG + templates/slots; clean handler
SPI (new node kind = enum member + handler + DI line); per-(run,node) execution rows with
exactly-once claim (G2) + serialized outputs + `ComposeOutputs` data-flow;
`WalletGetPortfolioNodeHandler` already reads balances (the read a balance-gate composes with);
fork/lineage audit trail.

Three gaps:
1. **No predicate/gate evaluation.** `QuestEdge.Condition` is a `string?` that is stored and
   round-tripped but **never parsed/evaluated** — used only as a presence flag for failed-
   predecessor skipping (`QuestManager.cs:266`). `ConditionNodeHandler` is a **no-op pass-
   through** (the schema comment claiming it evaluates expressions is inaccurate). **Build a
   real `GateCheck` handler** that evaluates a tenant-supplied predicate (balance≥X,
   KYC==approved, external signal) and returns Fail to gate downstream — the existing
   failed-predecessor-skip on Control edges then does the gating for free. This is the
   *minimal* new piece.
2. **No allocation/award/vest node types.** None exist. **Build generic mechanism-only
   handlers** (`Allocate`, `Vest`, …) that record/forward tenant-supplied amounts as opaque
   outputs or call back to ArdaNova — they must NOT compute economics (keeps the domain in
   the tenant). The deserialize→act→serialize shape supports a "callback/emit" handler.
3. **No suspendable/durable runs (the hard one).** `ExecuteAsync` runs the **entire DAG
   synchronously in one HTTP call** (`QuestManager.cs:198, 251-376`) — it cannot pause at
   "on gate-1-met → unlock next" or "vest over steps" awaiting an external signal/time. To
   express vesting/wait-for-settlement you need either a node that *suspends* the run + a
   per-node "advance run from node N" resume API (the existing `MarkRunCompleted/Failed`
   supervisor hooks anticipate externally-driven runs but not per-node resume), or an external
   scheduler driving one node at a time. **The pending `durable-saga-orchestration` track is
   the natural home** — and note this is the SAME suspendable/durable engine that Part-A M2
   (multi-step allocation compensation) wants.

Also: `CheckDependenciesAsync` exists but `ExecuteAsync` ignores it — cross-quest gating
isn't enforced if you rely on it.

---

## Synthesis — the shape of the economic system

OASIS provides three composable mechanism layers; ArdaNova supplies all economics:
- **Holon** = the economic *objects* (typed by `asset_type`, semantics in `metadata`, linked
  in a graph, linked to real ASAs once the Holon↔asset primitive lands).
- **Quest** = the economic *workflows* (tenant-composed DAGs of generic `GateCheck` +
  `Allocate`/emit nodes, parameterized via templates, gated on predicates — once the gate
  handler + suspendable engine land).
- **Allocation/signing/custody** = the value *movement* (once C1+C2 wire it to the real
  broadcast + reconciliation + custody machinery).

The single highest-leverage build that unlocks the most: a **durable/suspendable execution
engine** — it is simultaneously the fix for Part-A M2 (allocation compensation), the enabler
for Quest vesting/gating (Part C #3), and the home for multi-step economic flows. The
`durable-saga-orchestration` pending track should absorb it. Second: the **Holon↔ASA typed
link** (Part B #1) — small, unblocks "economic object = real on-chain asset." Third: close
the **value-path wiring** (C1/C2/H3) so allocations actually settle and are KYC-gated at the
single choke point.

None of this puts economic logic in OASIS: gates evaluate tenant-supplied predicates,
allocate-nodes forward tenant-supplied amounts, Holon metadata holds tenant semantics.
