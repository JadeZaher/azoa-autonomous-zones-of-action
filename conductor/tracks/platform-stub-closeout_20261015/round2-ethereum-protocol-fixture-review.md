---
type: review
---

# Local Ethereum protocol fixture source review

Lane A independently reviewed the coordinator-authored EthereumProtocolTests.cs, Fixtures/Ethereum/chain.mjs and its directory notes. No execution occurred.

The fixture deploys the compiled production AzoaNft artifact on an ephemeral loopback Ganache chain, uses actual .NET Ethereum provider signing and isolated Surreal preparation persistence, and reconstructs providers around saved signed wire. Assertions cover no worker egress before admission, native settlement, dual-permit NFT execution, finalized exact observation, ownership and exactly-once fee/value balances. Local finality is not represented as public-network reorganization proof.

Three source findings were reported before correction: missing SurrealTestSchema namespace import, redirected stderr without continuous consumption, and initialization-failure cleanup limited to the child process. Lane A corrected these: explicit import, fixed-size stderr drain retaining only a character count, and shared child/RPC/connection/namespace cleanup with original initialization failures preserved. The coordinator independently reread and accepted those narrow corrections by source. No other concrete blocker was found in this bounded pass. Runtime compilation, local deployment, database persistence and chain assertions remain pending the final integrated sweep.
