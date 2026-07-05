# DB-Only Null Provider — Plan

Source spec: [spec.md](spec.md)
Seam reference: `signing-core-keystone` (via
`conductor/tracks/custody-key-management/spec.md:113,235,256`)

## Decisions to record before starting

These require a choice between valid approaches. Record the decision here (replace
the `[ decision ]` marker) before executing the corresponding phase.

| # | Decision required | Notes / leaning |
|---|-------------------|-----------------|
| D1 — Provider name + `ChainType` | `[ decision ]` `SimulatedBlockchainProvider` with `ChainType = "Simulated"`, **or** `NullBlockchainProvider` with `ChainType = "Null"`. | Leaning **`SimulatedBlockchainProvider` / `"Simulated"`** — "simulated" reads as *intentional fake settlement*, which matches the deterministic-confirmed behavior better than "null" (which implies no-op). Distinct `ChainType` keeps the factory cache key `"{chainType}:{network}"` (`BlockchainProviderFactory.cs:37`) from aliasing a real chain. |
| D2 — Selection flag shape | `[ decision ]` **Global** `Blockchain:Mode = "Simulated" \| "Live"` that overrides `GetDefaultProvider()` / `GetProvider(...)`, **or** **per-chain** (`BlockchainChainConfig.Mode`, or a `"Simulated"` entry in `Chains[]`). | Leaning **global `Blockchain:Mode`** for v1 (simplest, the dev/test/demo default this track targets); leave per-chain as a documented follow-up. Flag lives in the `"Blockchain"` section (`appsettings.json:49`), read via `BlockchainConfig` (`Core/BlockchainNetworkConfig.cs:24-29`) — **not** hardcoded (per `config-driven-calls`). |
| D3 — Where the factory honors the flag | `[ decision ]` (a) Mutate `BlockchainProviderFactory.GetProvider` / `GetDefaultProvider` to short-circuit to the simulated provider when `Mode == Simulated`, **or** (b) wrap/decorate the factory so `GetProvider` selection is external and the factory class is untouched. | Leaning **(a)** — a small, well-tested branch at the top of `GetProvider` (`BlockchainProviderFactory.cs:32`) keyed on `_config` is the least surprising. The provider is still registered via DI (`Program.cs:413-414` + one new line), so resolution is unchanged; only *selection* gains the branch. |
| D4 — Simulated balance ledger | `[ decision ]` (a) New SurrealDB table `simulated_balance` (POCO under `Persistence/SurrealDb/Models/`), (b) event-source balances from `BlockchainOperation` rows (`Models/BlockchainOperation.cs`), or (c) in-memory ledger (test/demo only, non-durable). | Leaning **(a)** for a durable demo experience; the real `wallet` aggregate stays balance-free (`Wallet.cs:23`). If durability isn't needed for v1, **(c)** is acceptable and cheapest — record which. Either way the marker guardrail (sim-only) applies. |
| D5 — Tx-hash persistence marker | `[ decision ]` Store synthetic hash in `BlockchainOperation.Parameters["txHash"]` with a `Parameters["simulated"]="true"` flag, **or** add a `TxHash` + `Simulated` column to `BlockchainOperation` (`Models/BlockchainOperation.cs:5`). | Leaning **`Parameters` dict** (`BlockchainOperation.cs:12`) for v1 — no schema change, and the `sim:tx:` prefix on the value is self-marking. Promote to real columns only if querying simulated rows by index becomes hot. |

---

## Phase 1 — Provider skeleton + marker contract

- [ ] Create `Providers/Blockchain/Simulated/SimulatedBlockchainProvider.cs`
      (namespace mirroring `AlgorandProvider.cs:8`), deriving
      `BaseBlockchainProvider` (`Core/Blockchain/Base/BaseBlockchainProvider.cs:39`),
      ctor `(IConfiguration, ILogger<SimulatedBlockchainProvider>)` like
      `AlgorandProvider.cs:24-34`. (Use D1 for the name + `ChainType`.)
