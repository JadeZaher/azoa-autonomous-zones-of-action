---
type: lane-checkpoint
round: 4
wave: W2
lane: L6
helper: verification-fixtures
branch: r4/verification-fixtures
base: beae9470
merged: pending (L6 lead merges)
---

# Round 4 · L6 helper `r4/verification-fixtures`

Scope: plan §4 L6 **Helper (opus)** — real-DB interleaving fixtures and WTT six-route protocol fixtures. Owned and
touched only: `tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/Round4Gates/Interleaving/**`,
`tests/AZOA.WebAPI.IntegrationTests/Fixtures/Wtt/**`, and this record. No Program.cs/config hunks.

## Delivered

### Interleaving fixtures (`Round4Gates/Interleaving`, design in its `AGENTS.md`)

| Test | Seam | Smoke (throwaway 3.1.4, RocksDB, :8048) |
|---|---|---|
| `SurrealSwapAdmissionTests.InterleavedAllowanceClaimNeverCrossesTheOldOperationsFirstSendOrCancellation` | allowance claim ∥ old-op authorize ∥ cancel, 3 rounds | pass |
| `SurrealSwapAdmissionTests.InterleavedFirstSendAndSubmissionWithdrawalOfAnExposedStageHaveOneWinner` | exposure: authorize ∥ withdrawal, 3 rounds | pass |
| `SurrealHoldCommandTests.InterleavedExchangeSourceConsumptionAndRevocationCommitOneLiabilityOrNone` ×2 | source consumption ∥ grant / API-key revocation | pass ×3 runs **only with the two harness fixes below applied locally** |
| `SurrealHoldCommandTests.InterleavedMandateFirstEgressAndRevocationNeverAuthorizeUnderARevokedMandate` | mandate first egress ∥ revocation | pass ×3 (same caveat) |
| `KycBeginInterleavingTests` (tenant) | 4× concurrent KYC begin | pass |
| `KycBeginInterleavingTests` (direct, no tenant) | 4× concurrent KYC begin | **FAIL — real product defect, see Needs other lane** |
| `OnRampLotInterleavingTests` ×2 | lot double claim; double draw of one version | pass |

Every fixture asserts exactly one winner and the loser's outcome by re-reading the winner; none weakens an
exactly-once assertion. Container reaped; the temporary endpoint/harness edits used for the smoke were reverted
(not committed).

### WTT six-route (`Fixtures/Wtt`, design in its `AGENTS.md`)

- `wormhole-pins.json`: upstream commit/tree, 51 git-blob-pinned sources (all fetched and verified), solc 0.8.4 /
  OZ 4.3.1 / Hardhat 2.26.3, Solana core + token bridge binaries pinned by sha256/length/slot (dumped read-only
  from mainnet and re-verified), pyteal toolchain + `python:3.10-slim` digest.
- `lib/upstream.mjs` (fetch + git-blob verification, loopback guard), `lib/guardian.mjs` (local guardian, VAA v1,
  RegisterChain + GuardianSetUpgrade payloads; signing/recovery self-checked), `lib/vaa.mjs` CLI.
- `evm/chain.mjs` — pinned core + token bridge compile (verified: compiles, BridgeImplementation 24 510 B) and
  Hardhat deploy/registration. `solana/dump-programs.mjs`, `solana/initialize.mjs`. `algorand/deploy.py`,
  `algorand/register.py` over upstream `PortalCore`. `run-wtt-fixtures.ps1 -Up/-Down` (parse-checked).
- `WttSixRouteProtocolTests` (trait `Gate=WttSixRoute`): pinned-protocol/guardian identity on all three chains,
  six-route emitter trust, six-route transfer acceptance. Fails closed when `.state/` is absent.
- Nothing executed against a chain (round-4 rule).

## Needs other lane

1. **L2 (identity) — CRITICAL product defect, every tenant-less KYC begin fails on 3.1.4.**
   `Providers/Stores/Surreal/SurrealKycStore.cs:199` and `:209`: `type::record($_selection_table, $_tenant_id)` with
   `$_tenant_id = ""` (and the profile lookup) is evaluated even though `WHERE $_has_tenant` is false; SurrealDB
   3.1.4 rejects it: `statement 2/13 returned ERR: Found  for the Record ID but this is not a valid id`. Fix: wrap
   both loads in `IF $_has_tenant { … } ELSE { [] }` (or skip adding the statements when `!hasTenant`). Gate:
   `KycBeginInterleavingTests(tenantScoped: False)`.
