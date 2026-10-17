---
type: lane-checkpoint
lane: L1-core
wave: W0
branch: r4/core
status: merged
merged: 4d665ec
---

# Round 4 — L1 core (W0 `r4/core`)

Base `feat/platform-stub-closeout` @ `a6853f9`. Prelude `e075b6d`. Helpers merged: A `r4/core-chains`
@ `7b23166` → merge `ba593e6`; B `r4/core-fiat` @ `4cf7e91` → merge `37dc3ea`. Helper records:
`round4-lane-L1-helperA-chains.md`, `round4-lane-L1-helperB-fiat.md`. Closing vocabulary: everything
below is **Built** (compiled) at most; no test, lint gate, container or schema golden was run (D-2).

## Scope closed

| Item | Where |
|---|---|
| SurrealForge 1.3.0 from nuget.org (no local source, no cached 1.3.0 existed) | `AZOA.WebAPI.csproj:113-117`, `tests/AZOA.WebAPI.IntegrationTests/AZOA.WebAPI.IntegrationTests.csproj:30`, `tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj:45` |
| `Helpers.Encoding` → `ByteEncoding`; "never shadow BCL names" | `Helpers/ByteEncoding.cs:4`, `Helpers/AGENTS.md:25` (§BCL names) |
| `using AlgorandEncoder = Algorand.Utils.Encoder;` ×5 files / 17 sites | `Providers/Blockchain/Algorand/AlgorandProvider.{Exchange,ExchangeValidation,Fungible,ValueAcceptance,ValueTransfer}.cs` |
| `SurrealIdentifierTemplate` (A-1) | `Core/Surreal/SurrealIdentifierTemplate.cs`, `Core/Surreal/AGENTS.md:168` (§identifier-template) |
| `scripts/round4/secret-scan.mjs` | copied, plus: `.env` files are also read from the main worktree (lane worktrees have none) and an optional diff-args argument (default `--cached`) |
| `.Build()` ×17 | `SurrealEthereumPreparationStore.cs:94`, `SurrealHostedProviderAttemptStore.cs:35,77,89,129,137,146`, `SurrealUserStore.cs:89,153,289,302,314`, `SurrealWalletAuthChallengeStore.cs:66,113`, `Services/Auth/WalletAuthIdentityMigrationService.cs:48,123,141` |
| `using SurrealForge.Client` | `HoldReleaseWorkflowFence.cs:7`, `NftWorkflowCostAuthorityFence.cs:6`, `Services/Governance/ConsumerGovernanceService.cs:15` |
| `SurrealHoldCommandStore.Mandate.cs:34` | `using AZOA.WebAPI.Core.Surreal;`; the raw `IF … THROW` beside it → `RequireResult` (byte-identical SQL) `:39` |
| `RetryOnConflictAsync<T>` inference | lambda returns the response: `SurrealWebhookRegistrationStore.Management.cs:120`, `.Withdrawal.cs:51` |
| `LeasedWebhookSender.cs:38` | closed by the `ByteEncoding` rename (was a CS0104 cascade) |
| `ActionManager.cs:1578` | `using System.Text.Json;` `Managers/ActionManager.cs:1` |
| Vocabulary drift | `Helpers/ActionSigningScopes.cs:4` (`using AZOA.WebAPI.Services.Actions;`), `Services/Deployments/DeploymentArtifactCompiler.cs:53` (`Gate` → `GateCheck`; config key stays `predicate`), `Helpers/NftLifecycleOperationProjection.cs:32` (see §For L5) |
| SRDB0001 (27 build sites) | template: `DeploymentAuthorityFence.cs` ×4, `SurrealActionWorkflowAdmissionStore.cs` ×4, `SurrealActionLocalMutationStore.Witnesses.cs` ×4, `SurrealGovernanceStore.Finalization.cs` ×1, `WalletAuthIdentityMigrationService.cs` ×2 (`AssertLease`/`AssertUser`), `ExchangeAuthorityFence.cs` (pre-existing per-site pragma removed). Generated-statement captures `LET $v = (<sql>)` → `SurrealGeneratedStatementScope.Assign`: `SurrealDeploymentRecoveryAuditStore.cs` ×2, `SurrealDeploymentStore.RecoveryWithdrawal.cs` ×2, `.ReplacementRecovery.cs` ×2 (+`RequireResult`), `SurrealSwapOperationStore.Stages.cs` ×2, `.RentRelease.cs` ×1, `SurrealNodeFeeScheduleStore.cs` ×1, `WalletAuthIdentityMigrationService.ExactMutation`. `SurrealHostedSessionStore.cs:375` closed by helper B. Remaining suppressions: `SurrealGeneratedStatementScope.cs` (4, pre-existing) + the one in the template |
| Ordinal enum casts → name mapping | `SurrealActionRunStore.cs:55,58,91,117,131` via `ToKind`/`FromKind` (`Enum.Parse(name)`, throws on drift); both enums verified member-identical |
| Converter-less enums | `[JsonConverter(typeof(JsonStringEnumConverter))]` `NodeFeeSettlement.cs:112,118,124`, `NodeFeeAtomicGroup.cs:74`; `tests/AZOA.WebAPI.Tests/Architecture/EnumStorageConverterTests.cs` + `Architecture/AGENTS.md` §enum-storage |
| F-H13 | `SurrealFungibleCreationStore.cs:272-274` — literal containing `;` → `$_fungible_pending_detail` parameter |
| DI | `Program.cs:1160` → `WalletSignatureVerifier`; fungible creation store/service/action resolver/recovery worker `Program.cs:1351-1359` (inside the fungible block) |
| §3.4 rule | root `AGENTS.md:56` §Program.cs registration order |
| Host tests (authored, not run) | `tests/AZOA.WebAPI.IntegrationTests/Hosting/ProgramCompositionTests.cs` — Development host with `ValidateOnBuild`+`ValidateScopes`; `IRealValueKycGate` is `RiskHoldValueGate` and the displaced inner gate is not; `IWormholeAdapter`, `IFungibleCreationStore`, `IFungibleCreationService` resolve; exactly one `FungibleCreationRecoveryWorker` hosted service |
| `scripts/round4/merge-check.mjs` | as §3.3; verified passing on this tree and failing on a duplicated KYC-gate registration |
| `TestDataBuilders` | `public static partial class TestDataBuilders` + `KybApprovedTenantBuilder` (APPROVED, fresh, current `IdentomatKybProvider.AuthorityRevision`, one beneficiary so `Verified` projects true) written through `IKybVerificationStore.TryStartAsync(null, …)`; refuses a tenant that already has a row |
| SDK/frontend compile | SDK: `FiatPayoutView` interface (`sdk/azoa-wallet/src/api/client.ts`, mirrors the backend record), `ActionRunResult` exported from both indexes, `ActionRecovery*` from the root index. The plan's "stale swap re-exports `src/api/index.ts:112-114`" no longer exist (tsc clean). Frontend: `tsconfig.json` target `es5` → `ES2020` (BigInt literals, Set/URLSearchParams iteration), `run-panel.tsx` typed fallback `Result`, `hold-recovery-panel.tsx` explicit `Cancel`/`Replace` narrowing + `unknownCommand()` |
| IDE0005 made real (orchestrator item) | `GenerateDocumentationFile=true` + `NoWarn CS1591;CS1573;CS1572;CS1734;CS0419;CS1574;CS1570` (`AZOA.WebAPI.csproj:14-17`); 743 unused usings removed in 545 files — deletions only, from the build's own IDE0005 locations (iterated to 0; `dotnet format` was rejected because it also re-sorted usings) |
| Nullable warnings in round-3 code | 44 app + 9 test warnings cleared with null-forgiving at already-guarded sites; one behaviour change: `SurrealHoldCommandStore.ExchangeAbsence.cs:61`/`.ExchangeSettlement.cs:28` refuse (`null`) when `opening.Definition` is null instead of throwing |
| `InternalsVisibleTo` | `AZOA.WebAPI.csproj` → `AZOA.WebAPI.Tests`, `AZOA.WebAPI.IntegrationTests` (round-3 tests read internal wire types) |

