# Shared helpers

Helpers in this directory are pure, reusable application-boundary conversions;
domain policy stays in its owning manager or service. `SurrealRecordGuid`
normalizes either a bare GUID/hex id or a `table:id` link so manager response
mappers do not maintain private copies of transport parsing. `BareId` also
normalizes SurrealDB's backtick/double-quoted textual-id rendering before it is
used in a typed record comparison or GUID conversion.
