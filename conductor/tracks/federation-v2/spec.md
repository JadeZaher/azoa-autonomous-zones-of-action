---
type: spec
track: federation-v2
created: 2026-07-05
revised: 2026-07-12
status: deferred
horizon: v2
activation_gate: >-
  DO NOT START the federation build until build-side traction justifies it: ≥3
  independent operators running production nodes AND ≥1 concrete cross-node use case
  with a real counterparty (e.g. ArdaNova + a partner economy that must reference each
  other's assets/identities). Federating before that is a P2P network with one member —
  spec theater. The ONE exception is Layer 0 (§L0), the node-identity + conformance
  manifest, which is cheap enough to land during alpha hardening and pays off
  standalone (it is also the certification primitive the Commons network later reads).
depends_on:
  - final-hardening-cutover        # value engine, custody, holon graph, MCP surface
  - user-sovereign-identity        # self-owned avatars = portable identity substrate
  - tenant-consent-delegation      # revocable per-scope grants = per-node consent substrate
related:
  - consent-gate-architecture
  - data-engine-decision
  - user-self-sovereignty-initiative
  - holochain-dotnet-client-review
  - fiat-stripe-bridge
supersedes_framing: >-
  The prior revision framed federation as "ActivityPub, NOT consensus — signed JSON over
  HTTPS" with Holochain as a deferred/optional substrate. This revision (2026-07-10)
  reframes to Holochain-FIRST per owner direction: the hApp IS the public ledger, the
  Commons DHT is the shared substrate, federation is peer-to-peer, and the heavy runtime
  lives in a SEPARATE dedicated repo (AZOA.Commons). See review-2026-07-10.md for the
  decision log and review-2026-07-12.md for the package/runtime boundary.
---

# Track: federation-v2

## Goal

Let independent, sovereign AZOA nodes **join a peer-to-peer network community without any
node surrendering custody, private data control, or autonomy** — turning "N isolated
nodes" into "a commons a user and their assets can move through."

Federation is **Holochain-first**: the shared substrate is a community-maintained **hApp**
whose DHT holds federation *facts* — holon publications, cross-node references, node
identity, and proof-of-certification — as validated, agent-signed entries. Each operator
runs a hApp cell (embedded in their AZOA deployment or as a separate service) and thereby
becomes a member of the network. The hApp — not a hub, not a global chain, not a shared
SQL database — is the public ledger.

Federation deliberately does **not** federate value: the chain remains the neutral
settlement layer every node treats as source of truth ([[data-engine-decision]]). The
Commons carries verifiable *references* to and *facts about* value events, never custody
or off-chain balance authority.

Mental model: **an agent-centric P2P community**, not blockchain consensus and not a
Mastodon-style hub-and-server topology. Each operator is a first-class agent in a shared
distributed graph. There is no global consensus round; validity is enforced by the DNA's
validation rules, and each agent holds its own source chain. HTTPS survives only as a
**bootstrap / fallback transport** (see §Transport), not as the federation model.

## The two repos (crux of this design)

Federation is **its own dedicated base repo — `AZOA.Commons`** — not code baked into the
AZOA node monorepo. This is the single most important structural decision in this spec.

| Repo | Owns | Does NOT own |
|---|---|---|
| **`AZOA.Commons`** (new, separate) | The Holochain **DNA/hApp** (zomes + validation rules for the federation facts), the conductor/runtime story, the .NET (or Rust-binding) **Holochain client**, the Commons entry schemas, and the certification/conformance vocabulary. This is what the *community* maintains — a public hApp/DNA anyone can run, plus optionally a private one. | AZOA node domain logic, SurrealDB access, the value engine, quest execution, custody. |
| **`azoa` node** (this monorepo) | A **thin integration client** (`AZOA.Commons.Client`) and a small `HoloFederationService` that maps between local domain objects and Commons entries. Nothing more. | The Holochain runtime, the DNA, consensus/validation logic, the P2P networking. |

The AZOA node's coupling to federation is a **slim client dependency**, not the Holochain
runtime. An operator who never federates never pulls the runtime. The community maintaining
`AZOA.Commons` can evolve the DNA independently of AZOA node releases (with a versioned
DNA/schema contract between them — see open decisions).

## Why this is deferred (not-needed-yet, on purpose)

As of the activation gate, every AZOA deployment is a sovereign single node; there is no
network, so consume-side network effects are structurally zero. A P2P network with one
member validates nothing. This track is the **captured design** so that when the gate is
met, execution is a matter of building `AZOA.Commons` + the thin node client, not
re-deriving the architecture. **The deferral is discipline, not a build order** — L0
(node identity + conformance) can and should land early because it is cheap, standalone,
and the exact primitive the Commons network later reads to gate certification.

## Sovereignty invariant: shared substrate ≠ shared data (READ THIS)

Holochain-first appears to collide with the old "no shared DB" non-goal. It does not — and
the reconciliation is the whole point of the design:

- The **Commons DHT is a shared substrate for federation FACTS**: holon *references* and
  publications an operator has chosen to federate, node identity descriptors, and
  proof-of-certification (conformance) entries. These are things an operator *deliberately
  publishes to the network*.
- The **operator's private SurrealDB, custody keys, customer PII, and unfederated holons
  are NEVER on the DHT.** A holon isn't federated until its owner explicitly publishes a
  reference; even then the payload the peer resolves is provenance-attested, not a copy of
  the private database.

So: **the Commons holds public facts the operator opted into; it never holds the operator's
private state or custody.** A shared *fact ledger* is not a shared *database*. This is how
Holochain-first stays sovereignty-preserving — and it is precisely why federating value is
still forbidden (value lives on-chain; the Commons only references it).

---

## Layers as capabilities (reframed onto the Holochain substrate)

The L0→L3 progression survives as **capabilities**, not as an HTTPS document-exchange
protocol. Each is an independently shippable increment; each is now expressed as Commons
entries + validation rules rather than well-known JSON endpoints.

### L0 — Node identity + conformance manifest  *(the prerequisite; alpha-cheap)*

Each node is (or maps to) a **Holochain agent** and publishes a signed **node descriptor**
+ **proof-of-certification** entry to the Commons DHT:

```
NodeDescriptor {
  nodeId, agentPubKey, namespace, chains[], holonSchemaVersion,
  gateConformance: ["G1","G2","G3","G5","G7"],   // gates this node passes
  endpoints: { resolve, directory },              // for HTTPS fallback / bootstrap
  // signed by the hApp agent key (see §Identity)
}
CertificationProof {
  nodeId, conformanceManifest,   // MACHINE-READABLE gate-result artifact (see prereq)
  producedAt, gateSuiteVersion,
}
```

- **Trust primitive:** peers decide whether to interoperate based on *published gate
  conformance*, validated by the DNA's rules — not blind trust. The G1–G7 suite becomes a
  public **node-operator conformance standard**; the certification proof is how a node
  proves it passes, published to the network as a certification entry.
- **Alpha-cheap portion:** the *local* half — generating and signing the descriptor +
  conformance manifest — can ship during alpha hardening with no Commons runtime at all
  (served at a well-known path as HTTPS bootstrap). The *publish-to-DHT* half waits for the
  Commons repo. Landing the local half early makes each node legible and standalone.
- **Real prerequisite (architect finding, valid):** the conformance manifest **has no
  generation source today.** G1–G7 are xUnit pass/fail tests emitting no machine-readable
  artifact. A **gate-result serializer** (tests → signed JSON manifest) MUST exist before
  "submit audit data for certification" is real. Flagged as prerequisite P0.
- AC: descriptor + conformance manifest generated from *real gate runs* (not hand-authored),
  signed, third-party-verifiable; once Commons exists, published as a certification entry
  the DNA validates.

### L1 — Verifiable cross-node holon references  *(the core)*

The unit of federation is a **portable reference published to the Commons**, never data
replication:

```
azoa://{nodeId}/holon/{id}
  → a HolonReference entry on the DHT: { nodeId, holonId, type, holonSchemaVersion,
                                         resolveHint, agent-signed provenance }
  → resolve the actual holon via the issuing node (Commons resolveHint / HTTPS fallback)
  → returns the holon + agent-signed attestation of provenance
```

- The consuming node **verifies the attestation against the issuing agent's key** (same
  fail-closed discipline as the VAA verifier — never trust payload blind) and caches with a
  TTL. Under Holochain-first, entry authorship is already agent-signed and DNA-validated, so
  much of the verification is substrate-native; the node still re-verifies on resolve.
