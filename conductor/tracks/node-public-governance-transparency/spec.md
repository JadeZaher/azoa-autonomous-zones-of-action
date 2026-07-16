---
type: spec
track: node-public-governance-transparency
created: 2026-07-11
status: in_progress
horizon: alpha
depends_on:
  - node-operator-governance
related:
  - node-conformance-manifest
  - node-egress-fees
---

# Track: public node-governance transparency

## Goal

Let anyone request the economic rules a node currently applies and a bounded,
privacy-safe history of changes without receiving `node:govern` authority.
Transparency is permanently free even when the node later enables paid egress.

## Contract

- `GET /api/node-transparency/current` exposes typed current governance
  allowlists, fee schedules, and configured chain/network treasury destinations.
- Separate anonymous audit endpoints expose newest-first governance, fee, and
  treasury pages through an exclusive `(occurred_at,id)` cursor.
- Public DTOs never forward raw stored JSON, actor avatar ids, internal record
  ids, secrets, exception detail, or private operator evidence. Actor continuity
  is omitted until a stable node-scoped HMAC key and privacy review exist.
- Opaque cursors are Data-Protection protected, purpose/version bound, size
  limited, and backed by an explicitly persistent/shared key ring outside local
  development.
- Weak ETags are semantic cache validators. `ContentSha256` is not an append-only
  history proof, and page/snapshot responses continue to report
  `CryptographicHistoryProofAvailable=false`.
- `GET /api/node-transparency/audit/checkpoint` is an opt-in bounded public
  history document. It chains only redacted typed audit projections in canonical
  ascending order and signs a checkpoint with the dedicated local node identity.
  Its protected prior checkpoint lives beside that identity rather than in
  SurrealDB; rewrites, truncation, and changed prefixes after a checkpoint fail
  closed. It is intentionally unavailable until node identity and checkpointing
  are configured.
- A signed local checkpoint is not a database-root solution: the first checkpoint
  remains a trust-on-first-observation boundary, history is bounded, and
  independent completeness/non-equivocation still require an externally retained
  or witnessed checkpoint in the conformance/federation work.
- Invalid cursors and unavailable storage return generic `no-store` responses;
  public endpoints suppress development exception detail while central logging
  still records the original exception.
- Anonymous rate limiting may trust forwarded client IPs only from configured
  proxies/networks or under a fail-fast edge-only deployment contract.
- Browser reads use a credential-free any-origin GET policy. Supplied bearer or
  API-key credentials are ignored before any identity-store lookup and cannot
  change the anonymous IP partition.

## Temporary raw-query waiver

`Providers/Stores/Surreal/SurrealNodeTransparencyStore.cs` retains two raw
SELECT variants for one descending `(occurred_at,id)` keyset pagination path.
Owner: `SurrealForge.Client` typed query surface. The reviewed remediation is tracked by
[AZOA code-style back-propagation](../azoa-code-style-backpropagation/spec.md),
and this waiver expires 2026-09-30. The current builder cannot express
`time < cursor OR (time == cursor AND id < cursor-id)` with a typed record-id
comparison.

## Acceptance criteria

1. Anonymous callers can retrieve all sanitized current/audit surfaces; every
   mutation remains JWT-only `node:govern`.
2. Redaction tests prove actor ids, record ids, raw JSON, secrets, and exception
   detail never appear, including Development error responses.
3. Cursor tamper, cross-purpose, cross-instance, oversize, restart-key-ring, and
   equal-timestamp tie cases are covered; pages have no duplicates or omissions.
4. ETag `*`, weak semantic matching, 304 headers, and `no-store` error behavior
   are covered.
5. Direct/self-hosted deployments cannot spoof `X-Forwarded-For` to rotate rate
   limiter partitions, and invalid API-key headers cannot create pre-limit
   key-store reads on this public route.
6. The raw waiver is removed or re-reviewed in the linked package-remediation
   track before expiry. Integrated build, unit, and live-Surreal tests pass
   before archive.
7. Checkpoint tests prove deterministic ordering, signature verification,
   redaction, exact-prefix extension, and fail-closed detection of a later
   history rewrite.

## Non-goals

- Exposing raw operator identity or internal audit rows.
- Claiming cryptographic completeness from a self-signed current digest.
- Charging for transparency, health, conformance, quoting, or payment proof.
