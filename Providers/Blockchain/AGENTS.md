# Providers/Blockchain — agent notes

Directory-level rationale for the blockchain providers. Terse one-line doc-comments
live in the code; the "why" and cross-cutting seams live here.

## Â§atomic-group-observation

`IAtomicTransferGroupObservationModule` is an evidence-only capability. Its
Algorand implementation uses Indexer records as the authority for a confirmed
two-leg group and validates every immutable field: transaction id, group id,
sender, ASA, recipient, amount, and the absence of close-to, clawback, and rekey
fields. Before any HTTP request, the Algorand adapter requires canonical
uppercase base32 transaction digests, canonical 32-byte base64 group digests,
and a positive invariant-culture ASA id. Both legs must have the same positive
confirmed round. If Indexer has
not indexed a leg, Algod can only classify it as pending, unseen, or pool
rejected; it can never upgrade that leg to confirmed. Observation performs no
persistence, signing, broadcast, settlement transition, or ownership mutation.

The capability also accepts `AtomicTransferGroupObservationEvidence` for a
durable receipt-driven reconciler. This is a secret-free projection of the same
facts needed for observation (chain/network, asset, source, destinations,
amounts, group and transaction ids); it deliberately has no signing context or
wallet material. The original request overload remains for direct callers and
first validates into this evidence type. A provider must treat both overloads as
identical read-only evidence paths.

## Â§network-instance-isolation

`IBlockchainProvider.Initialize` mutates a provider's network client and active
network. Therefore a registered provider is never a singleton instance shared by
network cache keys. `BlockchainProviderFactory` receives
`BlockchainProviderRegistration` creators, creates one fresh provider for each
canonical `(chainType, network)` pair, and initializes it exactly once through a
`Lazy` cache. Production DI registers the concrete providers as transient and
hands creators to the singleton factory; consumers resolve only through
`IBlockchainProviderFactory` and must not call `Initialize` themselves.

All shipped providers inherit `BaseBlockchainProvider`: constructors allocate no
network clients, factory binding constructs those resources exactly once, and a
factory-bound instance rejects later public `Initialize` calls. A standalone
provider may initialize itself lazily for a direct unit test or local tool, but a
factory registration must return it still unbound. Third-party implementations
that bypass the base class remain responsible for honoring the interface contract.
The factory rejects undefined `ChainNetwork` values and disabled live networks
before invoking a registration creator; simulated mode is the intentional
network-free exception.

This is a settlement precondition: network-specific treasury routing and any
future fee settlement may rely on a returned provider remaining bound to the
requested chain/network. The factory tests exercise concurrent real Solana
resolution and assert that resolving Testnet cannot retarget the cached Devnet
provider.

## §bridge — capability modules, not a boolean

### The seam

There are no bridge primitives on `IBlockchainProvider` and no `SupportsBridging` property.
Capability is expressed by implementing a module that `TryGetModule` resolves at runtime:

- `IBridgeSourceLeg` — `LockAsync`, `ReleaseAsync`
- `IBridgeTargetLeg` — `MintWrappedAsync`, `BurnWrappedAsync`

**Absence IS the fail-closed refusal, structurally.** A chain that cannot bridge does not
implement the module, so there is no method to call, no refusal body that could regress into a
fabricated `Ok`, and no boolean that could be set true by mistake. This is strictly stronger
than the boolean it replaced, and it can express a direction asymmetry the boolean could not:
Algorand is a proven SOURCE and deliberately not a target.

The orchestrator (`Services/CrossChainBridgeService.cs`) resolves two legs plus one
`IBridgeProtocol<TProof>` and runs a single lifecycle; it contains no chain name in any
decision. See `Services/AGENTS.md` §bridge.

### Verification path (there is exactly one)

Cross-chain proof / VAA verification is **not** a provider method. `IBlockchainProvider`
deliberately has **no `VerifyBridgeProofAsync`** — it was removed in B2 because the provider
overrides returned `true` unconditionally (an always-true verifier that lies). Verification
belongs to the PROTOCOL:

    Services/Bridge/WormholeProtocol.cs → Services/WormholeAdapter.cs
        → Services/Wormhole/Secp256k1VaaSignatureVerifier.cs

