---
type: review-notes
status: round 3 closed at source; runtime verification pending (integrated sweep after round 4)
---

# Round 3 closeout (2026-10-16)

Round 3 resumed the interrupted Codex session (checkpoint `3d08ace`) in Claude Code. Five
lanes delivered, each got an independent correctness review and (for value/auth paths) a
security review, and every lane went through bounded fix/re-review cycles (max two). Nothing
has been compiled or executed in this tree except lane V's harness smoke runs; the integrated
build/test/schema sweep runs once after round 4, per the owner's policy. Full per-item evidence
with file:line is in `round3-join-ledger.md`; this note is the summary.

## Lane outcomes

| Lane | Delivered | Verdict |
|---|---|---|
| N — NFT / credential lifecycle | replacement mint cost consent end-to-end; requester and participant renewal; tenant scoping; `NftEffectAuthorities` | Approved (cycle 1) + residual LOWs closed |
| A — WTT bridge / Solana | durable journal, lifecycle, recovery, guardian transport, three providers declaring WTT modules (`Enabled`-gated) | Approved with carry-forward (cycle 2) |
| B — Hold / exchange / reserve / fiat reversal | atomic exchange commit/settle/compensate, manager cutover, reversal recovery with in-batch caps, mandates | Approved with carry-forward (cycle 2) |
| C — DEX / Ethereum | swap submission, settlement, nonce-winner recovery, allowance cleanup, shared-allocator nonce reuse, operator settlement; legacy swap stack deleted; modules undeclared until activation | Approved with carry-forward (cycle 2) |
| V — verification harness | backend HTTP flows, MSW feature slices, Vitest browser mode, hermetic Playwright E2E, CI jobs | Approved (cycle 1) + closing LOWs |

## Owner decisions this round

- Continue the four-round loop as planned; checkpoint first (`3d08ace`).
- Add four test layers (HTTP flows, MSW slices, browser mode, Playwright E2E) via lane V.
- SDK client-side `DexAdapter`/Jupiter/Tinyman: **retire only** (round 4).
- WTT under a tenant credential delivers only to the user's own wallets; relayer fee must be zero.
  "Own wallet" = proven custody (`IKeyCustodyService.CanSignAsync`); client wallet creation is always External.
- SurrealForge: **patch the library** (1.2.1) rather than work around it in AZOA.

## Bug classes found and fixed across the tree

These were invisible to static review of the Codex output because every one fails *closed*
(the feature is dead, nothing leaks), which deny-path fixtures cannot detect:

1. **Fence binding names** — `SigningAuthorityFence.Sql()` rewrote variables to names that did
   not match their bindings; every signing-fenced batch would have failed. Fixed (one line).
2. **SurrealForge validator gaps** — `FOR`/closure variables reported as unbound; every KYC
   evidence write and two other batches would have thrown. Worked around in AZOA (3 sites, still
   valid SQL) and fixed at the root in SurrealForge 1.2.1.
3. **Reserved-word record ids** — `SurrealRecordParam.Of("user", …)` throws in SurrealForge 1.2.0
   (39 sites on `main`, latent since August). Fixed in SurrealForge 1.2.1; no AZOA workaround.
4. **Single-record reads captured as arrays** — `Assign(Key(…))` produced arrays that every scalar
   comparison rejected; 14 sites across Hold, NFT cost fences and webhooks. New
   `SurrealGeneratedStatementScope.AssignRecord`; all sites migrated.
5. **Cross-tenant scoping** — confirmed in NFT lifecycle and Hold settlement, fixed; WTT, DEX and
   exchange were clean.

Also fixed: the SDK/frontend/integration compile cascade inherited from `3d08ace`
(`ISurrealExecutor` usings, idempotency type ambiguity, `ExchangeAuthorityFence` raw-string braces,
missing Moq reference).

## Still owed (round 4 wave 1 seed)

From the ledger, in priority order:
- **Must-fix before real cards:** lane B's two clawback crash-path MEDIUMs.
- **Deploy-blocking:** remove the stale unique index `ethereum_preparation_nonce` on any kept
  database (Railway) — verify `surrealforge up` drops it or ship an explicit `REMOVE INDEX`.
- **Compile/runtime blockers:** D1 (fiat session attach, compile error), D2 (Stripe payment-intent
  webhook 500), D8 (Stripe idempotency key with changing `expires_at`), the SRDB0001 layer (~35
  interpolated `SurrealQuery.Of` sites), SurrealForge 1.2.1 bump once published.
- **Product defects:** D5, D6, D7, D9 (providers need an injected HttpClient); D4 downgraded to
  "confirm with vendor".
- **Owner-decided work:** SDK DEX adapter retirement; signature-verified External wallets.
- **Gates:** transaction-scope architecture test, cross-tenant architecture test,
  allow-path real-database fixtures for every fence, interleaving tests, and the retired-vocabulary
  guard arriving with the vocabulary branch.
- **Test debt:** integration tests mocking internal collaborators; round-4 slice/journey specs per
  lane on lane V's harness.
- The per-lane LOW carry-forwards listed in the ledger.

## Final-sweep prerequisites

Listed in order in `round3-join-ledger.md` §Final-sweep prerequisites: `dev-up -Reset` after the
vocabulary goldens land (also drops the stale nonce index), schema regeneration for every new POCO
field, SDK dist rebuild before the frontend typecheck, then the build/test/lint/typecheck/schema
gates plus lane V's HTTP, browser and E2E layers.
