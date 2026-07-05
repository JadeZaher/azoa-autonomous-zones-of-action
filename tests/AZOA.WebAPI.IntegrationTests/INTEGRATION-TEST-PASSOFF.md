# Integration Test Pass-off

Branch: `feat/ardanova-azoa-integration-phase2`
Investigated at HEAD `0bac087` (the prompt referenced `579f308`; more work has
landed since, but the failure shape is the same pre-existing class of issues).

## TL;DR

The reported symptom (62 Failed / 37 Passed / 154 Skipped, every failure an
opaque `400 (Bad Request)` from the seed helpers) had a **single dominant root
cause that is now FIXED**: the in-process WebAPI app wrote to SurrealDB
namespace `azoa`, which no test ever created, while the harness created a
*different* per-test namespace. Aligning the two namespaces (plus a handful of
clearly-wrong test fixtures and a test-side JSON converter gap) took the suite
from **37 Passed / 62 Failed** to **216 Passed / 37 Failed / 0 Skipped**.

The remaining 37 are a **pre-existing long tail** that the namespace bug was
masking — every test used to fail at seed time before ever reaching its real
assertion, so these were invisible. They are heterogeneous (test-design vs the
IDOR-hardening, environment-dependent gates, machine-dependent perf budgets,
and a store-layer socket race) and several need product/owner decisions, so
they are documented here rather than force-greened.

## Exact repro

```bash
# 1. Test SurrealDB container (v3.1.4, root/root, in-memory) on :8000
podman run -d --name azoa-test-surrealdb -p 8000:8000 \
  docker.io/surrealdb/surrealdb:v3.1.4 \
  start --log info --user root --pass root --bind 0.0.0.0:8000 memory

# 2. Full suite
dotnet test tests/AZOA.WebAPI.IntegrationTests/AZOA.WebAPI.IntegrationTests.csproj -nologo

# Single class
dotnet test tests/AZOA.WebAPI.IntegrationTests/AZOA.WebAPI.IntegrationTests.csproj \
  --filter "FullyQualifiedName~HolonControllerIntegrationTests" -nologo
```

Connection defaults: `tests/AZOA.WebAPI.IntegrationTests/SurrealTestDefaults.cs`
(`http://127.0.0.1:8000`, `root`/`root`).

---

## Root cause #1 (FIXED): app namespace `azoa` never created

### The actual 400 body (captured)

```json
{"isError":true,
 "message":"SurrealHolonStore.UpsertAsync failed: SurrealDB statement 1/1 returned ERR: The namespace 'azoa' does not exist",
 "result":null,"detail":null}
```

`HttpResponseMessage.EnsureSuccessStatusCode()` in the seed helpers threw
`"400 (Bad Request)"` *without* the body, hiding this. Surfacing the body was
step 1 of the investigation and was kept as a permanent harness improvement
(`EnsureSeedSucceededAsync`).

### Why it happened (file:line)

- `https://github.com/Escherbridge/surrealforge/tree/main/src/SurrealForge.Client/SurrealConnectionOptions.cs:26` — the app's
  `Namespace` defaults to `"azoa"`.
- `Extensions/SurrealDbServiceCollectionExtensions.cs:61` — binds
  `SurrealConnectionOptions` from the `SurrealDb` config section.
- `tests/AZOA.WebAPI.IntegrationTests/Factories/AZOATestWebApplicationFactory.cs`
  set `SurrealDb:Endpoint/User/Password` but **NOT** `SurrealDb:Namespace`, so
  the app kept the `azoa` default.
- `tests/AZOA.WebAPI.IntegrationTests/IntegrationTestBase.cs:73` (original) —
  the harness created a *different* namespace `test{guid}` per test instance and
  applied the generated `.surql` schemas there.
- Net effect: the harness provisioned `test{guid}` while the app connected to
  `azoa`. `azoa` was never created (the in-memory container boots empty), so
  every controller write faulted with "namespace does not exist".