`WormholeProtocol` is the only seam the orchestrator can reach verification through; the
adapter is its Guardian-API transport and the single hardened guardian-quorum implementation,
which stays fail-closed and unconditional. Do **not** reintroduce a provider-level verifier. No
always-true boolean may exist on a verification path anywhere in the tree.

**Unconditional means unconditional: there is no configuration key.** The
`Blockchain:Wormhole:RequireFullSignatureVerification` escape hatch was REMOVED. It defaulted to
fail-closed, but it was a switch that turned off the guardian-quorum check authorising a wrapped
mint on the target chain, and no such switch may exist on a value path. `WormholeConfig` now
carries no boolean at all, and `WormholeAdapterTests` pins both halves: a structurally perfect VAA
still fails closed without a registered `IVaaSignatureVerifier`, and no combination of
configuration produces a structural-only acceptance.

### Real broadcast vs absent capability

A leg implementation MUST either broadcast a real transaction and return the real tx id, or
fail loud. A fabricated-success `Ok(op-id)` on a value path is forbidden (it makes the bridge
record value that never moved). `BridgeLock` carries an explicit `PendingConfirmation` flag so
"broadcast but unconfirmed" can never be read as settled.

- **Algorand — SOURCE leg, real and proven.** Lock and release broadcast real signed
  transactions and prove vault control, vault balance, and recipient opt-in against chain state
  before signing. No target leg: no canonical platform-controlled wrapped ASA lifecycle exists.
  See `Algorand/AGENTS.md`.
- **Solana — no legs.** `SolanaTransactionSigner` is genuinely complete; what is missing is the
  provider-side SPL build→submit pipeline and a reviewed bridge lifecycle on top of it. The
  refusal is never a signer problem, and `SolanaProviderSplPipelineTests` pins that by
  asserting the real `ISolanaSPLModule` is still present alongside the absent bridge legs.
- **Ethereum — no legs.** Same shape: the EIP-1559 value pipeline and its signer are real; the
  bridge lifecycle is unbuilt.
- **Simulated — both legs.** The explicitly synthetic provider is the only one that implements
  source and target, and every identifier it returns carries the reserved `sim:` marker.

### Solana canonical-byte contract (coordination with the B1 signer worker)

`ITransactionSigner.Sign(byte[] canonicalTxn, SigningKeyMaterial key)` is chain-agnostic:
the **provider** owns producing `canonicalTxn`; the **signer** owns the Ed25519 signature
over exactly those bytes. For Solana the contract this side assumes (and any real Solana
provider pipeline MUST produce) is:

- `canonicalTxn` = the **serialized Solana message** (NOT a full transaction, NOT base64):
  the wire-format message = `[header (3 bytes)][account keys][recent blockhash (32 bytes)]
  [compiled instructions]`, serialized with the standard short-vec (compact-u16) length
  prefixes, exactly the byte sequence a Solana validator hashes and that clients sign.
- The signer returns the **fully-assembled submittable wire transaction** —
  `[compact-u16 signature count][64-byte Ed25519 signatures...][message bytes]`,
  produced by `Transaction.Populate(message, [signature]).Serialize()`. The provider
  does **NOT** reassemble anything: it base64/base58-encodes the returned bytes as-is
  and submits them straight to the RPC `sendTransaction` boundary.
- The signing key is the fee-payer / first required signer; the provider MUST place that
  pubkey first in the message account-keys so signature order matches. The signer
  fails closed if `Message.AccountKeys[0]` is not the signing key's public key.

Until a real Solana pipeline is built to this contract, Solana implements no bridge leg.

### Conformance (FR-6) — the gate a route passes before it is enabled

`tests/AZOA.WebAPI.Tests/Conformance/` is a shared, provider-agnostic battery that EVERY concrete
`IBlockchainProvider` in this assembly is run through. Enrollment is by reflection: adding a
provider class enrolls it, and a provider the harness cannot construct fails by name. There is no
opt-in and no opt-out, which is the point — a chain added later inherits the whole battery instead
of relying on someone remembering to copy the tests.

