# Local Ethereum protocol fixture

`chain.mjs` starts a fresh Ganache chain on a randomly assigned loopback port and deploys the compiled production `AzoaNft` artifact. Build that artifact with `npm ci` and `npm run build` in `contracts/ethereum` before the final integration sweep. The fixture fails if prerequisites are absent; it never substitutes mocked RPC responses.

The C# parent consumes generated local keys through a private process pipe, uses real custody signing and an isolated SurrealDB preparation store, and closes the child process when finished. Keys are ephemeral and do not belong to any public network. No application configuration is changed. Ganache finality is local test finality; public-network reorganization behavior remains covered separately by observation tests.

Assertions cover byte-identical preparation after provider reconstruction, no egress before authorization, native value settlement, NFT ownership and exactly-once native fees. Keep runtime verification status in the active Conductor track.

Child stderr is consumed continuously through a fixed-size buffer and never printed or retained; startup diagnostics expose only its character count. Initialization failure uses the same child/connection/namespace cleanup as normal disposal. The handshake containing ephemeral keys stays confined to the parent pipe.