This is exactly the long-standing `integration-test-namespace-isolation` gap
noted in project memory: the per-test namespace was created but never wired to
the WebAPI executor.

### The fix

The factory is an `IClassFixture` (one host per test class) and
`SurrealConnectionOptions` binds once at host build, so per-test-**method**
namespace isolation through the app is impossible via static config. The
correct granularity is **per test class**:

- `AZOATestWebApplicationFactory` now owns a per-factory namespace
  `public string TestNamespace { get; } = $"itest{Guid.NewGuid():N}"` and pins
  the app to it via `SurrealDb:Namespace` + `SurrealDb:Database` in the
  in-memory config.
- `IntegrationTestBase` now uses `factory.TestNamespace` (instead of generating
  its own) so the namespace it CREATES + schemas is the SAME one the app
  CONNECTS to.

Classes run in parallel under their own namespace; methods within a class share
it and run serially (xUnit default). **Tradeoff:** methods in a class now share
data (no per-method DB reset). This unmasked a few fixture collisions, fixed
below.

---

## Other clean fixes applied (test-only, no production change)

1. **Seed body diagnostics** (`IntegrationTestBase.EnsureSeedSucceededAsync`) —
   seed failures now include the HTTP status + response body instead of an
   opaque "400 (Bad Request)". Kept permanently; this is how every subsequent
   root cause below was diagnosed in one run.

2. **Test JSON must mirror the server** (`IntegrationTestBase.JsonOptions`) —
   the server registers `JsonStringEnumConverter` (`Program.cs:54`) but the test
   client's `JsonSerializerOptions` did not, so deserializing any response
   containing an enum threw
   `The JSON value could not be converted to ...WalletType`. Added the converter
   to the test options.

3. **Test fixtures that violated production validators** (the validators are
   correct; the fixtures predated them):
   - `Builders/TestDataBuilders.cs` WalletBuilder default address `addr_{guid}`
     → `addr{guid}` (underscore violated `WalletCreateModelValidator`'s
     `^[a-zA-Z0-9]+$`, `Validators/WalletCreateModelValidator.cs:18`).
   - `Builders/TestDataBuilders.cs` AvatarBuilder default username/email made
     **unique per instance** (were constant `testuser`/`test@azoa.local`) — with
     a class sharing one namespace, the second `SeedAvatarAsync()` hit
     "An account with this email already exists."
   - `Controllers/AvatarControllerIntegrationTests.cs` — passwords
     `secret123`/`right` (no uppercase / too short) and usernames `a1`/`a2`
     (< 3 chars) replaced with validator-compliant values
     (`AvatarRegisterValidator.cs:10-16`).
   - `Controllers/WalletControllerIntegrationTests.cs` — inline addresses
     `sol_addr_1`/`dup_addr` → `soladdr1`/`dupaddr`.

### Before / after

| | Passed | Failed | Skipped |
|---|---|---|---|
| Before (reported) | 37 | 62 | 154 |
| After fixes | **216** | **37** | **0** |

(Skips went to 0 because the namespace fix means the SurrealDB-touching tests
now actually run instead of skipping/failing at setup.)

---

## Remaining 37 failures — triage (NOT fixed; see rationale)

### Bucket A — test design conflicts with the IDOR-hardening (≈15 tests)

**Root cause (file:line):** `Controllers/AvatarController.cs:61-77` (Update,
Delete) and the wallet/NFT/blockchain-op controllers pass BOTH the route id AND
the **authenticated** avatar id (`GetAvatarIdFromClaims()` → `TestAuthHandler`
`DefaultAvatarId` = `a1111111-1111-1111-1111-111111111111`) to the manager,
which scopes the operation to the authenticated avatar (the IDOR-resistant
"owned-resource" pattern recorded in project memory, STARODK precedent).