It certifies eight invariants this codebase has actually violated: no fabricated success, unknown
is never Confirmed (including the vacuous-null trap), non-retrying broadcast, confirm-timeout is a
pending marker rather than a failure, per-user signing never falls back to a platform key, amounts
round-trip past the old `ulong` ceiling, capability honesty, and fail-closed errors that name the
real cause.

It does NOT duplicate the per-chain pipeline suites (`Signing/AlgorandProviderTransactTests.cs`,
`SolanaProviderSplPipelineTests.cs`, `EthereumProviderPipelineTests.cs`, `BridgePrimitivesTests.cs`).
Those drive real build→sign→submit→confirm pipelines against chain-specific stubs and remain the
decisive evidence for anything that needs a blockhash, a nonce or suggested params. Where the
shared battery cannot reach such a state, that is recorded as a declared verdict with the suite
that carries the proof — never a silent skip. See `tests/AZOA.WebAPI.Tests/Conformance/AGENTS.md`
for the enrollment mechanism and the exemption policy; `Certification_report` prints the matrix.

### Security invariants (reviewer checklist)
- No fabricated-success return on any lock/burn/mint/release value path.
- A chain without a reviewed lifecycle implements NO leg — never a leg with a refusal body.
- No always-true verifier anywhere (provider-level verify removed; the protocol path is the
  only verifier and is fail-closed).
- Missing signer/config ⇒ explicit error, never a silent fake-Ok.
- Real tx hash (from `BuildSignSubmitAsync`) is returned on success, not a synthetic op-id.
- No double-broadcast: broadcast step is non-retrying; confirm-timeout ⇒ pending marker.
- Preconditions the chain enforces (opt-in, full-supply-for-destroy, vault control) are READ
  from chain state, never assumed.

## §value-rail-factory — registering a rail that is not a chain (D9)

`IBlockchainProviderFactory` now extends `IValueRailProviderFactory`. The split exists
because the chain-typed factory made a non-chain rail **type-impossible** to register:
registration was `Func<IBlockchainProvider>`, resolution returned `IBlockchainProvider`, and
`ResolveEnabledNetworkConfig` demanded a `ChainNetwork`. A fiat rail is not a chain provider
and has no Devnet.

Both blockers are gone:

- `ValueRailProviderRegistration(railId, Func<IValueRailProvider>, resolveEndpoint)` registers
  any rail. `BlockchainProviderRegistration(chainType, Func<IBlockchainProvider>)` is a
  subclass with `IsChainRail = true` and no endpoint resolver — every existing call site is
  source-compatible, since `Func<T>` is covariant in `T`.
- A chain rail resolves its endpoint from `Blockchain:Chains` exactly as before. A non-chain
  rail brings its OWN `RailEnvironment -> BlockchainNetworkConfig?` resolver, because that
  config section is keyed Devnet/Testnet/Mainnet. Returning null for an environment means
  "disabled for this rail".

`GetProvider(chainType, ChainNetwork)` still returns `IBlockchainProvider` and throws a clear
error naming `IValueRailProvider` if asked for a rail that is not a chain. Instances are
cached per (rail, environment **name**) rather than per `ChainNetwork`, so a rail whose
environments are not chain networks still gets one independent instance per environment.

Binding order in `CreateInitializedProvider` is narrowest-first: `BaseBlockchainProvider` gets
the factory-only entry point that also marks the instance non-rebindable; any other chain
provider gets the chain-shaped `Initialize`; only a rail that is not a chain gets
`InitializeRail`.

Simulated mode redirects only CHAIN rails to `SimulatedBlockchainProvider`. A non-chain rail
is left alone: a simulated blockchain is not a stand-in for a payment processor, and treating
it as one would fake money.

`GuardAgainstUnregisteredEnabledChains` is unchanged and must stay that way. It reads
`Blockchain:Chains` DIRECTLY and diffs against the registered chain types, deliberately not
via `GetAllEnabledProviders`, which silently skips unregistered chains and would make the
exact defect the guard exists to catch invisible. Its registered-chain list in `Program.cs`
(`registeredBlockchainChainTypes`) must stay in sync with the registration array beneath it;
`ValueRailSeamTests` asserts both directions against the real shipped `appsettings.json`.

