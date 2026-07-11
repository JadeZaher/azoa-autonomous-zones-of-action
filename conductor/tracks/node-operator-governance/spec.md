---
type: spec
track: node-operator-governance
created: 2026-07-10
status: in_progress
horizon: alpha-partial
activation_gate: >-
  Phase 1 (the alpha slice) may land during alpha hardening: it is ONLY the scope
  itself + a single-node fee-schedule config surface + a governance-parameter config
  surface for the sovereign operator. It ships value standalone (a node that charges
  for economic actions and declares its own economy knobs). Phase 2 (federation rules,
  ecosystem-incentive parameters as enforced rules, peer allow/deny, cross-node
  governance) is HARD-GATED on federation-v2 reaching its own activation gate
  (>=3 operators + >=1 real cross-node use case) — a peer allow/deny list with no
  peers to allow or deny is spec theater. Do NOT build Phase 2 surfaces before the
  Commons substrate exists to enforce them.
depends_on:
  - avatar-dapp-rbac               # the scope-manager + policy model this scope plugs into
  - final-hardening-cutover        # value engine, allocation seam, HolonType registry, MCP surface
related:
  - federation-v2                  # federation RULES are governed here; the substrate lives there
  - fiat-stripe-bridge             # the TOKENS_PER_CENT price schedule is the fiat-mint precedent for fee-setting
  - data-engine-decision           # chain is settlement source-of-truth; fees never become off-chain balance authority
  - tenant-consent-delegation      # per-scope grant discipline the govern scope inherits
  - consent-gate-architecture      # single custody chokepoint; fee accrual never bypasses it
---

# Track: node-operator-governance

## Goal

Give a node operator a **third, economic-governance capability tier**, distinct from
platform infra admin (`operator:admin`) and DApp authoring (`dapp:manage`): the power to
run their OWN ecosystem economy with their OWN rules — set the fees the node charges,
define the federation rules the node applies, declare ecosystem-incentive parameters, and
set the governance knobs of their economy — **so long as it interops with the core
protocol and peers opt in to collaborate.**

Owner's vision (verbatim):

> "There should be a node operator scope that will apply federation rules, set fees etc.
> Node operators can create their own ecosystem incentives and governance rules so long as
> it interops with the core technology and others want to collab — it's doable."

This is **local economic sovereignty, interop-gated at the boundary.** An operator can set
ARBITRARY local rules for their own economy; the moment a rule touches another node, it is
constrained by (a) conformance to the core protocol and (b) mutual opt-in. That boundary is
the crux of this spec (§The interop constraint).

## The scope: `node:govern`

**Chosen name: `node:govern`** (constant `AzoaScopes.NodeGovern`).

Rationale for the name:

- **`node:` prefix, not `operator:`** — deliberately NOT colonised under `operator:` so it is
  never confused with `operator:admin`. `operator:admin` is destructive cross-avatar INFRA
  (key rotation, backfill). `node:govern` is ECONOMIC governance of the node's own ecosystem.
  Two different axes; two different prefixes keep the vocabulary honest.
- **`:govern` verb** — it governs *parameters and rules*, not the platform. It is not
  `node:admin` (that would blur back into infra) and not `node:operator` (a role-noun, not a
  capability — the vocabulary in `AzoaScopes.cs` is capability-shaped: `dapp:develop`,
  `dapp:manage`, `wallet:manage`, not role nouns).

### Where it sits vs the two existing tiers

| Scope | Axis | What it gates | Auth model |
|---|---|---|---|
| `operator:admin` | **platform infra admin** | destructive cross-avatar surfaces (key rotation, data backfill) | JWT-only; on the `ApiKeyForbiddenScopes` denylist; never key-issuable |
| `dapp:manage` / `dapp:develop` | **DApp economy authoring** | author/edit/compose/deploy holons, quests, dapp-series | self-issuable capability scopes, bounded by owning avatar's role |
| **`node:govern`** (this track) | **node ECONOMIC governance** | fee schedules, federation rules, ecosystem-incentive params, economy knobs | **JWT-only** (see below); NOT on the value path, so it is not on the S6 operation-scope map |