- [ ] Implement the marker helpers: `SimAddress(chain, seed)` → `sim:<chain>:<hash>`
      and `SimTxHash(op, inputs)` → `sim:tx:<digest>`. Centralize the `sim:`
      prefix as a constant so tests reference it. (Guardrail per spec "Identity
      and marker".)
- [ ] `ValidateAddressAsync` (`IBlockchainProvider.cs:20`): accept `sim:`-prefixed
      addresses as valid; reject anything that looks like a real address (no
      cross-contamination).

## Phase 2 — Deterministic write + status ops

- [ ] Override `MintAsync` (`IBlockchainProvider.cs:23`),
      `TransferAsync` (`:36`), `BurnAsync` (`:30`) to return
      `Ok(SimTxHash(...))` via the base `Ok<T>` helper
      (`BaseBlockchainProvider.cs:332`). Hash must be a pure function of inputs
      (same inputs ⇒ same hash). These replace the real providers' signing-required
      stubs (`AlgorandProvider.cs:148-164`) for simulated mode.
- [ ] Override `GetTransactionStatusAsync` (`IBlockchainProvider.cs:68`): any
      `sim:tx:`-prefixed hash → `OperationStatus.Completed`
      (`Models/OperationStatus.cs:32`) confirmed dict, no network call.
- [ ] Override `GetChainInfoAsync` (`IBlockchainProvider.cs:87`) to return a
      clearly-marked simulated chain descriptor.

## Phase 3 — Balance ledger + persistence seam

- [ ] Implement `GetBalanceAsync` (`IBlockchainProvider.cs:19`) against the
      simulated ledger (per D4). After a simulated mint/transfer/burn the balance
      reflects the change.
- [ ] Wire the synthetic tx hash + simulated marker into the existing operation
      record path: `BlockchainOperation.Parameters` (`Models/BlockchainOperation.cs:12`)
      via `IBlockchainOperationStore.UpsertAsync`
      (`Interfaces/Stores/IBlockchainOperationStore.cs:15`). (Per D5.) Confirm the
      manager seam (`NftManager.cs:53-77`) needs no change — the provider returns
      the hash; the manager already persists the entity + operation.

## Phase 4 — Selection wiring

- [ ] Register the provider: add
      `builder.Services.AddSingleton<IBlockchainProvider, SimulatedBlockchainProvider>();`
      next to `Program.cs:413-414`.
- [ ] Add the `Blockchain:Mode` flag to `BlockchainConfig`
      (`Core/BlockchainNetworkConfig.cs:24-29`) and a sane per-environment default
      in `appsettings.json` / `appsettings.Development.json` (Development → Simulated).
      (Per D2.)
- [ ] Honor the flag in the factory (per D3): when `Mode == Simulated`,
      `GetProvider` / `GetDefaultProvider` (`BlockchainProviderFactory.cs:32-60`)
      resolve the simulated provider. Preserve the existing throw
      (`:34-35`) for genuinely-unregistered chains in Live mode.

## Phase 5 — Tests

- [ ] **Factory selection test:** with `Blockchain:Mode = "Simulated"` the factory
      returns `SimulatedBlockchainProvider`; with `"Live"` it returns the real
      provider for the requested `ChainType`. Tests load real `appsettings`
      (per `config-driven-calls`).
- [ ] **Determinism test:** `MintAsync` / `TransferAsync` / `BurnAsync` with the
      same inputs return identical `sim:tx:` hashes; different inputs differ.
- [ ] **Confirmation test:** `GetTransactionStatusAsync` on a simulated hash
      reports `OperationStatus.Completed`.
- [ ] **Persistence test:** simulated mint/transfer/burn produce the expected
      balance via `GetBalanceAsync` and a persisted operation carrying the marker.
- [ ] **Marker / no-collision test:** simulated addresses + hashes carry the
      `sim:` prefix and assert the format cannot equal a real Algorand (58-char
      base32, `AlgorandProvider.cs:64-69`) or Solana address.

## Phase 6 — Verification

- [ ] `dotnet build` — **zero warnings** (nullable enabled),
      per `conductor/workflow.md:18`.
- [ ] `dotnet test` — green.
- [ ] Swagger UI launches and lists all expected endpoints
      (`conductor/workflow.md:20`) — no endpoint regressions from the new registration.
- [ ] Grep the new provider for any real network call (`HttpClient`, node URL) —
      there must be **none**; the simulated provider never touches a node.
- [ ] Confirm simulated rows are queryable by their marker (the guardrail holds at
      the data layer).
- [ ] Move `tracks.md` row for `db-only-null-provider` to `[x]` Shipped.

## Known follow-ups (file separately)

- **Per-chain simulated mode** — if D2 chose global, the per-chain
  `BlockchainChainConfig.Mode` variant (some tenants Live, some Simulated) is a
  clean follow-up.
- **Frontend mode toggle / badge** — surface "Simulated" state in the UI so demo
  users see they are not on real chain. Out of scope here.
- **Promote tx-hash marker to a column** — if D5 chose `Parameters`, add a real
  `TxHash` + `Simulated` column to `BlockchainOperation`
  (`Models/BlockchainOperation.cs:5`) once querying simulated rows by index is hot.
