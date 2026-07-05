# DB-Only Null Provider — Specification

## Goal

Add a **simulated blockchain provider** — a `SimulatedBlockchainProvider` deriving
from `BaseBlockchainProvider` — that lets the platform run in a **database-only,
no-chain mode**. In this mode mint / transfer / burn / balance operations are
satisfied entirely from SurrealDB and return **deterministic synthetic tx hashes**
that report as confirmed, instead of touching any real network. Selection is a
**config flag** routed through the existing `IBlockchainProviderFactory`, so the
rest of the app is unchanged. The simulated provider is the right default for
dev / test / demo and for "no-chain" tenants who only want the wallet + NFT data
model without on-chain settlement.

## Background

The provider seam already supports this cleanly. Three facts make it a drop-in:

1. **The base class is fully virtual with not-implemented defaults.**
   `Core/Blockchain/Base/BaseBlockchainProvider.cs:39` is `abstract class
   BaseBlockchainProvider : IBlockchainProvider` where every `IBlockchainProvider`
   method is `virtual` and returns `Error<T>("… not implemented")`
   (`BaseBlockchainProvider.cs:76-197`). A derived provider overrides only what it
   needs — exactly the pattern `AlgorandProvider`
   (`Providers/Blockchain/Algorand/AlgorandProvider.cs:14`) uses.

2. **The factory keys providers by `ChainType` string and is DI-fed.**
   `Core/BlockchainProviderFactory.cs:20-30` takes
   `IEnumerable<IBlockchainProvider>` from DI and builds a
   `Dictionary<string, Func<IBlockchainProvider>>` keyed on each provider's
   `ChainType`. `GetProvider(chainType, network)`
   (`BlockchainProviderFactory.cs:32-55`) resolves by that string and caches the
   initialized instance under `"{chainType}:{network}"`. Registration is two lines
   in `Program.cs:413-414`:
   ```csharp
   builder.Services.AddSingleton<IBlockchainProvider, AlgorandProvider>();
   builder.Services.AddSingleton<IBlockchainProvider, SolanaProvider>();
   ```
   Adding a third `AddSingleton<IBlockchainProvider, SimulatedBlockchainProvider>()`
   is sufficient for the factory to see it — **no factory code change is required**
   to make the provider *resolvable*. The decision in `plan.md` is only about how
   the flag *selects* it (see Decisions).

3. **Providers in this codebase return tx hashes; managers own persistence.**
   The provider contract returns `AZOAResult<string>` (a tx hash) for write ops —
   e.g. `MintAsync` (`Interfaces/IBlockchainProvider.cs:23-28`),
   `TransferAsync` (`:36-41`), `BurnAsync` (`:30-34`). SurrealDB persistence of the
   resulting entity lives in the manager layer: `NftManager.MintAsync`
   (`Managers/NftManager.cs:53-76`) builds a `Holon` with `AssetType = "NFT"` and
   calls `_holonStore.UpsertAsync(holon, …)`, then records a `BlockchainOperation`
   via `IBlockchainOperationStore` (`Managers/NftManager.cs:14,77-`). Wallets and
   Holons are SurrealDB POCOs: `Persistence/SurrealDb/Models/Wallet.cs:28` (table
   `wallet`, no balance field — *chain is source of truth* per its `SurrealNote`
   at `:23`) and `Persistence/SurrealDb/Models/Holon.cs:26` (table `holon`,
   `asset_type` at `:63-65`).

   **Design consequence:** the simulated provider does **not** re-implement entity
   persistence (the managers/stores already do that). Its job is to (a) synthesize
   deterministic addresses + tx hashes, (b) report those hashes as confirmed via
   `GetTransactionStatusAsync`, and (c) own the one thing that has no DB home today
   — a **simulated balance ledger** — since real providers read balances from chain
   (`Wallet.cs:23` deliberately has no balance column).

Current write methods on the real providers are mostly *stubs that error* because
they require signing — e.g. `AlgorandProvider.TransferAsync`
(`AlgorandProvider.cs:156-164`) and `BurnAsync` (`:148-154`) return
`Error<string>("Transfers require signing …")`. In database-only mode there is no
signer and no chain, so these become **deterministic simulated successes** instead.

## Confirmed source facts (file:line evidence)

### Provider abstraction surface

`Interfaces/IBlockchainProvider.cs` — the full contract the simulated provider
implements (via the base class):

- Props: `ChainType` (`:8`), `ActiveNetwork` (`:9`), `SupportsBridging` (`:120`).
- `Initialize(BlockchainNetworkConfig, ChainNetwork)` (`:11`),
  `TryGetModule<T>(out T?)` (`:16`).
