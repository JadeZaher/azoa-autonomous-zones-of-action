# WTT six-route local protocol fixture

Pinned Wormhole core + token bridge on three local chains with ONE local guardian set, for the six directed routes
ETH→SOL, ETH→ALGO, SOL→ETH, SOL→ALGO, ALGO→ETH, ALGO→SOL (`round3-wtt-contract.md` §Local acceptance).
`run-wtt-fixtures.ps1 -Up` brings everything up and writes `.state/`; `-Down` reaps exactly what it started.
`WttSixRouteProtocolTests` (trait `Gate=WttSixRoute`) reads `.state/` and **fails closed** when it is absent.

## Pins

`wormhole-pins.json` is the single provenance record:

- Upstream `wormhole-foundation/wormhole@bb5957f3…` (tree `6e61447f…`, captured in round 3). Every source file is
  fetched from that commit and verified against its **git blob id** from the pinned tree (`lib/upstream.mjs`); a
  mismatch throws. `node lib/verify-pins.mjs` re-verifies all of them.
- EVM: solc 0.8.4, istanbul, 200 runs, OpenZeppelin 4.3.1 (integrity from upstream's lockfile) — upstream's
  `foundry.toml`/lockfile values. The fixture's own `package.json` + force-added `package-lock.json` pin
  ethers 6.15.0, Hardhat 2.26.3 (EIP-1898 + `finalized`, which Ganache 7.9.2 lacks), solc, OZ, web3.js.
- Solana: canonical program ids; bytes are the read-only equivalent of `solana program dump` (programdata minus the
  45-byte loader header) pinned by sha256, length and deployment slot. **Mainnet binaries are newer than the pinned
  source commit**; the instruction layout used here comes from the pinned SDK/program source and is proven only when
  `initialize` succeeds against the dumped bytes. A program upgrade changes the digest and fails the dump.
- Algorand: pyteal 0.11.1 / py-algorand-sdk 1.10.0b1 via upstream `requirements.txt` (pinned blob) inside
  `python:3.10-slim` pinned by digest; TEAL is compiled by LocalNet algod from the pinned pyteal and its approval
  program sha256 is recorded at deploy and re-checked by the test.

## Guardian

`lib/guardian.mjs`: ephemeral secp256k1 guardian set, VAA v1 encoding, body digest `keccak256(keccak256(body))`,
strictly increasing signer indexes, governance VAAs from chain 1 / emitter `…04`, RegisterChain and Core
GuardianSetUpgrade payloads. `lib/vaa.mjs` is the runner CLI; governance sequences live in `.state/governance.json`.
Private keys only ever sit in the git-ignored `.state/guardians.json`.

## EVM

`evm/chain.mjs compile|deploy|register`: compiles the pinned sources (imports confined to the pinned cache and
OZ), deploys `Implementation`+`Setup`→`Wormhole` proxy and `TokenImplementation`+`BridgeImplementation`+
`BridgeSetup`→`TokenBridge` proxy (pinned WETH9 artifact), records implementation runtime-code sha256, and submits
RegisterChain VAAs, verifying `bridgeContracts(chain)` afterwards. RPC must be numeric loopback.

## Solana

`solana/dump-programs.mjs [--pin]` fetches and verifies the binaries; the runner starts the existing
`Fixtures/Solana` agave image with `--bpf-program` at the canonical ids; `solana/initialize.mjs` sends core
`Initialize` (discriminant 0: expiration u32, fee u64, guardians Vec<[u8;20]>) and token-bridge `Initialize`
(discriminant 0: core pubkey) with the pinned SDK account order, then checks account ownership and stored keys.

## Algorand

`algorand/deploy.py` drives upstream `PortalCore` helpers directly (create core + token bridge apps, `init` from a
local GuardianSetUpgrade VAA). It deliberately bypasses `admin.py`'s boot, which compiles `vaa_verify` on a public
testnet and its devnet branch reads a misplaced NFT metadata file. `algorand/register.py` submits RegisterChain VAAs
through upstream `submitVAA`. LocalNet is the existing `Fixtures/Algorand/compose.yml`.

## Status (round 4)

Authored; pins verified; EVM sources compile; nothing executed against a chain (round-4 rule: no E2E runs).
Not authored, fail closed in the gate with labelled reasons: Solana RegisterChain (verify_signatures + post_vaa +
register_chain sequence) and per-route transfer acceptance through `WttLifecycleService`. See the lane record
`round4-lane-verification-fixtures.md` for the deferred candidates and their unblocks.