`BlockchainProviderNotFoundException` now derives from `ValueRailProviderNotFoundException`,
so every existing `catch` keeps working.

## §client-signed transfers (D-3, round 4)

`ClientSigning/IClientSignedTransferModule` is implemented by Algorand, Ethereum and Solana
(`*Provider.ClientSigning.cs`). Plans are built by the same code paths custody uses (Solana shares
`PlanValueMessageAsync` with `PrepareValueTransferAsync`), never signed or sent at plan time, and every module re-derives
the plan from its recovery fields before trusting it. See `Services/Blockchain/AGENTS.md` §client-signed operations.

- **Ethereum nonces (RV-L5-01/-05).** A plan reserves its own signer nonce in `ethereum_preparation` (see
  `Ethereum/AGENTS.md` §client-signed nonces); submit and every hash attribution re-prove that reservation.
- **Solana absence (RV-L5-02).** `Expired` comes only from a complete finalized-ancestry scan (the reviewed WTT
  `ScanWttMessageHistoryAsync`) that starts at the first finalized block past the blockhash's last valid height and walks
  parent links to the plan's context slot, 32 blocks per reconcile pass, resuming from the cursor the observation returns
  (`ClientSigningObservation.EvidenceJson`, persisted by the service). A null signature status or a short
  `getSignaturesForAddress` page proves nothing. A node without that ledger history yields `Unproven`, which parks the
  row for an operator and never settles it.

## §module activation (D-7, round 4)

Algorand declares `IDexTransactionModule` and `IExchangeTransactionModule`; Ethereum and Solana declare
`IDexTransactionModule`. Declaration is not activation: each provider re-maps `IBlockchainProvider.TryGetModule<T>`
through `ChainModuleActivation`, which advertises a DEX or exchange module only where
`{Dex|Exchange}:Activation:{Chain}:{Network}` is explicitly `true` (`BlockchainProviderFactory.TryGetModule` delegates to
the provider so the pin applies on every path). `appsettings.Development.json` turns the devnet pins on for fixtures;
production stays off until that chain's protocol fixture passes in the sweep; anything still off at the end goes to
`deferred.md`.

- **Admission only (RV-L5-11).** A pin gates new admissions (DEX quotes, the swap node's admission check, capability
  discovery). Work already admitted resolves its module through `DexModuleResolver.ResolveForExisting` /
  `ChainModuleActivation.TryGetUngated`, so turning a pin off never strands in-flight recovery, stage progress or cleanup.
- **Deployed hosts fail closed.** The Dockerfile pins `ASPNETCORE_ENVIRONMENT=Production`, but a Railway service variable
  can override it, and a deployment running as Development would inherit the development pins. `HostHonoursPins` therefore
  ignores every pin (and `Wtt:Enabled`) on a host carrying Railway's injected `RAILWAY_PROJECT_ID`/`RAILWAY_ENVIRONMENT_ID`
  unless `AZOA:Activation:DeployedHostAcknowledged=true` is also set there explicitly.

## §rpc-http-clients (D9, round 4)

`ChainRpcHttp.Create` builds every chain RPC client: an explicit handler (unit tests) wins, then the host's
`IHttpClientFactory` named client `chain-rpc:{chain}` (production constructors take the factory), else a bare client.
Fake-RPC flows configure the named client instead of reflection. Never put a node URL (it can embed a provider API key)
into a result, log message or chain-info dictionary — `GetChainInfoAsync` no longer returns `rpcEndpoint`.
`ChainRpcHttp.AddChainRpcClients` registers every `chain-rpc:{chain}` client with `RemoveAllLoggers()` (the factory's
default handler logs request URIs at Information, which would print QuickNode-style `/TOKEN/` paths, RV-L5-10), and any
log line that names a node uses `ChainRpcHttp.LogSafeEndpoint` (scheme, host and port only).