- Account: `GetBalanceAsync` (`:19`), `ValidateAddressAsync` (`:20`).
- Lifecycle: `MintAsync` (`:23`), `BurnAsync` (`:30`), `TransferAsync` (`:36`).
- Exchange: `ExchangeAsync` (`:44`), `SwapAsync` (`:51`).
- Query: `GetTokenMetadataAsync` (`:60`), `GetTokensByOwnerAsync` (`:64`),
  `GetTransactionStatusAsync` (`:68`).
- Contract: `DeployContractAsync` (`:73`), `CallContractAsync` (`:79`).
- Chain info: `GetChainInfoAsync` (`:87`).
- Bridge: `LockForBridgeAsync` (`:93`), `MintWrappedAsync` (`:100`),
  `BurnWrappedAsync` (`:107`), `VerifyBridgeProofAsync` (`:114`).

### Base class

`Core/Blockchain/Base/BaseBlockchainProvider.cs` (namespace
`AZOA.WebAPI.Providers.Blockchain.Base`, `:6`):

- `abstract string ChainType` (`:45`), `ChainNetwork ActiveNetwork` settable
  (`:46`), `virtual Initialize` (`:54-61`).
- All write/read methods virtual with `Error<T>("… not implemented")` defaults
  (`:76-197`).
- Helpers `Error<T>` (`:321-330`), `Ok<T>` (`:332-340`) — the simulated provider
  builds its results with these.

### Factory + registration + config

- `IBlockchainProviderFactory.GetProvider(string chainType, ChainNetwork network)`
  (`Core/BlockchainProviderFactory.cs:8`), `GetDefaultProvider()` (`:9`).
- DI-fed provider dictionary keyed by `ChainType`
  (`BlockchainProviderFactory.cs:24-29`); throws
  `"No provider registered for chain type: {chainType}"` on miss (`:34-35`).
- `BlockchainConfig` (`Core/BlockchainNetworkConfig.cs:24-29`):
  `DefaultChain` (`:26`), `DefaultNetwork` (`:27`), `Chains` (`:28`).
- `BlockchainChainConfig` (`:15-22`) and `BlockchainNetworkConfig` (`:3-13`)
  with `IsEnabled` (`:12`).
- `appsettings.json:49-` `"Blockchain"` section drives all of the above.

### Persistence seam (managers own it, not providers)

- `NftManager` ctor injects `IHolonStore` + `IBlockchainOperationStore`
  (`Managers/NftManager.cs:13-20`); `MintAsync` upserts a `Holon`
  (`:60-73`).
- `IBlockchainOperationStore.UpsertAsync` (`Interfaces/Stores/IBlockchainOperationStore.cs:15`).
- `BlockchainOperation` (`Models/BlockchainOperation.cs:5`): has `Status`
  (`:11`, default `OperationStatus.Pending`) and a `Parameters` string dict
  (`:12`) — **but no dedicated `TxHash` column today**. Synthetic tx hashes
  therefore live in `Parameters["txHash"]` (or equivalent) until a column is added.
- `OperationStatus` constants (`Models/OperationStatus.cs`): `Pending` (`:23`),
  `Completed` (`:32`), `Minted` (`:40`), `Burned` (`:41`).

## Design — the simulated provider

### Identity and marker (CRITICAL brand/safety guardrail)

Simulated tx hashes and addresses **must be distinguishable from real ones at the
data layer** so simulated data can never be mistaken for settled on-chain value.

- **Address format:** deterministic synthetic addresses carry a documented
  `sim:` prefix (e.g. `sim:algo:<base32-of-hash>`). The prefix is reserved — no
  real Algorand (58-char base32, `AlgorandProvider.cs:64-69`) or Solana (base58)
  address can collide because neither alphabet contains `:`.
- **Tx-hash format:** synthetic hashes carry a `sim:tx:` prefix followed by a
  deterministic digest of the operation inputs (see Determinism below).
- **Persisted marker:** a boolean / discriminator (e.g. `simulated = true`, or the
  `sim:` prefix on the stored hash in `BlockchainOperation.Parameters`) so a query
  can always partition simulated rows from real ones. The chosen marker mechanism
  is recorded in `plan.md`.
- The provider's `ChainType` is its own distinct value (e.g. `"Simulated"`) so
  the factory cache key `"{chainType}:{network}"`
  (`BlockchainProviderFactory.cs:37`) never aliases a real chain's cache slot.

### Determinism

Write ops return a **deterministic** synthetic tx hash derived purely from inputs
(e.g. `sim:tx:` + a stable hash of `{op, walletAddress, tokenId, amount,
assetType, nonce?}`). The same logical operation produces the same hash, which is
what makes the unit tests assertable. `GetTransactionStatusAsync`
(`IBlockchainProvider.cs:68`) recognizes any `sim:tx:`-prefixed hash and reports
it `confirmed` / `OperationStatus.Completed` without any network call.