2. **Owner of `SurrealHoldCommandTests.cs` (L3 money-hold / L6 lead) — whole Hold store class cannot start.**
   `tests/AZOA.WebAPI.IntegrationTests/Persistence/Surreal/SurrealHoldCommandTests.cs:25` bootstraps golden
   `action_definition`, which no longer exists (now `action`). Same stale name in `SurrealNftLifecycleStoreTests.cs`.
3. **Same owner — exchange seed violates the schema.** `SurrealHoldCommandTests.Exchange.cs:185-186` creates a
   `User` without `PasswordHash` (`password_hash` ASSERT non-empty). Add `PasswordHash = "fixture-no-login"`.
   With 2+3 fixed locally, every Hold interleaving fixture passed three consecutive runs.
4. **L6 lead — CI.** `Gate=WttSixRoute` tests fail closed until the runner has brought the chains up; exclude the
   trait from the default integration job or add a job that runs `run-wtt-fixtures.ps1 -Up` first.
   `Fixtures/Wtt/package-lock.json` is force-added (root `.gitignore:182` ignores lockfiles).

## deferred.md candidates

- **WTT-SIX-ROUTE-SOLANA-REGISTRATION** — Solana RegisterChain needs the pinned `verify_signatures` (secp256k1
  instruction) + `post_vaa` + `register_chain` multi-transaction sequence against the loaded token bridge.
  Unblock: author it from the pinned SDK sources in `Fixtures/Wtt/solana/` (artifacts are already pinned; this is
  build work, not external). Gate: route tests with target Solana.
- **WTT-SIX-ROUTE-EXEC** — per-route transfer acceptance through `WttLifecycleService` with guardian-signed proofs
  over these deployments. Unblock: L5's WTT compile fixes (SigningContext null, Algorand `ApplicationId` parse,
  Solana init-only `Data`) merged, then one runner execution to validate the deploy scripts. Gate:
  `EveryDirectedRouteSettlesARealTransferExactlyOnce`.
- **WTT-FIXTURE-FIRST-RUN** — none of the deploy scripts has run (round-4 no-E2E rule); expected friction points:
  mainnet Solana binaries are newer than the pinned source (initialize layout), py-algorand-sdk 1.10.0b1 against
  current algod. Unblock: one `-Up` at the P2/integrated sweep.

## Self-review

- Transaction scope: fixtures target the serialization rows named in the round-3 review; no store code changed.
- Flow-tests: all tests drive public store seams with real SurrealDB; no mocks. Cut: a redundant "cancelled stage
  never sends" assertion (covered by `FirstSendRacingCancellationHasOnlyOneConsistentOutcome`) and a stale-version
  retry in the lot test (same branch as the race losers). Mutation probes were not run; both race branches are
  asserted, and swap fixtures loop three rounds.
- The Hold/swap fixtures are partial-class extensions (reuse of authoritative seeds); documented in the directory
  `AGENTS.md`.
- Build: `dotnet build azoa.sln -c Debug` 0 errors, 22 warnings (≤ 25), none in owned files. Secret scan: no hits.

## Inventory

New files only: `Round4Gates/Interleaving/{AGENTS.md, InterleavingDatabase.cs, KycBeginInterleavingTests.cs,
OnRampLotInterleavingTests.cs, SurrealHoldCommandTests.Interleaving.cs, SurrealSwapAdmissionTests.Interleaving.cs}`;
`Fixtures/Wtt/{AGENTS.md, .gitignore, package.json, package-lock.json, wormhole-pins.json, run-wtt-fixtures.ps1,
WttSixRouteProtocolTests.cs, lib/{upstream,verify-pins,guardian,vaa}.mjs, evm/{chain.mjs,hardhat.config.cjs},
solana/{dump-programs,initialize}.mjs, algorand/{deploy,register}.py}`.
