# Observability

`OpenTelemetryExtensions` owns trace and metric registration plus W3C request
correlation. Keep exporter configuration optional so a missing collector never
prevents startup. Domain code should create spans only for meaningful operations
and attach stable identifiers, never credentials, addresses containing private
material, raw payloads, signing data, query text, or exception messages. Failure
spans carry error status plus exception type only.

Exception logging policy and file-sink configuration live in
`Core/Diagnostics/AGENTS.md`. OpenTelemetry currently exports traces and metrics;
structured exception logs remain on the standard `ILogger` pipeline.
