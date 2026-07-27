# AZOA provider conformance

Use this checklist for every new family, new network profile, or completion of
an existing provider.

## Required descriptor

Record:

- family and canonical chain type
- canonical network/profile names
- public chain identity, provider/signing family, environment, and CAIP-2 or
  equivalent protocol identity as separate fields
- chain ID and genesis/network identity
- RPC/indexer endpoints and required environment-variable names
- native asset identifier, symbol, and decimals
- address and signature schemes
- persisted signing-key representation
- fee and nonce/sequence model
- confirmation, finality, and reorganization policy
- explorer transaction/address templates
- supported capabilities in .NET and SDK
- bridge protocol chain ID, vault, wrapped-asset authority, and deployment IDs,
  when bridge support is intended

Store values with the provider family. Configuration may supply endpoints,
credentials, and deployment addresses, but must not redefine protocol constants
or contain raw private keys.

## Ownership gate

Reject the change if it introduces chain-specific switches, SDK imports, RPC
methods, identifiers, decimal constants, address codecs, transaction parsing, or
finality logic in generic Controllers, Managers, Services, Helpers, Core, or
frontend components.

Allowed specialized boundaries are the blockchain provider family, its SDK
mirror, a provider-owned module, or a named DEX/bridge adapter whose role is
itself chain-specific.

Generic services may consume typed provider results and capability descriptors.
They must not interpret chain-specific dictionary keys.

## Registration and network gate

Require:

- exactly one registration for each enabled family/profile
- transient provider construction through `BlockchainProviderFactory`
- immutable canonical `(chainType, network)` binding
- startup failure for enabled configuration without a provider
- startup failure for an advertised signing/value capability without its module
- new live networks disabled by default
- remote node chain/genesis identity checked during initialization
- no private key, mnemonic, or seed in provider configuration or logs
- signer lookup keyed by the signing family rather than copied per compatible
  public chain

## Key and transaction gate

Pin official golden vectors for:

- valid and invalid addresses
- key generation and mnemonic restoration
- persisted-key-to-signer reconstruction
- signature verification and wrong-key rejection
- canonical unsigned transaction/message bytes
- fully signed wire transaction bytes
- transaction hash derivation
- amount zero, maximum, overflow, and values above `2^53`
- values above `ulong.MaxValue` for uint256/EVM families
- fee, nonce/sequence, expiry/blockhash, and network replay protection

Use vetted chain libraries. Do not hand-roll curves, hashes, base encodings,
mnemonics, or wire framing when an audited library exists.

Pin new crypto/chain libraries and review NuGet/npm advisories, package
provenance, lockfile changes, and license compatibility.

Define golden vectors for each signing surface. Browser wallet methods,
personal-message signing, canonical transaction signing, and server custody
must not share an underspecified generic byte-signing contract.

## Capability parity gate

For each shared capability, assert identical .NET and SDK declarations. Mark
server-only or client-only behavior `N/A` explicitly:

| Capability | .NET | SDK | Evidence |
| --- | --- | --- | --- |
| Balance/query | false | false | |
| Faucet | false | false | |
| Native transfer | false | false | |
| Fungible create/mint | false | false | |
| Fungible transfer | false | false | |
| Fungible burn | false | false | |
| Contract calls | false | false | |
| Atomic groups | false | false | |
| Bridge | false | false | |
| Server custody/key generation | false | N/A | |
| Browser-wallet submission | N/A | false | |

Unsupported methods must return explicit errors. No method may fabricate success,
a transaction hash, or an operation ID.

## Bridge gate

Keep `SupportsBridging=false` until tests prove all four real provider
transactions: lock, wrapped mint, wrapped burn, and release.

Also prove:

- platform control of the source vault and wrapped mint authority
- recipient opt-in, trustline, allowance, or token-account prerequisites
- exact amount and asset identity preservation
- real transaction hashes and confirmed submission
- timeout/pending recovery through reconciliation
- no second broadcast after ambiguous submission
- replay protection and duplicate-request idempotency
- explicit failure when signer, vault, deployment, or network config is absent
- finality/reorganization handling
- reverse-route behavior

Do not add provider-level VAA verification. The existing Wormhole adapter and
guardian-quorum verifier remain the sole proof-verification path.

## Documentation gate

Update the nearest directory `AGENTS.md` when ownership, wire contracts, key
representations, network binding, or recovery behavior changes. Put rationale
there and keep implementation comments to one-line local facts.

Preserve `WalletBootstrapIdentity` v1 output or provide an explicit migration,
because those values participate in persisted wallet identity.

When provider-owned key modules are added, update
`Services/Signing/AGENTS.md` to describe generic encryption, custody, and
dispatch rather than centralized chain-specific key generation.

## Final integrated sweep

Run only after all edits are complete.

```powershell
dotnet restore azoa.sln
dotnet build azoa.sln --configuration Release --no-restore
dotnet test tests/AZOA.WebAPI.Tests/AZOA.WebAPI.Tests.csproj --configuration Release --no-build
dotnet test tests/AZOA.WebAPI.IntegrationTests/AZOA.WebAPI.IntegrationTests.csproj --configuration Release --no-build --filter "Category!=Chaos&Category!=Perf"

Push-Location sdk/azoa-wallet
npm ci
npm run build
npm run typecheck
npm run lint
npm test
Pop-Location

Push-Location frontend
npm ci
npm audit --audit-level=low
npm run lint
npm run typecheck
npm run build
Pop-Location
```

Also run the repository's NuGet advisory/license gate and SDK/frontend package
audit. If no automated license gate exists, record the dependency and license
review in the active Conductor track before capability activation.

The integration suite requires the repository's SurrealDB v3.1.4 test
environment. If it is unavailable, report the integration gate as unverified.
Do not replace it with weaker mocks.

## Handoff

Report:

- family versus network-profile decision
- public-chain/family/environment/protocol identity mapping
- provider-owned files added or changed
- .NET/SDK capability matrix
- registration/config parity
- golden-vector sources
- real testnet transaction IDs for every advertised value capability
- bridge quartet evidence or the reason bridge remains false
- final sweep results and environment-blocked gates