The failing tests seed a resource owned by a **fresh random** avatar
(`Guid.NewGuid()` or the register-generated id) and then act on it while
authenticated as `DefaultAvatarId`. The manager correctly finds no row matching
(id = seeded, owner = default) and returns 404 / empty.

Representative failures:
- `AvatarControllerIntegrationTests.{Delete,Update,AddWallet,GetWallets,RemoveWallet}` → 404 / 400
- `AvatarNFTControllerIntegrationTests.*` (Get/Bind/Transfer/Verify/composite) → 400, and `GetAvatarNFTsByAvatarAsync` → found 0
- `BlockchainOperationControllerIntegrationTests.{Get_ExistingOperation,GetByAvatar}` → 404 / found 0
- `Mcp*` happy-path tools (`AvatarScopedQuery`, `NftOwnershipGraph`,
  `HolonTraverse`, `QuestReachability`, `VectorSearch`) → "found 0" /
  "quest/holon not found" (same avatar-scoping mismatch in the seed-vs-query
  identity).

**Why not fixed here:** the correct fix is per-test and intent-sensitive —
either seed the resource owned by `DefaultAvatarId`, or drive the request with
`factory.CreateAuthenticatedClientForAvatar(seededAvatarId)`. Doing this
wholesale risks silently neutering the IDOR assertions these tests are partly
meant to guard. This needs the test owner to confirm, per test, whether the
intent is "act on my own resource" (use the seeded id as the auth identity) or
"the server must reject cross-owner access" (keep the mismatch but assert 404).

**Proposed fix (per test):**
1. For "happy path on my own resource" tests, replace
   `var x = await SeedXAsync(...)` + `Client.<verb>(...)` with a client
   authenticated as the resource owner:
   `var client = Factory.CreateAuthenticatedClientForAvatar(ownerId);` and use
   `ownerId` consistently for both the seed's `ForAvatar(ownerId)` and the
   request. Use `Guid.Parse(TestAuthHandler.DefaultAvatarId)` as `ownerId` when
   the default client is used.
2. For the avatar self-CRUD tests (Update/Delete), the authenticated identity
   IS the avatar, so they must operate on `DefaultAvatarId`. Add a seed path that
   registers/ös upserts the avatar row with `Id == DefaultAvatarId` (e.g. a
   `SeedSelfAvatarAsync()` helper that writes via `SurrealClient` with the fixed
   id) and call Update/Delete on that id.

### Bucket B — environment / repo-layout dependent (5 tests)

These assert on files/containers that the recent `Core/` structure refactor
moved or renamed. Not a code bug; the gate config paths are stale.

- `G1_CrashDurabilityTest.G1_DurabilityAckGate_FailsClosed_IfSyncEventual`
  → expects `podman-compose.yml` at repo root; file not present at that path.
- `G1_CrashDurabilityTest.G1_HardKill_DurableInserts_SurviveRestart`
  → `podman kill ... surrealforgedb-test` — expects a container literally named
  `surrealforgedb-test`; the working container here is `azoa-test-surrealdb`.
- `G5_RestoreDrillTest` → expects `scripts/surrealdb/backup.ps1`; the `scripts/`
  tree no longer exists at this commit (confirmed: `scripts/` is absent).
- `SurrealPerfBudgets.WalletGetById_P99_Under50ms` → machine-dependent timing
  (observed p99 64–132 ms vs 50 ms budget); flaky on this host.

**Proposed fix:** update the gate config paths to the post-refactor locations
(or restore `scripts/surrealdb/*` + `podman-compose.yml`), standardize the test
container name to one constant shared by the gate tests and the start docs, and
relax/quarantine the perf budget on developer machines (keep it CI-only with a
warm-up + higher budget, or `[Trait("Category","Perf")]` + skip locally).

### Bucket C — store-layer socket race / own-namespace tests (3 tests)

