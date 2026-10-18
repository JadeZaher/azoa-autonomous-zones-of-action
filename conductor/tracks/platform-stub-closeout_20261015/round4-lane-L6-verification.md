---
merged: ab6c600d
type: lane-checkpoint
round: 4
wave: W2
lane: L6
branch: r4/verification
merge: 8 (merges last; L7 swapped ahead)
helper: r4/verification-fixtures (merged into this branch, record round4-lane-verification-fixtures.md)
---

# Round 4 · L6 verification (`r4/verification`)

Scope: plan §4 L6 lead bullet, the W2 brief, the coordinator's in-flight additions (fixtures-helper harness items,
L2/L3/L4/L5/L7 asks, RV-L1-02/04/07, RV-L4 RETURN rule, RV-X X-9). Base merged up to `de43c00d`.

## Stop-the-line findings (for the orchestrator / W3)

1. **CRITICAL — the API cannot start: DI recursion deadlocks host start.** `ActionManager` →
   `IEnumerable<IActionExternalOperationResolver>` → `DeploymentActionResolver` → `IDeploymentService` →
   `DeploymentService(IFrozenActionRuntime)` → `Program.cs:1419` factory `sp => sp.GetRequiredService<ActionManager>()`
   → `ActionManager` … (439 nested `Program.cs:1419` frames in the hang dump). `ValidateOnBuild` cannot see through
   factory lambdas; `ValueRailStartup.GuardAccountPauseWired` (`Program.cs:1940`) resolves `IActionManager` during
   startup, DI's stack guard moves the recursion to another thread, which blocks on the lock the first thread holds:
   a silent hang, not an exception. Every `WebApplicationFactory` test (all HTTP flows, `IntegrationTestBase`
   classes) and the real process hang. Pinned by `Hosting/ProgramCompositionTests` (bounded 90 s → red with a pointer,
   `Hosting/AGENTS.md`). Owner L4 (actions/deployments): break the cycle (e.g. `DeploymentService` takes
   `Lazy<IFrozenActionRuntime>`/resolves per call, or the resolver stops depending on the service). The link dates
   from `3d08ace` (round 3); nothing ran a host until now.
2. **HIGH — new fail-closed class (re-plan trigger size): SurrealForge 1.3.0 `UpdateOnly`/`DeleteOnly.Build()` throws
   without a `Where`.** 9 production sites (L4 ×6: `SurrealActionLocalMutationStore.cs:51,59`,
   `SurrealActionSigningAuthorizationStore.cs:85,87`, `SurrealHolonStore.cs:157`, `SurrealWalletStore.cs:69`; L3
   `SurrealFungibleCreationStore.cs:50`; L1 `SurrealGovernanceStore.Finalization.cs:52`; L5
   `SurrealNftLifecycleStore.cs:279`) fail every call that reaches them (local mutations, NFT finalize, governance
   finalization, fungible attach, signing authorization, holon/wallet deletes). Fixed here only in the unowned
   `SurrealNodeGovernanceStore.cs:58,61` (`.Where(row => row.Version == parameters.Version)`, the version the same
   batch just upserted). 178 test-seed sites were swept (`.Where(seed => true)`). Rule:
   `SurrealQlSourceRulesTests.Typed_conditional_mutations_always_carry_a_Where`; residue ratcheted with owners.
3. **HIGH — `FiatReversalEventHandler.cs:240` `LotId = lot?.Id`** writes the stripped `[Id]` (bare text) into
   `record<on_ramp_lot>`: every reversal of a credited lot fails to record. (The sweep's "masked only" note covered
   the link-typed copies, not this one.) Owner L3.
4. **MEDIUM — 1.3.0 "required field cannot be unset"** in `SurrealActionLocalMutationStore.cs:55` (`parent_holon_id`)
   and **"Combine requires at least two queries"** in `SurrealFungibleCreationStore.cs:179` — both throw on their
   allow paths (seen in `SurrealActionNftHolonLinkTests`, `SurrealFungibleProjectionTests.SubmittedRecovery*`).
   Owners L4, L3.
