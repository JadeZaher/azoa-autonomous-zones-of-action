# Architecture ratchets

`CodeStyleDebtBudgetTests` is a non-regression ceiling, not an endorsement of
the current debt. It scans the production source roots and rejects new raw
mutations, dynamic query construction, or blanket `catch (Exception)`. It also
keeps a per-file raw-query/catch ceiling for Surreal stores. A migration may
lower or remove an entry; raising a ceiling requires an active track waiver and
secondary review.

The raw-mutation scanner recognizes ordinary, verbatim, and raw string literals
whose first mutation is `CREATE`, `UPSERT`, `UPDATE`, or `DELETE`, including a
mutation assigned through a leading `LET`. Multi-table transaction components
are counted too: their atomicity may justify a retained entry, but it does not
exempt them from the inventory.

## Federation boundary ratchet

`FederationBoundaryTests` keeps the activation gate enforceable: before recorded,
machine-verifiable activation evidence proves three independent production
operators and a real counterparty use case, the AZOA node must not reference any
`Azoa.Commons.*` or `Azoa.Holochain.*` package, a Holochain runtime/conductor API,
or the prohibited NextGen/HoloNET prior-art client. Post-gate, a reviewed ratchet
change may permit only the thin `Azoa.Commons.Client` facade; Contracts and Runtime
remain prohibited direct references. The runtime remains in the separate
`AZOA.Commons` repository. The normative package and protocol shape lives in
`conductor/tracks/federation-v2/holochain-dotnet-bridge-contract.md`.
The source scan blocks any `Holochain` or `Azoa.Holochain` implementation
namespace, not merely a current allowlist of known runtime types.