- The holon graph is already parent/child-composable; this lets a graph **edge cross a node
  boundary** — Node B composes a holon that *points at* a holon issued on Node A (a
  fractionalized asset, a partner-economy membership) with provable provenance, without
  either importing the other's private DB.
- **Real prerequisite (architect finding, valid):** `HolonType` is currently **unversioned,
  fail-open, and per-node.** Cross-node interpretation requires a **versioned type
  vocabulary** shared through the Commons (a foreign holon's `AssetType` is only
  interpretable if both nodes reference the same versioned type contract). Cross-node type
  mismatch must be a first-class, surfaced, fail-closed condition. Flagged as prerequisite P1.
- AC: a foreign `azoa://` reference resolves, verifies, and renders in a local holon graph
  read; a *tampered* attestation is rejected; a reference to a down/unknown node degrades
  gracefully (cached-stale or explicit unavailable, never a fake).

### L2 — Discovery  *(substrate-native, but stay dumb first)*

Under Holochain-first, discovery is largely **inherent to the DHT** — agents find each other
and query published descriptors/references through the shared graph. Still escalate only on
demand:

- **Phase A — bootstrap peer lists.** Operators configure initial peers / a bootstrap
  server (`Federation:Bootstrap`) so cells find the network. Covers the real early case (an
  ArdaNova-plus-partners consortium). This is Holochain's normal bootstrap, not a hub.