## Hunks

```hunk path=Program.cs
                    .Select(metadata => metadata.Policy)
                    .OfType<string>()
                    .Where(policy => !string.IsNullOrWhiteSpace(policy))
```

```hunk path=Program.cs
builder.Services.AddSingleton<AZOA.WebAPI.Interfaces.IWalletSignatureVerifier,
    AZOA.WebAPI.Services.Signing.WalletSignatureVerifier>();
```

```hunk path=Program.cs
// Durable fungible creation lifecycle (store, service, action resolver, recovery worker); see Services/Fungible.
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Stores.IFungibleCreationStore,
    AZOA.WebAPI.Providers.Stores.Surreal.SurrealFungibleCreationStore>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Managers.IFungibleCreationService,
    AZOA.WebAPI.Services.Fungible.FungibleCreationService>();
builder.Services.AddScoped<AZOA.WebAPI.Services.Actions.ActionFungibleCreationService>();
builder.Services.AddScoped<AZOA.WebAPI.Interfaces.Actions.IActionExternalOperationResolver>(sp =>
    sp.GetRequiredService<AZOA.WebAPI.Services.Actions.ActionFungibleCreationService>());
builder.Services.AddHostedService<AZOA.WebAPI.Services.Fungible.FungibleCreationRecoveryWorker>();
```

