---
type: retro
created: 2026-07-11
related_tracks:
  - azoa-code-style-backpropagation
  - node-operator-governance
  - node-public-governance-transparency
  - node-egress-fees
---

# Retro: code-review standards must become primitives and gates

## Trigger

Review of the node-governance reliability slice found correct conditional
semantics implemented with long raw SurrealQL strings, repeated catch/convert
blocks, undocumented interface methods, local result/link helpers, application
rationale mixed into generic-package guidance, and no public transparency path
for the governance evidence being collected.

## Decisions

- Fix missing expressiveness in SurrealForge first; do not create an AZOA-only
  query-builder fork.
- Prefer typed CRUD and conditional mutation. Keep raw SQL only where it
  preserves an atomic/multi-schema invariant the package cannot yet express.
- Expected outcomes remain typed results; unexpected exceptions are observed at
  one centralized boundary instead of being repeatedly caught and flattened.
- Interfaces own contracts and implementations inherit them. Shared helpers move
  to the generic package or an explicit domain helper.
- Every turn includes changed-file pruning, backed by a ratcheting architecture
  debt budget rather than prose alone.
- Generic SurrealForge documentation remains datastore-generic. AZOA blockchain
  and governance rationale belongs in AZOA directory docs and conductor tracks.
- Governance evidence gets a separate, sanitized public transparency surface.
  Optional paid egress never paywalls that surface or meters Holochain gossip.

## Follow-through

The active tracks are `azoa-code-style-backpropagation` for the repository-wide
mechanical migration, `node-public-governance-transparency` for the implementable
public surface, and deferred `node-egress-fees` for the separately gated economic
feature. No track may be archived on a narrow passing test; its own acceptance
criteria define completion.

## Secondary critique findings

The independent review found that style defects were masking correctness and
operations defects, not merely readability debt:

- saga compensation and forward continuation each had split-write crash
  windows, and status transitions did not prove ownership of the current lease;
- governance compare-and-set was initially enforced only by a manager pre-read,
  allowing two writers to publish the same next audit version;
- the first typed mutation API draft was mutable and accepted ambiguous nulls,
  required-field removal, mismatched record prefixes, and ignored explicit
  column names;
- the diagnostic sink could drop entries, emit invalid truncated JSON, expose
  exception messages on spans, and introduced a second severity control;
- trust-all forwarded headers and credential-bearing public requests could
  undermine anonymous rate partitions or perform identity-store reads before
  limiting; and
- public audit cursors, weak ETags, error caching, and treasury-history
  validation needed restart, equal-timestamp, wildcard, and previous-snapshot
  tests.

Each finding became either a tested invariant in this change or an explicit
active-track blocker. The provider multi-network singleton alias remains
fail-closed in `node-operator-governance`; it is not presented as repaired.