- `SurrealPerfBudgets.{BridgeTxInsert,SagaSteps_DueScan}` →
  `The namespace 'test<guid>' does not exist` — these construct their OWN
  per-test namespace via `SurrealTestSchema`/direct client and have a
  cross-connection DB-visibility race (documented in `SurrealTestSchema.cs:50-55`).
- `SurrealQuestStoreTests.UpsertQuest_RewritesChildren...` →
  `Only one usage of each socket address ... (127.0.0.1:8000)` — ephemeral
  `HttpClient`-per-call socket exhaustion under parallel load. This one is
  **non-deterministic** (it passed in run #2, failed in run #3), confirming a
  race rather than a logic bug.

**Proposed fix:** route store-layer tests through a single pooled
`IHttpClientFactory`/shared `HttpClient` (the package already supports this for
the app via `AddHttpClient`), and apply DB-visibility ordering on one connection
as `SurrealTestSchema.BootstrapWithExtraAsync` already does. Consider a small
xUnit collection to serialize the Surreal-direct store tests if socket pressure
persists.

### Bucket D — factory re-registration + genuine app questions (≈3 tests)

- `G2_IdempotencyTocTouTest.IdempotencyKey_50Concurrent...` →
  `Scheme already exists: TestScheme`. `Mcp/McpAuthScopingIntegrationTests.cs:104-112`
  (and the G2 test) call `WithWebHostBuilder` + `AddAuthentication().AddScheme(TestScheme)`
  a second time, colliding with the base factory's registration
  (`AZOATestWebApplicationFactory.cs:65-71`). **Proposed fix:** in the override,
  REPLACE rather than ADD — clear existing `AuthenticationSchemeOptions`/scheme
  registrations first, or register the parameterized handler under a distinct
  scheme name and point the default scheme at it.
- `HolonControllerIntegrationTests.Interact_ShouldRemovePeersAndMetadata` →
  seeds a holon with one peer, posts an interaction removing that peer, expects
  `PeerHolonIds` empty but the peer is still present. This is a **genuine
  app-behavior question** (peer-removal on the holon interaction path) — needs a
  trace through `HolonController` interact → manager → `SurrealHolonStore` to
  confirm whether removal is wired. Do NOT paper over with a test change; this
  may be a real bug.
- `HolonControllerIntegrationTests.{Mint,Exchange}` and
  `STARODKControllerIntegrationTests.Deploy` → 400 from the chain-action path;
  likely the chain-capability gate / wallet pre-conditions, and
  `AvatarNFTController.Mint` → 500. These should be re-checked with the
  now-surfaced body (the seed diagnostic does not cover non-seed POSTs; add the
  same body-surfacing to the assertion or read `response.Content` in the test to
  see the real error before deciding fix vs accept).

---

## Files changed (all under tests/AZOA.WebAPI.IntegrationTests/, no production code)

- `Factories/AZOATestWebApplicationFactory.cs` — own + pin per-class namespace
  (`SurrealDb:Namespace`/`Database`).
- `IntegrationTestBase.cs` — use `factory.TestNamespace`; add
  `EnsureSeedSucceededAsync` (body-surfacing) on all seed helpers; add
  `JsonStringEnumConverter` to `JsonOptions`.
- `Builders/TestDataBuilders.cs` — alphanumeric wallet address default; unique
  avatar username/email defaults.
- `Controllers/AvatarControllerIntegrationTests.cs` — validator-compliant
  seed credentials/usernames.
- `Controllers/WalletControllerIntegrationTests.cs` — alphanumeric inline
  addresses.

## Verification at pass-off

- `dotnet build AZOA.WebAPI.csproj` → Build succeeded, 0 errors (no production
  code touched).
- `dotnet test tests/AZOA.WebAPI.Tests` → **985 Passed, 0 Failed** (unchanged).
- `dotnet test tests/AZOA.WebAPI.IntegrationTests` → **216 Passed, 37 Failed,
  0 Skipped** (was 37 / 62 / 154).