- **Phase B — DHT-native discovery.** Query the Commons for descriptors by capability /
  conformance / chain — the substrate already gossips this. No separate directory service to
  build; the hApp *is* the directory.
- **Phase C — richer indexing** only if scale ever demands it (an indexing zome or an
  optional index agent). Do not build speculatively.
- AC (Phase A): a node joins the Commons via bootstrap and resolves a peer's descriptor +
  a holon reference end-to-end.

### L3 — Identity portability  *(the consume-side unlock)*

The hard part already exists ([[user-sovereign-identity]] + [[consent-gate-architecture]]):
a self-owning avatar presents the **same identity to multiple nodes** and issues
**revocable, per-node-scoped consent grants**.

- Federation adds: an avatar proves "same principal on Node A and Node B" via a signed
  challenge (reuse `IWalletSignatureVerifier` / the WalletAuth challenge flow). Note this is
  the **avatar/user** identity, distinct from the **node/agent** identity that signs Commons
  entries (see §Identity) — do not conflate them.
- This turns N sovereign nodes into a **community a user moves through** — the actual
  consume-side value. Consent grants stay per-node and revocable, so portability never
  becomes cross-node custody.
- AC: an avatar authenticates the same key against two nodes; a grant on Node A is provably
  absent on Node B unless separately issued; revocation on one node does not require the other.

---

## The thin node-operator surface (keep it minimal)

A node operator's *required* interaction with the Commons is **three core functions**. Keep
this surface small; everything else is optional or Commons-side.

1. **Submit a holon for federation.** Publish a holon reference/entry to the Commons DHT
   (the L1 `HolonReference`). Default remains private/local; a holon is federated only when
   its owner opts in per-holon (or via a quest that opts in, or a template share).
