# DEX immutable contracts

Quote terms bind actor/tenant, owner and payer separately, recipient, explicit
chain/network/protocol identity, exact integer amounts, fee denomination/revision,
budgets and provider evidence. All amounts and asset identifiers cross JSON as
strings. The service constructs these terms from authenticated context and checked
provider evidence; client-supplied terms cannot become server-issued quotes.

ProtocolEvidence is provider-owned JSON with a versioned strict schema. It contains
only public protocol/account observations, never raw vendor responses, API keys,
signing contexts or private wire. Signing authority and prepared wire have separate
internal contracts and must not leak into the public operation view. A prepared
stage alone is not permission to submit; the owning operation store must commit
first-send authority under its current action/direct authorization fence.

The closed observation verdicts are Pending, Confirmed, FailedOnChain,
ExpiredAbsent and NeedsOperator. Only Confirmed carries a completed swap's exact
economic evidence. Retained prerequisite effects are distinct from swap success.
The full reviewed contract is `conductor/tracks/platform-stub-closeout_20261015/round3-dex-contract.md`.

`DexCapabilityView` pairs a module's grammar with the provider-read network
identity and a readiness flag; it is discovery, not a liquidity claim.
`SwapActionOutput` is the only workflow-visible swap result and is projected
from the immutable settlement receipt (never from a quote or unsigned wire).