### Simulated balance ledger

Real wallets have **no balance column** by design (`Wallet.cs:23`). The simulated
provider therefore needs a small balance ledger so `GetBalanceAsync`
(`IBlockchainProvider.cs:19`) returns a coherent value after a simulated mint /
transfer / burn. The storage choice (a new SurrealDB table vs. reusing
`BlockchainOperation` rows as an event-sourced balance vs. an in-memory ledger for
test-only mode) is a **decision recorded in `plan.md`**. Whatever is chosen, the
marker guardrail applies: simulated balances are never written to the real
`wallet` aggregate.

### Selection mechanism

A config flag makes the factory hand out the simulated provider. Two valid
shapes (decision in `plan.md`):

- **Global mode:** a top-level `Blockchain:Mode = "Simulated" | "Live"` (default
  per environment) that, when `Simulated`, makes `GetDefaultProvider()` /
  `GetProvider(...)` return the simulated instance regardless of requested chain.
- **Per-chain mode:** a `BlockchainChainConfig.Mode` (or a `"Simulated"` chain
  entry in `appsettings.json:52` `Chains[]`) so some tenants/chains are simulated
  while others are live.

Either way the flag is honored through config (per `config-driven-calls` memory —
tests load real `appsettings`), **not** hardcoded.

## Acceptance criteria

- [ ] New `SimulatedBlockchainProvider : BaseBlockchainProvider` exists, overriding
      at minimum `MintAsync`, `BurnAsync`, `TransferAsync`, `GetBalanceAsync`,
      `ValidateAddressAsync`, `GetTransactionStatusAsync`, `GetChainInfoAsync`,
      with a distinct `ChainType` (e.g. `"Simulated"`).
- [ ] Registered via the same DI mechanism as the real providers (one
      `AddSingleton<IBlockchainProvider, SimulatedBlockchainProvider>()` near
      `Program.cs:413-414`); the factory resolves it with no factory-code change
      to its resolution path.
- [ ] A config flag (global `Blockchain:Mode` or per-chain — decision in `plan.md`)
      makes `IBlockchainProviderFactory` hand out the simulated provider when set;
      flag read from `appsettings`, not hardcoded.
- [ ] Simulated `MintAsync` / `TransferAsync` / `BurnAsync` return a
      **deterministic** synthetic tx hash (same inputs ⇒ same hash) and the
      resulting balance change is reflected by `GetBalanceAsync`.
- [ ] `GetTransactionStatusAsync` reports any simulated tx hash as confirmed
      (`OperationStatus.Completed`) without a network call.
- [ ] Simulated addresses and tx hashes carry the documented distinguishing marker
      (`sim:` prefix) and the persisted marker is queryable; a test asserts the
      marker is present and that the format cannot collide with a real address.
- [ ] Unit test: factory hands out `SimulatedBlockchainProvider` when the flag is
      set, and a real provider when it is not.
- [ ] Unit tests: simulated mint / transfer / burn persist (via the existing
      manager/store seam or the simulated ledger — per `plan.md`) and return
      deterministic, confirmable hashes.
- [ ] `dotnet build` passes with **zero warnings** (nullable enabled) per
      `conductor/workflow.md:18`.
- [ ] `dotnet test` green.
- [ ] `tracks.md` row for this track moves to `[x]` Shipped.

## Out of scope

- Real signing or any network I/O (that is `signing-core-keystone` +
  `blockchain-devnet-providers`). The simulated provider deliberately does the
  opposite — it never signs and never calls a node.
- Changing real provider behavior (`AlgorandProvider` / `SolanaProvider` stay as-is).
- A balance column on the real `wallet` aggregate (`Wallet.cs:23` stays
  balance-free; chain remains source of truth for live mode).
- Frontend wiring of a mode toggle (a later UX track; this track is backend +
  config + tests only).
- Bridge simulation correctness beyond returning marked synthetic results
  (`LockForBridgeAsync` et al. may return marked stubs; full bridge simulation is
  out of scope).

## Tier

**Tier 1** — valuable, not a value-flow blocker. It enables dev/test/demo and
no-chain tenants but gates no real settlement. No hard dependency.

## Dependencies

- **None hard.** The provider seam (`BaseBlockchainProvider`,
  `BlockchainProviderFactory`) already exists and is sufficient.
- **Aligns with `signing-core-keystone`** (the generic provider/signer seam,
  referenced by `conductor/tracks/custody-key-management/spec.md:113,235,256`).
  The simulated provider is the **no-signer** member of that same seam: when
  `signing-core-keystone` lands the `sign`-delegate contract, the simulated
  provider remains the path that requires no key. Keep the simulated provider's
  method signatures congruent with that seam so a tenant can be toggled
  Simulated ↔ Live without changing call sites.
