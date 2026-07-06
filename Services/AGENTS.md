# Services — directory notes

Rationale / cross-cutting notes for `Services/`. Code carries terse one-line
doc-comments; the "why" lives here.

## §bridge — CrossChainBridgeService + WormholeAdapter + Reconciliation

The cross-chain bridge is FUND-LOSS-CRITICAL. Its safety rests on an
**exactly-once** invariant composed from five independent mechanisms; a change
to any one must preserve the whole:

1. **Kill switch** (`BridgeOptions.RealValueEnabled`). Every real-value entry
   point — `InitiateBridgeAsync`, `RedeemWithVAAAsync`, `ReverseBridgeAsync` —
   refuses to move value when the flag is off UNLESS both chains resolve to the
   Simulated provider (`IsSimulatedRoute`, fail-closed on unknown chains).
2. **Avatar-scoped idempotency keys.** A client-supplied `Idempotency-Key` is
   namespaced by the authenticated avatar (`{avatarId:N}:{key}`) so two avatars
   sending the same key never collide on one claim. Absent a client key, a
   deterministic content key is derived (never random) so duplicates collapse.
3. **VAA replay ledger** (`ConsumedVaas`, UNIQUE-on-digest). The canonical
   digest is `SHA-256` over the base64-DECODED VAA bytes
   (`WormholeAdapter.ComputeVaaDigest`); malformed base64 is rejected BEFORE any
   claim/transition/on-chain call. Insert-before-redeem: a duplicate digest ⇒
   the VAA was already consumed ⇒ reject, never mint.
4. **Atomic status guards.** Every phase change is a conditional
   `TryTransitionBridgeStatusAsync(id, expected, next, …)` whose `WHERE
   Status==expected` predicate elects a single exclusive owner. The forward
   redeem lifecycle is `VAAReady → Redeeming → Completed/Failed`; the reversal
   lifecycle is the EXPLICIT `Completed → Reversing → Refunded/Failed` (reversal
   provenance is a state, never inferred from a `CompletedAt` stamp).
5. **Reconciliation** re-derives truth from chain confirmations. It only ever
   ADVANCES on a positive on-chain signal, FAILS on an explicit negative signal,
   and otherwise leaves the row untouched (flagging MANUAL INTERVENTION past the
   hard-stuck threshold). It NEVER auto-reverses funds and NEVER re-broadcasts.

### G2 doc/canonicalization notes (final-hardening-cutover)

- **Emitter canonicalization at the single seam.** `WormholeAdapter.NormalizeEmitterAddress`
  is the one place that produces the emitter written into the replay ledger. It
  canonicalizes to lowercase 64-hex (`^[0-9a-f]{64}$` — strips `0x`, lowercases,
  left-pads) so the `consumed_vaa_ledger` schema ASSERT never false-rejects a legit
  redeem over mere casing / prefix. `ParseVAA` independently emits lowercase-hex via
  `Encoding.ToLowerHex`. A test pins that `ParseVAA` output always matches the ASSERT.
- **Wormhole mode is NOT launch-ready.** `WormholeAdapter.DeriveSequenceFromTxHash` is a
  dev stub (`string.GetHashCode`, collision-prone) — real sequence parsing from source-chain
  Wormhole message logs is a documented follow-up. Keep `RealValueEnabled` OFF for Wormhole
  routes until real sequence derivation lands; the kill switch (mechanism 1 above) gates
  init/redeem/reverse closed by default (`RealValueEnabled=false`) so this stub cannot move
  real value today.

### B3 coverage-gap resolutions (final-hardening-cutover)

- **Locked no-op loop — closed.** A `Locked` row whose lock tx is chain-confirmed
  must NOT self-transition `Locked→Locked` (the mint/VAA step never ran); the
  sweep flags `StuckFlagged` for operator/foreground re-drive instead of spinning
  as a silent no-op (`ReconciliationService` G1 guard). A `Locked` row with a
  chain-FAILED lock advances `Locked→Failed`.
- **Invisible lock-ok/mint-fail — surfaced.** The trusted flow's "funds locked on
  source, mint failed on target" path stamps `Failed` with an explicit MANUAL
  INTERVENTION message, and `CheckLockedFundsAtRiskAsync` /
  `GetFailedBridgesWithLockedFundsAsync` (Failed AND lock_tx set AND mint_tx
  absent) re-surface these otherwise-terminal-invisible rows every sweep tick.
- **Config-driven network — no hardcoded Devnet in value paths.** Provider
  resolution uses a single config-driven `_network`
  (`Blockchain:DefaultNetwork`, Devnet fallback only). `CrossChainBridgeService`
  and `WormholeAdapter` BOTH resolve lock (source) and redeem (target) providers
  on that network — `WormholeAdapter` previously hardcoded `ChainNetwork.Devnet`
  and was fixed to mirror the service. The only remaining `Devnet` literal is the
  per-row fallback in `ReconciliationService.TryResolveProvider` for rows written
  before the `Network` column existed (greenfield: none in practice).

### Idempotency settlement across crash windows

When a process dies AFTER the on-chain effect but BEFORE
`CompleteAsync`/`FailAsync`, the idempotency record stays `InProgress` and would
poison every retry. Two mechanisms settle it without guessing: the foreground
`SettleIdempotencyToBridgeStateAsync` mirrors the row's ACTUAL terminal state
after a 0-row conditional update; reconciliation's `SettleBridgeIdempotency`
settles the record only once chain truth proves a terminal state, resolving the
key from the row's persisted `IdempotencyKey` column (never fabricated).

### Ambiguous crash windows fail CLOSED

If a consume-ledger row exists but the bridge is still `Redeeming` (or a reversal
is `Reversing` with no persisted burn hash), the outcome is genuinely ambiguous
(on-chain may or may not have landed). These are parked for reconciliation with a
"manual/operator resolution required" error — NEVER auto-completed or
auto-failed, because either guess could double-mint or strand funds.

### Tests

- `BridgeExactlyOnceReplaySweepTests` — integrated exactly-once proof: duplicate
  key / replayed VAA / crash-resume / kill switch / reconciliation-idempotence
  driven through the real service + sweep over ONE shared fake store.
- `BridgeRedeemRecoveryTests`, `BridgeReverseRecoveryTests` — per-branch
  redeem/reverse resume decision trees.
- `BridgeIdempotencyScopingTests`, `BridgeKillSwitchTests` — key namespacing +
  kill-switch matrix.
- `ReconciliationBridgeHardeningTests` — G1/G5/G6/Window-6 sweep hardening.
- `CrossChainBridgeServiceTests` — service-level happy/edge paths.
