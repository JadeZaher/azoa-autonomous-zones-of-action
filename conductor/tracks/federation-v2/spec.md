---
type: spec
track: federation-v2
created: 2026-07-05
status: deferred
horizon: v2
activation_gate: >-
  DO NOT START until build-side traction justifies it: ≥3 independent operators
  running production nodes AND ≥1 concrete cross-node use case with a real
  counterparty (e.g. ArdaNova + a partner economy that must reference each other's
  assets/identities). Federating before that is a protocol with one implementer —
  spec theater. The ONE exception is Layer 0 (§L0), which is cheap enough to land
  during alpha hardening and pays off standalone.
depends_on:
  - final-hardening-cutover        # value engine, custody, holon graph, MCP surface
  - user-sovereign-identity        # self-owned avatars = portable identity substrate
  - tenant-consent-delegation      # revocable per-scope grants = per-node consent substrate
related:
  - consent-gate-architecture
  - data-engine-decision
  - user-self-sovereignty-initiative
---

# Track: federation-v2

## Goal

Let independent, sovereign AZOA nodes **interoperate without any node surrendering
custody, data control, or autonomy** — turning "N isolated nodes" into "a network a
user and their assets can move through." Federation solves only the *soft* problems
(discovery, identity portability, cross-node references, reference verification); it
deliberately does **not** federate value, because the chain is already the neutral
settlement layer every node treats as source of truth ([[data-engine-decision]]).

Mental model: **ActivityPub / Mastodon**, not blockchain consensus — loosely-coupled
sovereign servers exchanging *signed documents*, discovery via configuration then
directories. Never a global ledger, never a shared DB, never a hub SaaS.

## Why this is deferred (not-needed-yet, on purpose)

As of 2026-07-05 every AZOA deployment is a sovereign single node; there is no
network, so consume-side network effects are structurally zero. That is the correct
state pre-traction: a federation protocol with two implementers validates nothing.
This track is the **captured design** so that when the activation gate (frontmatter)
is met, execution is a matter of building, not re-deriving. See the market read that
motivated it: build-side utility is real and present; consume-side "network" utility
is latent and must follow node adoption, not lead it.

## Core insight (why this is additive, not a rewrite)

Two nodes never need to trust each other about balances — **the chain settles between
them**. So federation reduces to four additive protocols layered on primitives that
already exist (node crypto via `WalletKeyService`/`Secp256k1VaaSignatureVerifier`,
the composable holon graph, the `HolonTypeRegistry` schema contract (F5), the
self-sovereign avatar + revocable consent model, and the MCP read surface). No change
to the value engine. No node imports another's database.

---

## Layers (smallest-viable-first; each independently shippable)

### L0 — Node identity + conformance manifest  *(the prerequisite; alpha-cheap)*

Each node holds a keypair and publishes a signed descriptor at a well-known path:

```
GET /.well-known/azoa-node.json
{
  "nodeId":            "<stable id>",
  "publicKey":         "<node signing pubkey>",
  "namespace":         "<surreal ns / tenant scope>",
  "chains":            ["algorand"],           // real-value chains this node runs
  "holonSchemaVersion":"<HolonTypeRegistry version>",
  "gateConformance":   ["G1","G2","G3","G5","G7"],  // which node-operator gates this node passes
  "endpoints":         { "read": "...", "resolve": "...", "directory": "..." },
  "signature":         "<node-key signature over the canonical body>"
}
```

- **Trust primitive:** peers decide whether to interoperate based on *published gate
  conformance*, not blind trust. This is the standardizable artifact — the G1–G7
  suite becomes a public **node-operator conformance standard**, and the descriptor
  is how a node proves it passes.
- Reuse existing node crypto for the signature; reuse the health/ops surface for the
  endpoint. **~1–2 days.** Ship during alpha hardening: changes nothing operationally,
  makes each node a legible participant, and is the foundation every later layer needs.
- AC: descriptor served + signed + signature-verifiable by a third party; conformance
  list is generated from actual gate runs, not hand-authored.

### L1 — Verifiable cross-node holon references  *(the core)*

The unit of federation is a **portable reference**, never data replication:

```
azoa://{nodeId}/holon/{id}
  → resolve via that node's public read API
  → returns the holon + a node-signed attestation of provenance
```

- The consuming node **verifies the attestation signature against the descriptor key**
  (same fail-closed discipline as the VAA verifier — never trust payload blind) and
  caches with a TTL. Fetch-and-cache reuses idempotency + `SurrealTransientConflict`
  discipline.
- The holon graph is already parent/child-composable; this lets a graph **edge cross a
  node boundary** — Node B composes a holon that *points at* a holon issued on Node A
  (a fractionalized asset, a partner-economy membership) with provable provenance,
  without either importing the other's DB.
- `HolonTypeRegistry` (F5) is the **shared schema contract**: a foreign holon's
  `AssetType` is interpretable because both nodes reference the same (versioned) type
  vocabulary. Cross-node type mismatch = a first-class, surfaced condition.
- AC: a foreign `azoa://` reference resolves, verifies, and renders in a local holon
  graph read; a *tampered* attestation is rejected; a reference to a down/unknown node
  degrades gracefully (cached-stale or explicit unavailable, never a fake).

### L2 — Discovery  *(deliberately dumb first; escalate only on demand)*

- **Phase A — static peer lists.** Operators configure the nodes they federate with
  (`Federation:Peers`). Covers the real early case (a consortium / ArdaNova-plus-partners
  cluster). Zero network infrastructure.
- **Phase B — directory nodes.** An optional AZOA node running a `directory` role that
  peers register their descriptor with and query. Federated like email/Mastodon — many
  directories, no single owner, no consensus.
- **Phase C — gossip/DHT.** Only if scale ever demands it. Almost certainly never; do
  not build speculatively.
- AC (Phase A): a node federates with a configured peer end-to-end (descriptor fetch →
  reference resolve → verify) using only static config.

### L3 — Identity portability  *(the consume-side unlock)*

The hard part already exists ([[user-sovereign-identity]] + [[consent-gate-architecture]]):
a self-owning avatar can present the **same identity to multiple nodes** and issue
**revocable, per-node-scoped consent grants**.

- Federation adds: an avatar proves "same principal on Node A and Node B" via a signed
  challenge (reuse `IWalletSignatureVerifier` / the WalletAuth challenge flow).
- This turns N sovereign nodes into a **network a user moves through** — the actual
  consume-side value. Consent grants stay per-node and revocable, so portability never
  becomes cross-node custody.
- AC: an avatar authenticates the same key against two nodes; a grant on Node A is
  provably absent on Node B unless separately issued; revocation on one node does not
  require the other.

---

## Explicit non-goals (anti-patterns that would destroy the product)

- ❌ **Global consensus / a federation blockchain** — chains already provide settlement.
- ❌ **Shared/replicated SurrealDB across nodes** — kills sovereignty; recreates the
  trust problem federation exists to avoid.
- ❌ **A hub SaaS all nodes route through** — recreates the custody centralization the
  sovereign-operator model differentiates against.
- ❌ **Federating value** — value moves on-chain between nodes as ordinary chain ops;
  keep federation in the safe, soft-state (discovery / reference / identity) domain.
- ❌ **Building L1–L3 before the activation gate** — validate the node first.

## Acceptance criteria (whole track)

1. L0 descriptor + conformance manifest served, signed, third-party-verifiable, and
   generated from real gate runs.
2. L1 cross-node holon reference resolves + verifies + caches; tampered attestation
   rejected; unavailable-node degrades honestly.
3. L2 Phase-A static-peer federation works end-to-end; directory (Phase B) optional.
4. L3 same-key identity proof across two nodes with per-node revocable grants intact.
5. No node surrenders custody, DB control, or autonomy at any layer; every cross-node
   input is signature-verified fail-closed; no value is federated off-chain.
6. The G-suite conformance manifest is published as a **node-operator standard** doc
   (the standardizable artifact), separable from this implementation.

## Sequencing note

L0 is alpha-cheap and worth landing early (it's the foundation and pays off
standalone). L1–L3 are post-alpha / early-v2 and gated on the activation criteria in
frontmatter. Recommended order strictly L0 → L1 → L2(A) → L3, each a shippable
increment; stop at any layer that satisfies the then-current need.
