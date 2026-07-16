# Response contracts

## AZOAResult factories

Expected success and failure paths use `AZOAResult<T>.Success` and `.Failure`
instead of repeating private static object-initializer helpers in managers and
stores. `CaptureException` remains available only at a true exception boundary;
ordinary persistence methods let unexpected exceptions reach central logging.

## public node transparency

`NodeTransparencyResponses` is a purpose-built public projection. Do not replace
it with the operator governance DTOs: those include actor avatar ids and raw JSON
snapshots intended only for `node:govern` principals. Public audit items contain
typed economic fields, versions, and timestamps only. Their opaque cursor is a
transport token, not a record id. `ContentSha256` is a weak semantic ETag,
domain-separated by response kind but not cross-language canonical JSON. Audit
page hashes intentionally exclude the randomized protected cursor encoding;
equivalent tokens represent the same next boundary. The hash is not a signed
claim that the stored history is append-only.
