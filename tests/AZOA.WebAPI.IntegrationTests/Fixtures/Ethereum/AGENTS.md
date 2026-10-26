# Local Ethereum protocol fixture

`chain.mjs` starts a fresh Hardhat 2.26.3 chain (`hardhat-chain.mjs` over `contracts/ethereum/scripts/hardhat-network.mjs`, random accounts, see `contracts/ethereum/scripts/AGENTS.md`) on a randomly assigned loopback port and deploys the compiled production `AzoaNft` artifact. Build that artifact with `npm ci` and `npm run build` in `contracts/ethereum` before the final integration sweep. The fixture fails if prerequisites are absent; it never substitutes mocked RPC responses.

The C# parent consumes generated local keys through a private process pipe, uses real custody signing and an isolated SurrealDB preparation store, and closes the child process when finished. Keys are ephemeral and do not belong to any public network. No application configuration is changed. Hardhat finality is local test finality; public-network reorganization behavior remains covered separately by observation tests.

Assertions cover byte-identical preparation after provider reconstruction, no egress before authorization, native value settlement, NFT ownership and exactly-once native fees. Keep runtime verification status in the active Conductor track.

Child stderr is consumed continuously through a fixed-size buffer and never printed or retained; startup diagnostics expose only its character count. Initialization failure uses the same child/connection/namespace cleanup as normal disposal. The handshake containing ephemeral keys stays confined to the parent pipe.

## DEX protocol chain

`dex-chain.mjs` starts its own loopback Hardhat chain, verifies the pinned WETH9 artifact digest (over the committed LF bytes, so a Windows autocrlf checkout still matches), deploys the
pinned Uniswap V3 factory/router/quoter/position-manager npm artifacts, two `DexTestToken` instances with one
initialized full-range 0.3% pool, and the compiled production `AzoaDexExecutor` with a dedicated local policy key.
It reports addresses, deployed runtime code hashes, the genesis hash and ephemeral keys over the same private pipe.
`EthereumDexProtocolTests` drives the production `EthereumProvider` DEX module with real Surreal preparation and
allowance stores: quote, exact approval, policy-permitted swap, finalized settlement grammar, exactly-once replay and
allowance release from the saved confirmed stage. The DEX module pins state reads to finalized block hashes through
EIP-1898 `{blockHash, requireCanonical}` parameters and the `finalized` tag. Ganache 7.9.2 rejected the former, so
both fixtures moved to Hardhat 2.26.3, which honours both (probed on this tree; shared with the WTT Ethereum reads).

## Fixture custody must zero a copy of the key

Nethereum 4.21 `EthECKey.GetPrivateKeyAsBytes()` returns the key's internal array, not a copy. The fixtures' local
`IKeyCustodyService` zeroes the bytes it hands to the provider (as real custody does), so it must zero
`GetPrivateKeyAsBytes().ToArray()`; zeroing the original destroyed the fixture key after its first signature, and every
later signature failed with "the resolved signing key is not a valid secp256k1 scalar".

## Fixture key export

Hardhat hands over random 0x + 64-hex keys. Nethereum's `EthECKey(string)` stores the scalar as a big integer, and
both `GetPrivateKeyAsBytes()` and `GetPrivateKey()` re-export it minimally — a key whose first byte is `0x00`
(1 in 256 per key) comes back as 31 bytes, and the provider correctly refuses it ("the resolved signing key is 31
bytes, not the 32-byte secp256k1 private key"). `EthECKey.GenerateKey()` keys are unaffected (Nethereum redraws
until the export is 32 bytes), which is why the unit-test fixtures never flaked. Every fixture key export goes
through `EthereumFixtureKey.PrivateKeyBytes` / `PrivateKeyHex`, which left-pad to exactly 32 bytes (never trim) and
always return a fresh copy, so it also satisfies the zero-a-copy rule above. `EthereumFixtureKeyTests` pins a
known leading-zero key through the padding, `SigningKeyMaterial` and the real signer back to its own address.