`node:govern` sits **between** the two: broader than `dapp:manage` (it governs the whole
node economy, not one DApp author's holons) but narrower and non-destructive relative to
`operator:admin` (it never touches another avatar's keys, never runs a backfill, never
decrypts a user's key — it only sets *parameters* the value engine later reads).

### JWT-only, and who grants it

**`node:govern` is JWT-only and belongs on the `ApiKeyForbiddenScopes` denylist** — the same
discipline as `operator:admin`. Setting the fees your node charges, or the federation rules
your node applies, is a node-authority act; it must originate from a JWT-authenticated node
operator identity, never from a self-issued API key that stuffed the literal string into its
CSV. It is therefore ALSO excluded from `SelfIssuableScopes` and `IssuableCapabilityScopes`.

**Who grants it — the two cases the owner asked about:**

- **Single-operator sovereign node (today's reality).** The node's operator identity IS the
  node owner. `node:govern` is **implicitly held by the node's operator identity** — the same
  identity that carries `operator:admin`. Concretely: the JWT issuer stamps `node:govern` onto
  the node-operator's admin identity at issuance (alongside / by the same mechanism that will
  eventually stamp `operator:admin` per the final-hardening deferred "operator:admin JWT-issuer
  scope wiring" follow-up). On a sovereign single node there is exactly one governor: the owner.
- **Future multi-node federated node (post federation-v2).** Each node still has its OWN
  governor(s). `node:govern` is granted **per-node by that node's owner** — it is node-local
  authority, never platform-global. Node A's governor cannot set Node B's fees. A federation
  peer relationship does NOT confer cross-node governance (that would violate sovereignty — see
  federation-v2 §non-goals). Cross-node effects require mutual opt-in, not a shared governor.

**Distinction from `operator:admin` even though the same identity holds both today:** keep them
as **separate scopes** even though the sovereign operator carries both. They separate cleanly the
day a node wants to delegate economic governance (set fees, tune incentives) to a business
operator WITHOUT handing them the destructive infra keys (rotation, backfill). Modeling them as
one scope would foreclose that; two scopes cost nothing and preserve the option.

## Capabilities it gates (the surface — spec, not all endpoints exist yet)

All four surfaces are **parameter/config surfaces**, not engines. `node:govern` writes a
node-scoped configuration record; the existing value engine, quest engine, and (later)
federation client *read* those parameters. This keeps the track small and honors the
"governance-parameter surface, not a full rules engine — MVP scope" constraint.

### 1. Fee schedules  *(alpha-cheap; Phase 1)*

The operator sets/adjusts the fees the node charges for economic actions: **mint, transfer,
swap, quest completion, federation publication.** This is a `NodeFeeSchedule` config record
(node-scoped, versioned), NOT a per-request input.

- **Shape** mirrors the fiat-stripe-bridge precedent. The `TOKENS_PER_CENT` price schedule
  (`sdk/azoa-wallet/src/orchestration/stripe.ts` — `PriceSchedule = { kind: "tokensPerCent" }`)
  is the exact pattern: an **operator-configured, server-side-authoritative rate, never
  client-supplied.** `NodeFeeSchedule` is the on-node analogue — the operator's authoritative
  rate table for economic actions, read at action time, never overridable by the caller.
- **Fee model (opinionated MVP):** a flat + basis-points table keyed by operation type, e.g.
  `{ "Mint": { flatBaseUnits, bps }, "Transfer": {...}, "Swap": {...}, "QuestComplete": {...},
  "FederationPublish": {...} }`. `bps` (basis points of the moved value) covers value-scaled
  actions; `flatBaseUnits` covers fixed actions (mint of a supply-1 NFT, a quest completion).
- **Where fees accrue — CRITICAL, honors [[data-engine-decision]].** The chain is the neutral
  settlement source of truth; **AZOA never holds a stored off-chain balance.** So a fee is NOT a
  ledger row AZOA debits. A fee is one of:
  - **(a) an on-chain transfer to the node's treasury wallet** — the value engine, at broadcast
    time, moves the computed fee to a configured `NodeTreasuryWallet` as a second chain op
    (settlement stays on-chain, provenance intact). This is the sound model: fees settle where
    value settles.
  - **(b) a fee-adjusted allocation** for the fiat-mint path — the orchestrator's
    `tokensPerCent` already decides the mint quantity; a node fee reduces the minted quantity
    (fee retained as un-minted supply) or mints a treasury share. Ties DIRECTLY to
    `AllocationManager.AllocateAsync` — the fee is applied to the parsed `amount` before the
    typed mint/transfer op is built, and MUST run through the same idempotency claim so a
    redelivered request never double-charges.
  - Fee computation is **read-only parameter lookup**; it NEVER bypasses the custody chokepoint
    ([[consent-gate-architecture]]) and NEVER becomes an off-chain balance. A fee that cannot
    settle on-chain fails the action closed rather than accruing to a phantom balance.
- **Guardrail:** fee changes are versioned + audited (an immutable `node_fee_audit` trail,
  mirroring `consent_audit`); a fee schedule change is never retroactive to an in-flight
  idempotency claim.

### 2. Federation rules  *(deferred; Phase 2 — gated on federation-v2)*

This is **where "set the federation rules" lives.** federation-v2 defines the thin operator
surface (submit holon, submit conformance, host cell); `node:govern` is the authority that
decides **the RULES those functions apply**:

- **Which holons/quests/templates this node will federate** — a federation allow/deny policy
  (per-type, per-tag, or explicit list) that the L1 "submit a holon for federation" function
  consults before publishing a `HolonReference` to the Commons. Default = private/local
  (federation-v2's default); `node:govern` is where an operator opts a class of holons in.
- **Peer allow/deny** — which peer nodes this node will resolve references from / collaborate
  with. A `FederationPeerPolicy` (allowlist / denylist / conformance-threshold).
- **Certification/conformance requirements for peers** — the minimum G-suite gate conformance a
  peer must publish (federation-v2 L0 `CertificationProof`) before this node will interoperate.
  E.g. "only resolve references from peers passing G1,G2,G3,G5,G7." This is the operator turning
  the G-suite conformance standard into an ENFORCED local admission rule.

All three are **config records the federation-v2 client reads**; this track owns the
*governance surface* for them, federation-v2 owns the *substrate that enforces them*. They are
hard-gated on federation-v2 because a peer policy with no peers is inert.

### 3. Ecosystem incentives  *(deferred parameter surface; Phase 2)*

Operator-defined reward/incentive **parameters** — NOT a rules engine (explicit MVP boundary):

- quest-completion bonuses (an extra grant on `MarkRunCompletedAsync`),
- referral incentives (a grant keyed to a referral attribution),
- staking-style rules (a parameterised hold-to-earn knob).

Kept as a `NodeIncentiveParameters` config record read by the existing economic-primitive quest
nodes (Grant/Emit/Transfer). We do NOT build a general rules DSL; we expose a bounded parameter
set the existing Tier-2 nodes consume. The alpha-cheap subset (a flat quest-completion bonus
parameter) *could* fold into Phase 1 if a concrete need appears, but is scoped to Phase 2 by
default to keep the alpha slice tight.

### 4. Governance parameters  *(partly alpha-cheap; Phase 1 for the local knobs)*

The knobs that define the node's economy:

- **which chains** the node's economy operates on (an allowlist over the provider set),
- **which asset types** are allowed (an allowlist over the `HolonType` / `AssetType` vocabulary),
- **membership/onboarding rules** — the node's local policy for how avatars join its economy
  (open / invite / KYC-gated), composing the existing KYC gate + the quest-invitations model.

The chain-allowlist and asset-type-allowlist are **node-local, enforceable today** (they gate
which provider/holon-type an action may use) and belong in the **Phase 1** alpha slice as a
`NodeGovernanceParameters` config record. Membership/onboarding rules that reach across nodes
are Phase 2.

## The interop constraint (the crux)

The owner's phrase — *"so long as it interops with the core technology and others want to
collab"* — is the design boundary. State it crisply:

> **An operator may set ARBITRARY LOCAL rules for their own economy. A rule only takes effect
> ACROSS a node boundary when BOTH conditions hold: (a) conformance — the collaborating parties
> speak the core protocol; and (b) mutual opt-in — each peer independently chooses to
> collaborate. Local sovereignty is unconstrained; cross-node collaboration is interop-gated.**

Concretely, the boundary is drawn at three checkpoints:

1. **Local rules are unconstrained.** Fee schedules, chain/asset allowlists, incentive
   parameters, membership rules — an operator sets these freely. They govern only actions that
   settle on THIS node. Nothing gates a node's right to charge whatever it wants or reward
   whatever it wants locally.

2. **Conformance to the core protocol** is required for any cross-node effect. "Core technology"
   = the shared contract: the **G-suite conformance** (federation-v2 L0 `CertificationProof`),
   the **versioned `HolonType` vocabulary** (federation-v2 P1 — a foreign holon is only
   interpretable if both nodes reference the same versioned type contract), and the
   **agent-signing / attestation format** (how Commons entries are signed and verified
   fail-closed). A node whose rules produce artifacts that don't conform simply can't be
   collaborated with — the peer's federation client rejects the reference fail-closed. Conformance
   is not enforced by a central authority; it is enforced by each peer's own admission rules
   (checkpoint 3) reading the published conformance facts.

3. **Mutual opt-in** — collaboration is bilateral and voluntary. Node A publishing a
   `HolonReference` does NOT obligate Node B to resolve it; Node B's `FederationPeerPolicy`
   (capability #2 above) decides whether to accept A. Symmetrically, A's policy decides whom A
   resolves from. There is no global governor, no forced federation, no cross-node authority.
   This is why `node:govern` is **node-local** (§scope): a peer relationship never confers
   governance over the peer.

The result is exactly the owner's model: **local economic freedom + interop-gated,
opt-in-only, conformance-required federation.** An operator's arbitrary local rules can never
be imposed on a peer, and a peer can never be dragged into an economy it didn't consent to.

## Relationship to existing tracks

- **avatar-dapp-rbac (depends_on).** That track just shipped the scope-manager + policy model
  (`DappDevelop`/`DappManage` policies, role-bounded self-issuance, JWT-stamps-allowed-scopes).
  `node:govern` plugs into the SAME machinery: a new `NodeGovern` authorization policy, a new
  constant on `AzoaScopes`, membership on `ApiKeyForbiddenScopes` (JWT-only), and JWT issuance
  stamps it onto the node-operator identity. Reusing this model is why the scope side is cheap.
- **final-hardening-cutover (depends_on).** Provides the value engine, `AllocationManager` seam,
  `HolonType` registry, and MCP surface that fee-setting and asset-type governance read/extend.
- **federation-v2 (related, hard gate for Phase 2).** Federation RULES are governed here; the
  SUBSTRATE that enforces them lives there. Phase 2 cannot ship before federation-v2's own
  activation gate. This track is the "set the federation rules" half federation-v2's thin
  operator surface deliberately left out of scope.
- **fiat-stripe-bridge (related).** The `TOKENS_PER_CENT` operator-authoritative price schedule
  is the design precedent for `NodeFeeSchedule` — a server-side, operator-configured rate that
  the caller can never override.
- **data-engine-decision / consent-gate-architecture (related, invariants).** Fees settle
  on-chain, never as off-chain balance; fee application never bypasses the custody chokepoint.

## Acceptance criteria

1. `AzoaScopes.NodeGovern` (`"node:govern"`) exists, is on `ApiKeyForbiddenScopes`, is absent
   from `SelfIssuableScopes` / `IssuableCapabilityScopes`, and is stripped at API-key claim-emit
   time (a key CSV containing `node:govern` can never satisfy the `NodeGovern` policy).
2. A `NodeGovern` authorization policy exists and gates the governance surfaces; only a
   JWT-authenticated identity carrying the claim reaches them.
3. On a single-operator sovereign node, the node-operator identity implicitly carries
   `node:govern` (stamped by the JWT issuer alongside the admin identity).
4. **Fee schedule (Phase 1):** an operator can set a versioned `NodeFeeSchedule`; a subsequent
   mint/transfer/swap/quest-completion applies the configured fee; the fee settles on-chain to
   the node treasury (or as a fee-adjusted allocation), never as an off-chain balance; a
   redelivered idempotent request never double-charges; changes are audited and non-retroactive
   to in-flight claims.
5. **Governance parameters (Phase 1):** an operator can set a chain-allowlist and an
   asset-type-allowlist through the `NodeGovern` surface; every edit writes an immutable
   audit row; an action targeting a disallowed chain/asset-type is rejected fail-closed.
6. **Interop boundary:** a local rule (fee/allowlist) governs only local-settling actions; NO
   local rule can be imposed on a peer; documented + tested that `node:govern` is node-local.
7. **Federation rules (Phase 2, gated):** federation allow/deny, peer allow/deny, and
   peer-conformance-threshold config records exist and are read by the federation-v2 client;
   a peer below the conformance threshold is not collaborated with fail-closed; mutual opt-in is
   enforced (A publishing does not obligate B).
8. **Ecosystem incentives (Phase 2):** a bounded `NodeIncentiveParameters` set (quest-completion
   bonus, referral, staking-style) is read by the existing Tier-2 economic nodes; no general
   rules DSL is introduced.
9. Every governance write is audited in an immutable trail; no governance surface touches another
   avatar's keys, runs a backfill, or decrypts a user key (it is not `operator:admin`).

## Explicit non-goals

- ❌ **A general rules-engine / governance DSL** — incentives and rules are a BOUNDED parameter
  surface read by existing engines, not a Turing-complete rule language. MVP discipline.
- ❌ **Off-chain fee balances** — fees settle on-chain or fail; AZOA never becomes a ledger.
- ❌ **Cross-node governance authority** — `node:govern` is node-local; a peer relationship never
  confers governance over a peer. No global governor.
- ❌ **Folding into `operator:admin`** — kept a separate scope so economic governance can be
  delegated without handing over destructive infra keys.
- ❌ **Building Phase 2 (federation rules) before federation-v2's activation gate** — a peer
  policy with no peers is inert.
- ❌ **Key-issuable governance** — `node:govern` is JWT-only, on the forbidden-scopes denylist.
