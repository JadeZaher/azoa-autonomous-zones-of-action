# Tinyman v2 LocalNet fixture

`validator-pin.json` pins the Tinyman AMM v2 validator program exactly as deployed: approval and clear-state
bytes read from algod `GET /v2/applications/{id}` on **both** mainnet (app `1002541853`, round `66078979`) and
testnet (app `148607000`, round `68364065`) on 2026-10-17 — the two deployments are byte-identical. The pin
records app ids, capture rounds, creators, SHA-256 of each program (`approvalSha256`
`dd63834d…691f56`, `clearStateSha256` `18ae2b5f…03d8e3`), state schemas and extra pages. Raw captures:
`.omc/research/tinyman-v2-validator-{mainnet,testnet}-2026-10-17.json` (local, gitignored).

`deploy-localnet.mjs` (run during the sweep, after `Fixtures/Algorand/compose.yml` is up):
1. re-hashes the embedded program bytes against the pin (tamper check);
2. writes them to `Fixtures/Algorand/goal_mount` (gitignored) and creates the app with `goal app create
   --approval-prog-raw/--clear-prog-raw` inside `azoa_native_avm_algod`, from a funded default-wallet account
   (override `AZOA_TINYMAN_LOCAL_CREATOR`), with the pinned schemas;
3. reads the created app back from LocalNet algod and re-verifies both digests;
4. prints `ValidatorAppId`, `ApprovalSha256`, `ClearStateSha256`, `GenesisHash` — exactly the
   `Dex:Tinyman:Networks:Devnet:*` keys `AlgorandProvider.DexQuote.cs` requires before it will quote.

The pool logic-sig is not stored here: `Core/Blockchain/TinymanV2PoolLocator.cs` splices the validator app id
into the official template, so the LocalNet app id yields LocalNet pool addresses with no extra pin.
Loopback only; refuses a non-loopback algod. Pool bootstrap + liquidity for one pair and the swap itself belong
to the protocol test that consumes this output (sweep, D-7: Tinyman stays pinned off until that test is green).
