---
type: plan
track: node-operator-governance
created: 2026-07-10
status: in_progress
horizon: alpha-partial
depends_on:
  - avatar-dapp-rbac
  - final-hardening-cutover
related:
  - federation-v2
  - fiat-stripe-bridge
  - data-engine-decision
---

# Plan: node-operator-governance

Two phases, split on the federation-v2 activation gate. Phase 1 is the alpha-cheap
sovereign-node slice (scope + local fee/parameter config). Phase 2 is everything that
needs peers to exist, and is hard-gated on federation-v2.

## Phase 1 — Sovereign-node governance  *(alpha-cheap; may land during alpha hardening)*

The value-standalone slice: a single sovereign node can charge for economic actions and
declare its own economy knobs. Needs NO federation substrate.

### 1.1 The scope
- Add `AzoaScopes.NodeGovern = "node:govern"`.
- Add it to `ApiKeyForbiddenScopes` (JWT-only, stripped at API-key claim-emit time — same
  discipline as `Operator`).
- Confirm it is ABSENT from `SelfIssuableScopes` and `IssuableCapabilityScopes`.
- Add a `NodeGovern` authorization policy (mirrors the `DappManage`/`Operator` policy shape).
- JWT issuance stamps `node:govern` onto the node-operator identity (rides the same
  final-hardening "operator:admin JWT-issuer scope wiring" seam; on a sovereign node the
  operator identity implicitly carries both).
- **AC:** a key CSV containing `node:govern` never satisfies the policy; only a JWT identity
  with the claim reaches the governance surfaces.

### 1.2 Fee schedules (local, on-chain-settling)
- `NodeFeeSchedule` config record (node-scoped, versioned): per-operation-type
  `{ flatBaseUnits, bps }` for Mint / Transfer / Swap / QuestComplete / FederationPublish.
  Modeled on the fiat-stripe-bridge `TOKENS_PER_CENT` operator-authoritative-rate pattern
  (server-side, never client-supplied).
- Fee application seam in the value path: at broadcast time the computed fee settles on-chain
  to a configured `NodeTreasuryWallet` (second chain op) OR, on the fiat-mint path, reduces the
  minted quantity / mints a treasury share inside `AllocationManager.AllocateAsync` BEFORE the
  typed op is built — and inside the SAME idempotency claim (no double-charge on redelivery).
- Immutable `node_fee_audit` trail; changes non-retroactive to in-flight claims.
- **AC:** setting a schedule then minting/transferring applies the fee on-chain; redelivery
  never double-charges; a fee that can't settle on-chain fails the action closed (never a
  phantom off-chain balance).

### 1.3 Governance parameters (local knobs only)
- `NodeGovernanceParameters` config record: chain-allowlist (over the provider set) +
  asset-type-allowlist (over the `HolonType`/`AssetType` vocabulary).
- Enforcement: an action targeting a disallowed chain / asset-type is rejected fail-closed.
- **AC:** disallowed chain/asset-type action rejected; allowlist edit is audited.

Status 2026-07-11: the reusable enforcement primitive has landed as dynamic
`NodeGovernanceParameters` allowlists over chains and asset types, governed by
the JWT-only `NodeGovern` policy. It is wired into allocation, fungible-token
launch, and Holon asset-type writes and fails before idempotency claims or
value/store side effects. The `node_governance_parameters:local` row and
append-only `node_governance_audit` rows are written together in one SurrealDB
transaction.

The fee-schedule slice now has a required fail-closed manager, versioned
schedule plus append-only audit, value-idempotent retries, expected-version CAS,
and allocation Mint/Transfer netting inside the won allocation claim. Gross,
fee, net, and schedule version are persisted into the operation/replay payload;
crash reconciliation restores the complete allocation result. Raw NFT, Swap,
QuestComplete, and FederationPublish consumers remain open Phase 1 work; their
configured entries must not be called enforced until those value paths quote
and settle them.

### 1.4 Interop-boundary invariant (documented + tested even pre-federation)
- Assert `node:govern` is node-local: a local rule governs only local-settling actions; no
  local rule reaches a peer. This is a test + doc even in Phase 1 so the boundary is fixed
  before Phase 2 builds on it.

**Phase 1 exit:** a sovereign node charges configurable fees, enforces chain/asset allowlists,
audits every governance write, and the node-local invariant is pinned. No peers required.

## Phase 2 — Federated governance  *(HARD-GATED on federation-v2 activation gate)*

Do NOT start until federation-v2 reaches its own gate (>=3 operators + >=1 real cross-node
use case). A peer policy with no peers is inert.

### 2.1 Federation rules
- `FederationHolonPolicy` (per-type/tag/list allow-deny) consulted by federation-v2's L1
  "submit a holon for federation" before publishing a `HolonReference`. Default private/local.
- `FederationPeerPolicy` (peer allowlist/denylist) — which peers this node resolves from.
- Per-peer `ConformanceThreshold` — minimum G-suite gates a peer must publish (L0
  `CertificationProof`) before this node collaborates; below-threshold peer rejected fail-closed.

### 2.2 Ecosystem incentives (bounded parameters)
- `NodeIncentiveParameters`: quest-completion bonus, referral incentive, staking-style knob —
  read by the existing Tier-2 economic nodes (Grant/Emit/Transfer). NO general rules DSL.

### 2.3 Cross-node interop enforcement
- Enforce the crux: a cross-node effect requires conformance (core protocol: G-suite +
  versioned HolonType + agent-signing format) AND mutual opt-in (A publishing does not obligate
  B; B's `FederationPeerPolicy` decides). Test both directions.

### 2.4 Membership/onboarding rules (cross-node reach)
- Node-local join policy (open / invite / KYC-gated) composing the existing KYC gate +
  quest-invitations model, extended to the federated case.

**Phase 2 exit:** an operator sets enforced federation rules + peer policies + bounded
incentive parameters; cross-node collaboration is conformance-required and opt-in-only; no
local rule is ever imposed on a peer.

## Prerequisites carried from related tracks
- avatar-dapp-rbac scope-manager + policy model must be shipped (Phase 1 reuses it).
- final-hardening value engine / `AllocationManager` / `HolonType` registry (Phase 1 fee seam).
- federation-v2 P0 (gate-result serializer), P1 (versioned HolonType vocab), and its L0/L1
  client must exist before Phase 2 (the conformance threshold + holon-reference policy read them).