```hunk path=AZOA.WebAPI.csproj
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);CS1591;CS1573;CS1572;CS1734;CS0419;CS1574;CS1570</NoWarn>
```

```hunk path=AZOA.WebAPI.csproj
    <PackageReference Include="SurrealForge.Client" Version="1.3.0" />
    <PackageReference Include="SurrealForge.Schema" Version="1.3.0" />
```

```hunk path=AZOA.WebAPI.csproj
    <InternalsVisibleTo Include="AZOA.WebAPI.Tests" />
    <InternalsVisibleTo Include="AZOA.WebAPI.IntegrationTests" />
```

```hunk path=frontend/tsconfig.json
    "target": "ES2020",
```

Also edited in `Program.cs` (not order-sensitive, no hunk needed): `principal!.FindFirst(…OperatorRevision)` null-forgiving at `:244`, and four unused usings removed.

## POCO deltas

- `NodeFeeSettlement.State/PrimaryEffectState/FeeEffectState`, `NodeFeeAtomicGroup.State`: string enum converter only. Stored text is unchanged (`[Inside]` names); no schema field change, no regeneration needed for this. Helper POCO deltas: see the helper records.

## Contracts

- No new routes. SDK type `FiatPayoutView` (camelCase mirror of `Services/Fiat/OffRamp/FiatPayoutOperatorController.cs:13`).
- `SurrealIdentifierTemplate` / `SurrealTemplateToken` / `SurrealTemplateFragment` (Core/Surreal) for any lane that must vary SurrealQL *text*; captures of typed statements use `SurrealGeneratedStatementScope.Assign`.
- `TestDataBuilders.KybApprovedTenant(tenantId).SeedAsync(services)` for L2–L6 fixtures.

## Deletions

- `tests/AZOA.WebAPI.Tests/Actions/Handlers/CredentialRevokeNodeHandlerTests.cs` — removed the second class `CredentialRevokeAuthorizationTests` (≈650 lines). Premise gone: it constructed the pre-round-3 `CredentialManager(ICredentialIssuerStore, IWalletStore, factory, IBlockchainOperationStore, idempotency, IValueAccessService)` that clawed back directly through `IAlgorandASAModule`; `CredentialManager` is now `(INftLifecycleStore, INftLifecycleService, IBlockchainProviderFactory)` and revocation runs through the NFT lifecycle. Issuer-only / holder-refused coverage lives in `tests/AZOA.WebAPI.Tests/Credentials/IssuedCredentialRevocationTests.cs` (holder refused, issuer allowed, self-issued refused, key reuse). Not re-covered there and worth a lifecycle fixture (L5): unrecorded issuer, non-soulbound asset, missing holder address, issuer without a wallet on the chain, lookup error fails closed.
- Generated artifacts reverted, not committed: `frontend/next-env.d.ts`, `frontend/src/test/msw/public/mockServiceWorker.js` (rewritten by `npm ci`/`next typegen`).

## Compile result (D-2)

- `dotnet build azoa.sln -c Debug --no-incremental` → **0 errors, 0 warnings** (app, unit, integration, live-tests projects). Warning budget was 25; the app project went 68 → 0 and test projects 9 → 0.
- `sdk/azoa-wallet`: `tsc --noEmit` clean, `npm run build` OK. `frontend`: `npm run typecheck` clean.
- `node scripts/round4/merge-check.mjs` → ok. `node scripts/round4/secret-scan.mjs` → no hits.

## Checklists

