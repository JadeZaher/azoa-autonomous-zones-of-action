# contracts/ethereum/scripts

## Local chain (`hardhat-network.mjs`)

Every local EVM — the contract unit tests (`test/*.test.mjs`), `deploy-local.mjs`, and the .NET protocol
fixtures (`tests/AZOA.WebAPI.IntegrationTests/Fixtures/Ethereum/*.mjs`) — runs on the Hardhat 2.26.3 network
loaded in-process. Ganache 7.9.2 was removed (round 4 L6): it rejects every EIP-1898 block parameter
(`{blockHash, requireCanonical}`) that the DEX and WTT finalized reads depend on; Hardhat honours them and the
`finalized` tag (probed by spawning both fixtures and calling `eth_getCode`/`eth_getBalance` with
`{blockHash: finalized.hash, requireCanonical: true}`).

- `hardhat.local.config.cjs` holds no keys. `hardhat-network.mjs` generates random per-process private keys,
  passes them through a process env variable that it deletes right after Hardhat loads, and never writes them
  to disk. Keys belong to no public network.
- `localChain()` returns a fresh EIP-1193 provider with its own genesis on every call
  (`createProvider` over the loaded config), so each test fixture starts from block 0.
  `serveLocalChain()` exposes one over loopback HTTP (`JsonRpcServer`, port 0 = random).
- Hardfork stays `shanghai` to match the compiled bytecode (`build.mjs` evmVersion).
- Hardhat uses internal module paths (`hardhat/internal/...`); they are stable inside the exact 2.26.3 pin.
  Bumping Hardhat means re-checking both paths. Hardhat 3 is a rewrite — not a drop-in bump.

## Lockfile audit (round 4 L6)

- Uniswap: `@uniswap/v3-core@1.0.1` (BUSL-1.1) and `@uniswap/v3-periphery@1.4.4` (GPL-2.0-or-later) with
  transitive `@uniswap/lib@4.0.1-alpha`, `@uniswap/v2-core@1.0.1` and a nested
  `@openzeppelin/contracts@3.4.2-solc-0.7`. All are devDependencies with lockfile integrity hashes; only their
  published JSON artifacts are deployed, and only on the loopback test chain. Nothing from them is compiled into
  the production contracts (`src/` uses `@openzeppelin/contracts@5.4.0`).
- `npm audit --omit=dev`: 0 after pinning `ethers@6.17.0` (6.15.0 pulled `ws` with GHSA-58qx-3vcg-4xpx /
  GHSA-96hv-2xvq-fx4p).
- Remaining advisories are Hardhat 2.x dev-tree only (`undici`, `adm-zip`, `mocha`, `@sentry/node`, `tmp` via
  `solc`, …); the only offered fix is Hardhat 3. They affect a loopback-only test tool, never the API, SDK or
  frontend. Revisit when moving to Hardhat 3.