5. **MEDIUM — liability read widening or test drift:** `HoldLiabilityReadService.ListAsync(actor, null)` returns
   tenant-held rows where the actor is beneficiary (`SurrealHoldCommandStore.Liabilities.cs` null branch + `Visible`);
   `SurrealHoldCommandTests.LiabilityReadsShowOwnReceivedAndCustodyLiabilitiesAndHideEveryoneElses` expects them
   hidden. Owner L3 helper decides which is the rule.
6. **LOW — `DEFINE PARAM IF NOT EXISTS`** for `[Inside]` lists never updates an existing database's list; harmless
   under D-9 (fresh DB), but a later `[Inside]` edit needs an `OVERWRITE` migration (now enforced by rule).

## Scope closed

| Item | Where |
|---|---|
| Hardhat 2.26.3 replaces Ganache (package + lockfile, ethers 6.17.0) | `contracts/ethereum/package.json`, `scripts/hardhat-network.mjs` (random per-process keys via deleted env var, fresh genesis per `localChain()`), `scripts/hardhat.local.config.cjs`; protocol fixtures `Fixtures/Ethereum/{hardhat-chain,chain,dex-chain}.mjs`; contract tests + `deploy-local.mjs` migrated (12/12 pass on Hardhat); EIP-1898 `{blockHash, requireCanonical}` and `finalized` probed against both fixtures; WETH9 digest over LF bytes (Windows autocrlf) |
| Uniswap lockfile audit | `contracts/ethereum/scripts/AGENTS.md` §Lockfile audit (prod tree `npm audit --omit=dev` 0; Hardhat-2 dev advisories recorded) |
| Contracts in CI | `ci.yml` dotnet job: `npm ci && build && test` in `contracts/ethereum` before integration tests |
| Tinyman LocalNet pins | `Fixtures/Tinyman/validator-pin.json` (mainnet 1002541853 @ 66078979, testnet 148607000 @ 68364065, byte-identical; sha256 approval `dd63834d…`, clear `18ae2b5f…`), `deploy-localnet.mjs` (goal raw create, re-verify, prints `Dex:Tinyman:Networks:Devnet:*`) |
| Jupiter/Whirlpool clone | `Fixtures/Solana/jupiter-whirlpool-pin.json` (program executable sha256 + deploy slot, SOL/USDC pool identity), `start-jupiter-validator.mjs` (`--clone-upgradeable-program` ×2, pool accounts, tick arrays derived at clone time via `find_program_address`, verified against mainnet; `--dry-run`) |
| Enum round-trip for every enum column | `Round4Gates/EnumStorage/EnumColumnRoundTripTests` (35 columns × members green; `[Inside]` = enum names, reviewed sentinel exclusion) |
| Captured-null negatives | `Round4Gates/CapturedNull/CapturedNullGuardTests` (KYC ×2, mandate owner list, fungible participant list; green); Hold sites by the money-hold helper |
| THROW-marker + duplicate per store family | `Round4Gates/Inventory/StoreFamilyGuardInventoryTests` (42 families: named cases, or a gap with its owner) |
| Allow path for every `*Fence.cs` | `Round4Gates/Inventory/FenceAllowPathInventoryTests` (21 fences → named success fixtures; 26/29 green, red ones are findings 2 and an L4 seed, below) |
| `SurrealQlSourceRulesTests` | `Architecture/{CSharpSource,SurrealQlSourceRules,SurrealQlSourceRulesTests}.cs`: `;` in `Of`; non-literal `Of` outside the 3 helpers; SRDB0001 suppression; `Assign(Key)`; ordinal casts; end-relative index (robustness, corrected rationale); RETURN-then-COMMIT only; `UpdateOnly/DeleteOnly` need `Where`; bare id into `[References]` (initializers + typed `Set`, compiler-faithful type resolution, normalizing-store proof); forward migrations use `OVERWRITE`; tenant payout writes only via the ledger. 41 tests, every rule mutation-pinned; reviewed W3 ratchet |
| `TransactionScopeGateTests` | `Architecture/TransactionScopeGateTests.cs`: BEGIN reaches COMMIT (call-graph fixpoint), THROW guard per deciding family, egress only via the operation-row committer, `// lock:` ratchet (13 files) |
| `// lock:` on stores with no W2 owner | `SurrealNodeGovernanceStore.cs`, `SurrealNodeTreasuryStore.cs`, `SurrealAdminBootstrapStateStore.cs` (×2), `WalletAuthIdentityMigrationService.cs` |
| Mock debt | `SurrealFungibleProjectionTests` (real `SurrealUserStore`/`SurrealWalletStore`, value access refuses loudly; chain edge still faked), `SurrealActionNftHolonLinkTests` (real `NodeGovernanceGuard.Unrestricted`, delegating `ChangeBeforeCommit` interleaver instead of a Moq store) |
| P2 rehearsal (rescoped per D-9) | `Fixtures/UpgradeRehearsal/{rehearse.ps1,AGENTS.md}` + `Round4Gates/DataCompatibility/GeneratedSchemaDataCompatibilityTests` (127 writable tables accept/read/re-write a model-valid row; every declared UNIQUE index exists; green) |
| gitleaks allowlist | `.gitleaks.toml` (exact reviewed values; one deleted file; one pin manifest), `--config` in both CI passes; `.github/AGENTS.md` §gitleaks; full history + tree: no leaks |
| Gate tests load every lane's inventory | already true (`GateInventory.cs` globs `Inventories/*/*.json`); remaining reds are inventory content (fixer `r4/fix-security` owns) |
| CI-evidence failures in owned paths | perf budgets green on 3.1.4 (root cause was W0's missing goldens); harness: `action_definition`→`action` golden, User seeds (`PasswordHash`/`Email`), bare-id seeds, unique key hash, `Set(null)`→`Unset`, 178 `Where` seeds; `Gate!=WttSixRoute` excluded from the default job; integration step `--blame-hang-timeout 10m` + dump artifact; npm audit split (prod `low`, tree `critical`, GHSA-vfj7) |
| E2E webhook receiver | `frontend/e2e/support/webhook-receiver/receiver.mjs` + compose service on internal `e2e_webhooks` (198.51.99.0/24, receiver `.10`, leaf cert/key only), SAN added, `FakeVendors.webhookDeliveries()`, stale "delivery is off" comments fixed (RV-L1-07) |
| Identomat `/external-api/delete/` (L2) | `Http/Infrastructure/IdentomatVendor.cs` (+ `DeleteRequests`), `frontend/e2e/support/fake-vendors/identomat.ts` |
| Money-page slices (L7) | `frontend/src/test/slices/render-slice.tsx` `renderSliceWithAuth`, `msw/handlers/{session,tenant-payout}.ts`, `payout-balance.slice.test.tsx` (2 green) |
| D-8 ledger fixtures + rule (L3) | `Round4Gates/TenantPayout/TenantPayoutLedgerRaceTests` (4 green) + `Tenant_payout_rows_change_only_through_the_ledger_commit` |
| Client-signing CAS race (L5) | `Round4Gates/ClientSigning/ClientSigningTransitionRaceTests` (2 green) |
| RV-L1-02 | `ci.yml` "Upgrade from main converges (or a generation reset is declared)", `Persistence/SurrealDb/SCHEMA_GENERATION` (= 2), `.github/scripts/schema_snapshot_diff.py`, OVERWRITE rule; `.github/AGENTS.md` §schema-upgrade |
| RV-L1-04 | `scripts/round4/merge-check.mjs`: `AddOnRampValueRateSource` in the order; KYC-gate registration forms (`typeof`, `ServiceDescriptor`, `new ServiceDescriptor`, `Replace`, `TryAdd*`) across every tracked production file (mutation-checked) |
| RV-X X-9 | `Hosting/ProgramCompositionTests.Round_four_recovery_workers_are_registered_and_resolve` + bounded host start + Development secrets via `UseSetting` |
| Fixtures helper | merged (`5aef520d`); its tenant-less KYC finding re-run on the merged base: green |

## Open (not done; not deferred — build work)

- **HTTP flow with a real child JWT (L4 ask) and wallet-signature ownership flow (L4 ask):** blocked by finding 1 —
  no `WebApplicationFactory` host starts. Author once the cycle is broken (a factory variant without
  `TestAuthHandler`, real `JwtBearerEvents`).
- **LOW 5 NFT real-DB renewal/participant-lapse fixtures (L5 ask).**
- **WTT six-route (helper):** WTT-SIX-ROUTE-SOLANA-REGISTRATION, -EXEC, -FIRST-RUN are build work (helper record).
- **Store-family gaps:** the `Gap` entries in `StoreFamilyGuardInventoryTests` (ActionRun, ActionWebhookDelivery
  throw, AdminBootstrapState, EthereumPreparation throw, Holon throw, HostedSession duplicate, KYB, KycControl,
  KycIdentity, KycScreening duplicate, NodeFeeSchedule, NodeGovernance duplicate, Saga throw, User, Wallet).
- Integration suite reds seen against a throwaway 3.1.4 (memory engine) and owned elsewhere:
  `SurrealDeploymentAuthorityTests` seeds bare ids into `record<action>`/`record<user>` (fixing the links exposes an
  NRE in the test's authority shape; L4), plus findings 2–5.

## Needs other lane

- **L4:** finding 1 (DI cycle); finding 2 sites ×6; finding 4 `parent_holon_id`; `SurrealDeploymentAuthorityTests`
  seeds; ratchet entries `no-where` ×4 files.
- **L3:** finding 3 (`FiatReversalEventHandler.cs:240`); `SurrealFungibleCreationStore.cs:50` (no `Where`) and
  `:179` (`Combine` with one query); `OnRampLotLookup.cs:126` (pass the const); finding 5.
- **L1:** `SurrealGovernanceStore.Finalization.cs:52` (no `Where`); `Mcp/Tools/VectorSearchTool.cs:127` (inline the
  ternary of consts); `Helpers/HostedSessionAliasFence.cs:29,37` (`.Replace` on SQL text → `SurrealIdentifierTemplate`).
- **L5:** `SurrealNftLifecycleStore.cs:279` (no `Where`).
- **L7:** `frontend/e2e/journeys/webhook-registration.journey.ts` can now assert a signed delivery via
  `vendors.webhookDeliveries()`.

## Hunks

```hunk path=docker-compose.e2e.yml
  webhook-receiver:
    image: node:20-alpine
    container_name: azoa-e2e-webhook-receiver
    command: ["node", "/receiver/receiver.mjs"]
```

```hunk path=docker-compose.e2e.yml
  e2e_webhooks:
    internal: true
    ipam:
      config:
        - subnet: 198.51.99.0/24
```

```hunk path=.github/workflows/ci.yml
              --config /repo/.gitleaks.toml \
```

```hunk path=.github/workflows/ci.yml
      - name: Upgrade from main converges (or a generation reset is declared)
```

No `Program.cs` or `appsettings*.json` change. POCO deltas: none. Published contracts: none (no routes).
Inventories: none (L6 owns no routes or stores).

## Compile and checks

- `dotnet build azoa.sln -c Debug`: 0 errors, 17 warnings (≤ 25; none in L6 files).
- SDK `npm run build` OK; frontend `npm run typecheck` clean; touched-file eslint clean (one pre-existing warning in
  `e2e/support/fixtures.ts`).
- Run by filter (own/new tests, throwaway `surrealdb:v3.1.4` on :8047, reaped): source rules + transaction gate
  61/61; enum 37/37; data compatibility 130/130; captured null 3/3; tenant payout 4/4; client signing 2/2;
  interleaving (helper) green on merged base; Hold/Governance/Webhook/Hosted/Fungible classes after the seed sweep
  158/168 (reds = findings 2–5); perf 3/3; contract tests 12/12; slices 4/4.
- `node scripts/round4/merge-check.mjs`: ok. `node scripts/round4/secret-scan.mjs`: no hits. gitleaks history + tree
  with `.gitleaks.toml`: no leaks.

## Checklists

- **Transaction scope:** only production change is `SurrealNodeGovernanceStore` (`Where` on the version the same
  batch upserted — same statements, same offsets) and `// lock:` comments.
- **§7 classes:** every class now has a mechanical rule or real-DB fixture (array captures and binding names are
  covered by the store fixtures; strict validator by the existing SurrealForge validation).
- **Flow-tests:** real stores, real SurrealDB, observable rows; fakes only at chain/vendor edges. Cut: none of mine
  are interaction asserts; inventories assert existence + staleness (their reds are deliberate).
- **Security:** receiver mounts only leaf cert/key; gitleaks entries are exact values; no secrets printed.
- **Pruning gate:** no unused usings in touched app files; directory `AGENTS.md` sections added for every new area.
