# Native AVM protocol fixture

The checked-in `compose.yml` and companion templates come from official AlgoKit
[v2.10.2 sandbox.py](https://github.com/algorandfoundation/algokit-cli/blob/v2.10.2/src/algokit/core/sandbox.py).
All four images are pinned by immutable manifest digest, captured from their
publisher Docker Hub API on 2026-10-15: stable algod, indexer 3.10.0, the official
conduit-localnet image, and PostgreSQL 16 Alpine used only by the protocol indexer.
AZOA platform persistence remains SurrealDB. Source captures and full registry
metadata are under `.omc/research/algorand-native-*pin*2026-10-15.json` and
`algorand-official-image-manifests-2026-10-15.json`.

The project and container names are dedicated to this fixture, and every published
port binds numeric loopback. Do not redirect to a public network, reset an existing
developer ledger, or update image pins implicitly. Start or reuse this isolated
stack only during the final integrated sweep and record its actual genesis.
The upstream `NUM_ROUNDS` template placeholder is intentionally preserved for the
official algod image initialization. No fixture service has been started yet.

```powershell
podman compose -f tests/AZOA.WebAPI.IntegrationTests/Fixtures/Algorand/compose.yml up -d
$env:AZOA_ALGORAND_LOCAL_ALGOD = 'http://127.0.0.1:4001/'
$env:AZOA_ALGORAND_LOCAL_INDEXER = 'http://127.0.0.1:8980/'
$env:AZOA_ALGORAND_LOCAL_TOKEN = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$headers = @{ 'X-Algo-API-Token' = $env:AZOA_ALGORAND_LOCAL_TOKEN }
$env:AZOA_ALGORAND_LOCAL_GENESIS = (Invoke-RestMethod -Uri ($env:AZOA_ALGORAND_LOCAL_ALGOD + 'v2/transactions/params') -Headers $headers).'genesis-hash'
```

Set `AZOA_ALGORAND_LOCAL_MNEMONIC` process-locally to an account funded on this
LocalNet, using the local wallet export tools. It must hold at least five ALGO.
Never print, persist in the repository, or substitute a public-network mnemonic.
The test verifies loopback and the separately pinned genesis before reading this
key or signing. Provider custody receives only the fixture user's explicit native
scope; platform custody is refused. The setup token above is the standard local
development token, not a production credential.

The application is compiled through the real algod TEAL endpoint. Typed native
create/call preparation uses the real SurrealDB journal and production provider.
A direct pinned-SDK update is deliberate test interference: both programs accept
calls, but the adapter must identify the code active at the exact call position.
An earlier call must remain provable after update; wire prepared for old code and
executed after update must not be called confirmed for that artifact. The SDK update
type was checked against the repository's pinned upstream source revision
`cc796c56fddc5849f625a6fb02aabf869a06d6bc`.

An indexer is mandatory. The fixture waits for its actual indexed transaction
before evaluating program history; only exact 404 means not indexed yet. Missing
setup or incomplete evidence fails the gate. Nothing has been executed while
authoring these files.
