---
type: retro
date: 2026-07-13
tracks:
  - node-operator-governance
status: active-follow-up
---

# Retro: direct NFT transfer must not bypass an inactive fee settlement path

## Finding

The direct NFT-transfer manager previously changed local ownership and created a
pending operation without reading the node Transfer fee schedule. Allocation
Transfer already rejected a positive fee because treasury settlement is inactive,
but the direct route could bypass that containment.

## Corrective boundary

The manager now loads the effective schedule before its first NFT read or write.
A nonzero flat amount or bps, an unavailable schedule, or a malformed schedule
rejects the route. This is intentionally a configuration-level gate: even a bps
entry that rounds to zero for a one-unit NFT remains configured economic policy
and cannot be silently ignored.

## Follow-up remains required

Do not enable the Transfer schedule entry because of this guard. Activation still
requires a version-pinned settlement claim, explicit fee-asset model, ownership
reservation/finalization, accepted-group persistence, chain reconciliation, and
an adapter that submits one verifiable atomic group.