2. **Submit audit/conformance data to stay certified.** Publish the L0 certification proof
   (the machine-readable gate-result manifest) so the node remains a certified member the
   network trusts. Certification is a live, re-published fact, not a one-time badge.
3. **Host a hApp instance.** Run a Commons cell — **either embedded in the AZOA deployment
   or as a separate service** at the operator's choice. Hosting is what makes the operator a
   P2P community member.

Product-level federation *modes* (per-holon, quest-on-complete, template federation) all
reduce to function #1 with different triggers:

- **User-driven per-holon:** operator/user marks a holon federatable, publishes a reference.
- **Quest-driven on complete:** a quest completion emits a signed, idempotent federation
  receipt referencing run/holon outputs — never copying the local DB.
- **Template federation:** share quest/holon/node/economic templates as Commons entries;
  imported templates stay provenance-tagged and locally reviewed before activation.

Fiat integrations use these modes only as receipts/metadata: Stripe or other fiat can
produce AZOA allocations locally; federation publishes verifiable *facts* about the
allocation/outcome — never the processor secret, customer PII, or off-chain balance authority.

---

## Identity: what key the node presents to the Commons

Two distinct identities, do not conflate:

- **Node/agent identity (NEW-but-mostly-moot under Holochain-first).** Each node maps to a
  **Holochain agent**; the **hApp agent key signs Commons entries** natively. This largely
  subsumes the old "node needs a Sign() primitive" gap — `WalletKeyService` today does
  keygen+encrypt only (no node `Sign()`), but under Holochain-first the *agent* key is the
  entry-signing identity. **Open decision:** is the agent key derived from / bound to the
  node's existing key material (so the descriptor and the agent are provably the same
  principal), or independent with a signed linking entry? Capture: the node MUST present a
  stable, verifiable agent identity that a peer can tie to the L0 descriptor.
- **Avatar/user identity (already exists).** Self-sovereign avatars + WalletAuth challenge
  (L3). Unchanged.

---

## Transport: Holochain-first, HTTPS as fallback/bootstrap only

- **Primary:** peer-to-peer over Holochain (the Commons hApp) — publish/validate/gossip
  entries, resolve references through the substrate.
- **Fallback/bootstrap only:** HTTPS remains for (a) bootstrap into the network, (b) the
  standalone alpha-cheap L0 descriptor at a well-known path before Commons exists, and (c)
  resolving a holon payload from the issuing node when a resolveHint points at its read API.
  HTTPS is a transport detail, **not** the federation model. There is no signed-JSON-document
  federation protocol; the federation protocol is the DNA.

---

## Holochain client story (open — the key AZOA.Commons build decision)

The vendored `NextGenSoftwareUK/holochain-client-csharp` is **prior art only, do not vendor
as a direct dependency.** Confirmed blockers (architect finding, valid): a
GPLv3-`LICENSE`-vs-MIT-`csproj` conflict, a native `holochain_serialisation_wrapper.dll`,
and 3 external `ProjectReference`s (no standalone build). Keep the do-not-vendor stance.

Under Holochain-first, `AZOA.Commons` needs its **own** client story. **Open decision:**

- **Option A — roll our own generic .NET Holochain client** (Admin/App WebSocket, MessagePack
  DTOs, app-auth tokens, zome calls, signals, timeouts, typed errors). Reusable outside AZOA;
  no opaque native DLL. Highest effort, cleanest.
- **Option B — Rust binding / sidecar.** Bind to the Holochain conductor in Rust and expose a
  slim FFI or local socket to .NET. Less .NET surface to maintain; adds a Rust build.
- **Option C — supervised conductor + minimal WS client.** Run/lifecycle a local conductor
  (loopback WebSockets), talk to it with the smallest client that works. Pragmatic first step.

