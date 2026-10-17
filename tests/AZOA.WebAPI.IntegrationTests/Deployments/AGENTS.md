# Native local protocol tests

These tests execute actual loopback chain protocols through production native
modules and `SurrealNativePreparationStore`. RPC, transaction signing and native
module behavior are not mocked. Custody intentionally uses only local test keys;
its helper refuses platform/tenant contexts and requires the exact actor, wallet,
chain and native scope. Native quotes must remain read-only, and replay after
provider reconstruction must return the identical prepared record without signing.

Setup lives in `../Fixtures/Algorand/AGENTS.md` and
`../Fixtures/SolanaNative/AGENTS.md`. Local RPCs require numeric loopback addresses
and separately configured expected genesis values before funding or signing.
They do not inherit any public endpoint from application settings. Each rail owns
an isolated SurrealDB namespace and disposes it when complete. Missing prerequisites
fail explicitly instead of turning the protocol tests into skipped assertions.

The mutable-program tests deliberately change live code after preparation. A
successful transaction alone cannot prove the requested code ran; historical
program identity must match its position in finalized history. Later upgrades
must not invalidate legitimate earlier receipts. Test execution remains reserved
for the final integrated four-round sweep; source authoring is not runtime proof.
