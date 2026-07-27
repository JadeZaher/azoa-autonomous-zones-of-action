---
name: add-azoa-chain-provider
description: Add, complete, or extend an AZOA blockchain provider or network profile while keeping all chain-specific logic and constants provider-owned. Use for new blockchain families, EVM-compatible network profiles, provider and SDK parity work, chain key/signing support, or bridge capability implementation and conformance.
---

# Add an AZOA chain provider

## Load the repository contract

Read these files completely before editing:

- `AGENTS.md`
- `Providers/Blockchain/AGENTS.md`
- `Interfaces/AGENTS.md`
- `Services/Signing/AGENTS.md`
- `Helpers/AGENTS.md`
- `tests/AZOA.WebAPI.Tests/Architecture/AGENTS.md`
- the target provider's nearest `AGENTS.md`, when present
- `references/provider-conformance.md`

Read official primary documentation for the chain, SDK, transaction wire format,
address/signature scheme, network identity, finality model, and bridge contracts.

Do not add federation packages, protocols, endpoints, or dependencies.

## Classify the addition

Choose exactly one path:

1. Add a network profile to an existing family when address, signing,
   transaction, token, and RPC semantics are shared.
2. Add a new provider family when any of those semantics materially differ.
3. Complete an existing fail-closed provider before creating a parallel family.

Prefer one family with multiple immutable network profiles. Do not duplicate an
EVM-compatible provider merely to add another EVM chain. Creating a new family
normally includes its first network profile; that is still path 2.

Record four distinct identities when applicable:

- public chain identity, such as `Base`
- provider/signing family, such as `Evm`
- environment/profile, such as `Testnet`
- protocol identity, such as CAIP-2 `eip155:84532`

Do not overload one `ChainType` value to mean all four. Define the typed
descriptor and factory/signer lookup behavior before adding profiles.

## Preserve provider ownership

Keep every chain-specific SDK type, RPC method, chain/network identifier, genesis
identifier, native asset constant, decimal count, address codec, key format,
signature rule, fee rule, finality rule, transaction codec, explorer format, and
bridge-chain constant inside the owning provider family and its SDK mirror.

Keep generic orchestration outside providers:

- Keep bridge VAA verification on the existing hardened Wormhole service path.
  Never add `VerifyBridgeProofAsync` to `IBlockchainProvider`.
- Let a provider-owned bridge module build and submit that chain's lock, wrapped
  mint, wrapped burn, and release transactions.
- Keep `WalletKeyService` generic for encryption/storage and dispatch key
  generation through a provider-owned key module.
- Preserve existing `WalletBootstrapIdentity` v1 identifiers unless an explicit
  identity migration is designed and tested.
- Put chain-specific DEX behavior in a specialized provider/adapter, not Core or
  a generic service.
- Make controllers and frontend consumers enumerate provider descriptors rather
  than switch on chain names.

Use integer base units covering the chain's complete native range. Use .NET
`BigInteger` or canonical decimal strings and TypeScript `bigint` or decimal
strings for uint256/EVM values; `ulong` is insufficient. A fixed-width unsigned
integer is allowed only when the chain protocol itself has that exact bound.
Never use floating-point amounts or JavaScript `number` for base units.

## Define capabilities before implementation

Write a capability matrix for both .NET and `sdk/azoa-wallet`. Mark unsupported
capabilities false and fail closed.

Do not advertise a capability based on a placeholder, synthetic operation ID, or
mock-only test. A successful value operation must return a real transaction hash.

Keep shared .NET and SDK capability declarations identical. Represent
intentionally server-only or client-only behavior as explicit `N/A`, not false
parity—for example server custody/key generation versus browser-wallet signing.
Configure a new provider disabled by default until its conformance and
live-network evidence are complete.

## Implement through established seams

1. Define or extend authoritative contracts under `Interfaces/`; document
   semantics there and use `inheritdoc` in implementations.
2. Add family-owned implementation and descriptors under
   `Providers/Blockchain/<Family>/`.
3. Add provider-owned key/address/signing or transaction-codec modules as needed;
   route private-key use through custody and zeroable byte buffers.
   Define the exact signing contract per execution surface; raw-byte signing,
   personal-message signing, transaction signing, and wallet-submitted
   transactions are not interchangeable.
4. Register a transient provider creator with `BlockchainProviderFactory`.
   Preserve immutable `(chainType, network)` instance binding.
5. Add configuration for every network profile without raw private keys.
6. Add the mirrored SDK provider under `sdk/azoa-wallet/src/<family>/`.
7. Expose provider/network metadata through the registry/API; do not expand
   hardcoded controller or frontend chain lists.
8. Add focused conformance, golden-vector, architecture, and parity tests.
9. Add or update the nearest directory `AGENTS.md` for rationale; keep source
   comments terse. When provider-owned key modules are introduced, update
   `Services/Signing/AGENTS.md` so `WalletKeyService` becomes a generic
   encryption/custody dispatcher rather than the owner of chain key generation.
10. Update `provider-boundary-conformance` for shared seam work and the
    applicable per-chain activation track for value capability work. Create a
    new activation track when no truthful one exists. Preserve OKF frontmatter
    on every touched file under `conductor/`.

## Enforce bridge safety

Leave `SupportsBridging` false unless all four provider primitives are real:

- lock native/source asset
- mint wrapped asset
- burn wrapped asset
- release native/source asset

Require real broadcast hashes, vault/mint-authority ownership, recipient
opt-in/allowance handling, confirmation and finality, pending-confirmation
recovery, replay protection, and focused failure tests.

Verified protocol chain IDs, address codecs, and configuration-key schemas may
land while bridging is false. Deployment addresses, vaults, mint authorities,
and relayer credentials remain external, empty/disabled configuration until
testnet evidence exists.

Never retry a broadcast after an ambiguous post-send result. Persist the known
transaction identity and reconcile from chain state. Never weaken AZOA's
idempotency, replay-ledger, or exactly-once assertions.

For Solana, obey the canonical-byte contract in
`Providers/Blockchain/Solana/AGENTS.md`: the provider compiles the serialized
message; the signer returns the complete wire transaction.

## Validate once at the end

Apply all implementation fixes before running tests. Do not use a
test-fix-test loop. A single focused run is allowed only when the test harness
itself changes.

Run the final integrated sweep in `references/provider-conformance.md` once. If
an environment-dependent gate cannot run, report it as unverified rather than
weakening or skipping its assertion.

Finish with the capability matrix, live-network evidence, exact commands run,
and every capability that remains fail-closed.