Whichever is chosen, the **runtime lives in `AZOA.Commons`, not the AZOA node.** The node
takes only the slim client. Revisit a tighter binding only after the first client is correct,
publishable, tested, and useful by itself.

---

## Explicit non-goals (anti-patterns that would destroy the product)

- ❌ **Global consensus / a federation blockchain** — chains already settle value; the
  Commons DNA validates federation *facts*, it does not run a consensus round over value.
- ❌ **A hub SaaS all nodes route through** — the Commons is a P2P community, not a
  centralized server everyone depends on. No single owner of the network.
- ❌ **Shared/replicated *private* state across nodes** — the DHT holds opted-in public
  facts ONLY (references, descriptors, certification). No operator's SurrealDB, custody keys,
  PII, or unfederated holons ever go on the substrate. (See §Sovereignty invariant.)
- ❌ **Surrendering custody** — custody stays local; the Commons references value events, it
  never holds keys or balance authority.
- ❌ **Federating value** — value moves on-chain between nodes as ordinary chain ops;
  federation stays in the safe soft-state (reference / identity / certification) domain.
- ❌ **Building the Commons before the activation gate** — validate the node first. (L0's
  local half is the sanctioned exception.)
- ❌ **Baking the Holochain runtime into the AZOA node monorepo** — it lives in
  `AZOA.Commons`; the node takes a thin client.

## Acceptance criteria (whole track)

1. `AZOA.Commons` exists as a **separate repo** with a versioned DNA/hApp and a client the
   AZOA node consumes as a **thin dependency** (runtime not baked into the node).
2. L0: node descriptor + **machine-readable conformance manifest generated from real gate
   runs**, agent-signed, third-party-verifiable, published to the Commons as a certification
   entry (and served standalone via HTTPS bootstrap pre-Commons).
3. L1: cross-node holon reference publishes, resolves, verifies, and caches; tampered
   attestation rejected; unavailable-node degrades honestly; cross-node **type mismatch is
   surfaced fail-closed** against a versioned `HolonType` vocabulary.
4. L2: a node joins via bootstrap and discovers peers/references through the DHT.
5. L3: same-key avatar identity proof across two nodes with per-node revocable grants intact,
   kept distinct from node/agent identity.
6. The three-function operator surface (submit holon, submit certification, host cell) is the
   only *required* node-side surface.
7. Sovereignty invariant holds at every layer: shared substrate carries opted-in public facts
   only; no private state, custody, or off-chain value crosses a node boundary; every
   cross-node input is signature/DNA-verified fail-closed.
8. The G-suite conformance manifest is published as a **node-operator standard** doc,
   separable from this implementation.

## Prerequisites (must exist before the gated build is real)

- **P0 — Gate-result serializer.** G1–G7 xUnit tests → signed machine-readable conformance
  manifest. Without this, "submit audit data for certification" has no source. (L0 dependency.)
- **P1 — Versioned `HolonType` vocabulary.** Replace the unversioned/fail-open/per-node type
  registry with a versioned contract shareable through the Commons; cross-node mismatch
  fail-closed. (L1 dependency.)
- **P2 — Node/agent identity binding.** Decide how the Holochain agent key relates to the
  node's existing key material and how it links to the L0 descriptor. (Identity dependency.)
- **P3 — Holochain client choice.** Resolve the client story (roll-our-own .NET / Rust
  binding / supervised conductor) for `AZOA.Commons`. (Substrate dependency.)

## Sequencing note

L0's **local half** (descriptor + conformance manifest + serializer, HTTPS-served) is
alpha-cheap and worth landing early — it is the foundation, the certification primitive, and
pays off standalone. Everything DHT-bound (`AZOA.Commons` repo, L0 publish, L1–L3) is
post-gate. Recommended order once the gate opens: stand up `AZOA.Commons` (DNA + client) →
L0 publish → L1 → L2(bootstrap) → L3, each a shippable increment; stop at any capability that
satisfies the then-current need.