**Transaction scope (`round3-transaction-scope-review.md`) on every changed store/fence.** No batch gained or lost a statement, guard or THROW; result offsets unchanged (template and `Assign` rewrites render byte-identical SQL — verified by construction: same literals, same tokens, same `" AND "` joins; `Assign(stmt, s, v)` ≡ the old `"LET $v = (" + Apply(stmt, s).Sql + ")"`). Exception: `WalletAuthIdentityMigrationService.ExactMutation` now scopes the typed mutation's parameters (`$identity__t`…) — renamed consistently, same statement, and `Apply` additionally rejects any unbound variable. Retry wrappers unchanged in placement. `SurrealActionRunStore` writes the same names as before for every member.

**SRDB0001 rewrites inside authority fences** (`DeploymentAuthorityFence`, `ExchangeAuthorityFence`, action admission, local-mutation witnesses, governance finalization): binding names unchanged; every token domain checked against `^[a-z0-9_]+$` before migration — scopes are `"_deployment_*_" + index`, `Scope()`/`Name()` letter encodings, digit suffixes, table names `holon`/`wallet`/`action_graph_bundle`/`user`, fixed lowercase column dictionaries. A token outside the allowlist now throws (fail closed) instead of rendering.

**§7 fail-closed class list.** Binding names: unchanged. Array captures: none added. Positional offsets: unchanged. Strict validator: no `FOR`/closure variables added. Lowercase-enum / explicit-NULL raw writes: none added. Captured-null guard widening: not touched (W1 re-sweep). Bare ids into `[References]`: none added. `;` in `Of` literals: F-H13 fixed; scan of every `.Of(` literal in production finds none. Ordinal enum casts: removed. Converter-less enums: fixed + gate. Statements after top-level RETURN: none added.

**Conventions / pruning gate.** IDE0005 now enforced and clean; no new raw SurrealQL (one raw `IF…THROW` replaced by `RequireResult`); no catch-all added; rationale in `Core/Surreal/AGENTS.md` §identifier-template, `Helpers/AGENTS.md` §BCL names, root `AGENTS.md` §Program.cs registration order, `Architecture/AGENTS.md` §enum-storage.

**Security.** No route changed. Newly *resolvable* (previously a DI failure): `FungibleCreationController` and the fungible worker — their authz/scope belong to L3's lane review. The KYB builder is test-only.

**Helper re-verification.** A: `SigningContext` is a `readonly record struct`; the WTT validators now require the `WttSigningAuthority` record and still check the bound context fields (no placeholder context). The removed `AssetCloseTo` check on Algorand acceptance is covered: `TxID()` must equal the server-built plan's id, which binds the whole transaction body (a close-to field changes the id). B: D1 creation fence / F-M5 CAS reviewed in its record.

## For L2–L7

- **L5 — `OperationStatus` mapping** (`Helpers/NftLifecycleOperationProjection.cs:32`): lifecycle `State == "Completed"` → `OperationStatus.Completed` (generic terminal success; there is no `Confirmed` constant); any other non-cancelled/failed state → `PendingConfirmation`. Consumers that accept terminal NFT success (`AllocationManager.cs:281` accepts `Completed|Transferred|Minted`) keep working; `NftManagerTests` updated to `Completed`. Confirm or switch to per-operation verbs (`Minted`/`Transferred`/`Burned`).
- **L3:** D8 (`FiatRailProvider.cs:~591` `expires_at` per attempt) is still open (helper B note). Fungible creation routes are now live in DI.
- **L2:** host test asserts only "outer is `RiskHoldValueGate`, inner is not" so the in-place KYC-gate swap at the single `IRealValueKycGate` registration passes unchanged.
- **L4:** resolver domains are unique today (`emit, fungible-creation, hold, local, nft, read, deployment, swap, governance`); `ActionOperationRecovery` builds a dictionary and throws on a duplicate.
- **L6:** `SurrealQlSourceRulesTests` "SRDB0001 only in the template helper" must also allow `SurrealGeneratedStatementScope.cs` (4 pre-existing suppressions). XML-doc diagnostics are silenced in the app csproj; 118 latent doc issues (95 CS1573, 7 CS1574 broken crefs, …) are unrecorded debt for whoever wants them.
- **L7:** frontend `tsconfig` target is now `ES2020`; SDK root index now exports `ActionRecovery*`, `ActionRunResult`, `FiatPayoutView`.
- **Owner:** none.
