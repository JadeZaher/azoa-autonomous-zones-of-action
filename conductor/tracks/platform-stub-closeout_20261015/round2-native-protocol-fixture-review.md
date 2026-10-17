---
type: review
---
# Independent native protocol fixture source review

Reviewer: lane C. Author: lane A. Review is static only; no build, fixture launch, funding or test execution occurred.

Inspected `AlgorandNativeProtocolTests.cs`, `SolanaNativeProtocolTests.cs`, `LocalSolanaNativeRail.cs`, `LocalNativeCustody.cs`, and the SolanaNative/Algorand fixture setup files. The fixtures instantiate production providers and a real SurrealNativePreparationStore, reconstruct providers between preparation attempts, require identical persisted signed wires and no additional custody calls, and then submit/observe actual local protocol transactions. They do not substitute a mock success for provider execution.

The local endpoints require numeric HTTP loopback addresses; expected genesis is provided separately and checked before funding or signing. Platform custody is refused. AVM tests include direct update interference and prove the older call remains attributable while a newly executed stale-code call cannot confirm the selected artifact. Solana tests exercise actual ELF deployment/upgrade/authority removal, old/new code history, ATA creation, metadata mutation/immutability and empty-token-account closure. These are meaningful authored gates, not yet evidence they pass.

Required correction sent to A: SolanaNativeProtocolTests imports both System.Text and Helpers, then uses ambiguous Encoding.UTF8. Qualify BCL encoding and prune unused import. A acknowledged ownership of the fix.

Setup followup: AVM instructions use unpinned `algokit localnet start`. The accepted plan requires a reproducible pinned official setup; A acknowledged this remaining concrete setup item and will resolve with official source evidence. It is not an external blocker or accepted completed gate.

No other concrete source blocker was identified in this bounded review. Runtime compilation/protocol behavior and setup reproducibility remain unverified; this is not approval of lane C's own provider implementation.

## Correction reread

A qualified System.Text.Encoding and removed the ambiguous import. AVM now has dedicated compose/templates sourced from official AlgoKit v2.10.2, immutable image digests, isolated names and numeric-loopback bindings; the docs preserve the upstream NUM_ROUNDS substitution and record source captures. Both requested source corrections are resolved in this bounded reread. Containers, compiler and protocol tests remain unrun; no runtime acceptance implied.
