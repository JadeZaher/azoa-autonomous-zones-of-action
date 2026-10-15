# Shared helpers

Helpers in this directory are pure, reusable application-boundary conversions;
domain policy stays in its owning manager or service. `SurrealRecordGuid`
normalizes either a bare GUID/hex id or a `table:id` link so manager response
mappers do not maintain private copies of transport parsing. `BareId` also
normalizes SurrealDB's backtick/double-quoted textual-id rendering before it is
used in a typed record comparison or GUID conversion.

`NftHolonFactory` is the shared pure mapping for guarded NFT metadata writes.
It contains no KYC, governance, fee, persistence, or operation policy; callers
must make those decisions before persisting its result.

`WalletBootstrapIdentity` is the shared canonical chain normalizer and
SHA-256-derived wallet id used by both wallet creation and tenant status reads.
Changing its v1 input is an identity migration, not a formatting refactor.

## §rail-amount-json

`RailAmountBaseUnitStringJsonConverter` is the HTTP/replay boundary converter for
base-unit amounts. It always writes a decimal JSON string so JavaScript clients
never round values above `Number.MAX_SAFE_INTEGER`; numeric reads remain accepted
only for compatibility with already-materialized internal payloads, and only up to
64 bits — a producer that emitted a larger JSON *number* has already lost
precision in its own number type, so the string form stays the contract.

It replaced `UlongDecimalStringJsonConverter` in the D13 widening (`Core/AGENTS.md`
§rail-amount). The wire form is byte-identical, so no client saw a change; only the
in-process type widened from `ulong` to `RailAmount`. The old converter was deleted
rather than kept, because a converter for the retired type invites a new amount
field to be typed `ulong` again.
