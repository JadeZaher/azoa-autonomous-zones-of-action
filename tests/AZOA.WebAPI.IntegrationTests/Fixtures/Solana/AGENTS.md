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
