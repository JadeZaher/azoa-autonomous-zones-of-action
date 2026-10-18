# Local Solana protocol gate

This image uses the official Anza Agave v3.1.8 Linux release and its published SHA256,
recorded in `.omc/research/agave-validator-assets-2026-10-15.md`. It is a local image,
not an asserted upstream Docker Hub tag. Build and execution belong to the final
integrated sweep.

```powershell
podman build -t localhost/azoa-solana-validator:agave3.1.8 -f tests/AZOA.WebAPI.IntegrationTests/Fixtures/Solana/Containerfile tests/AZOA.WebAPI.IntegrationTests/Fixtures/Solana
podman run -d --name azoa-solana-protocol --publish 127.0.0.1:18899:8899 --publish 127.0.0.1:18900:8900 localhost/azoa-solana-validator:agave3.1.8 --ledger /ledger/state --rpc-port 8899 --bind-address 0.0.0.0 --url https://api.devnet.solana.com --clone-upgradeable-program metaqbxxUerdq28cj1RbAWkYQm3ybzjb6a8bt518x1s
$env:AZOA_SOLANA_VALIDATOR_URL = 'http://127.0.0.1:18899'
```

The clone reads the public deployed Metaplex program; no remote wallet, signing key,
airdrop or payment is used. All transactions and faucet funds are local. Record the
cloned program data hash/container logs with test evidence. Existing container names
must be inspected and reused or changed; no volume reset/delete is required.

Official reference: https://docs.anza.xyz/cli/examples/test-validator and
https://github.com/anza-xyz/agave/releases/tag/v3.1.8. Protocol tests require this
validator; missing setup is a reported gate failure, not a silently skipped test.

## Jupiter / Whirlpool clone (`start-jupiter-validator.mjs`)

Same Agave image, separate container (`azoa-solana-jupiter`, loopback `18999`), cloned from **mainnet**
(Jupiter v6 exists only there). `jupiter-whirlpool-pin.json` (captured 2026-10-17, slot 458147380) pins:
- `JUP6LkbZbjS1jKKwapdHNy74zcZ3tLUZoi5QNyVTaV4` and `whirLbMiicVdio4qvUfM5KAg6Ct8VwpYzGff3uctyCc`
  (`--clone-upgradeable-program`) with their programdata address, last deploy slot and SHA-256 of the executable
  bytes. After start the script re-hashes the cloned programdata; a mismatch means mainnet upgraded the program
  since review and the fixture fails — re-pin only after re-reviewing `JupiterRouteCodec`/Whirlpool layouts.
- one pool: SOL/USDC Whirlpool `HJPjoWUr…ngndJ` (tick spacing 64) + config, both mints and vaults
  (`--clone`), and its Oracle PDA (`--maybe-clone`; absent for non-adaptive-fee pools).
- tick arrays are derived at clone time (Solana `find_program_address`, seeds `tick_array`/pool/start index) from
  the pool's current tick, current ±2 arrays, because mainnet price moves after the pin. The derivation was
  checked against mainnet: all five arrays at capture time exist on chain.

`--dry-run` prints the container command without starting anything. Pool *state* is whatever mainnet holds at
clone time (never pinned); only program bytes and pool identity/layout are pinned. Jupiter's hosted quote/Build
API cannot see a local validator, so the protocol test must drive on-chain execution of the provider-compiled
message against these clones; until that test is green Jupiter stays pinned off (D-7).
