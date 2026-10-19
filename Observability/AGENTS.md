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

`OpenTelemetry:ServiceName` defaults to `AZOA.WebAPI`.
`OpenTelemetry:ExportEnabled` defaults to `true` and controls only registration
of the OTLP trace exporter and metric reader. Setting it to `false` keeps resource
metadata, activity sources, meters, ASP.NET/HTTP instrumentation and request
correlation available to local listeners. Integration hosts set it before eager
registration so their shutdown never depends on an external collector.
When export remains enabled, `OpenTelemetry:Otlp:Endpoint` and `Protocol` retain
their existing behavior: an omitted endpoint uses SDK defaults or its environment
override, and the application protocol defaults to gRPC (`http/protobuf` opts in).
An omitted endpoint is not a no-op; the SDK defaults to localhost.

These choices follow the packaged OpenTelemetry OTLP exporter 1.15.3 README
(`core-1.15.3`, repository commit `d622e6c7c899527638b53a6b06919859f5752add`):
`AddOtlpExporter` adds a batch trace processor or periodic metric reader, and its
HTTP/protobuf `HttpClientFactory` option supports an in-process transport in
`OpenTelemetryExtensionsTests`. The tests retain local collection and verify
default/explicit enablement versus explicit refusal without a real collector.

## Real-value readiness

`RealValueReadinessHealthCheck` is inert while
`Blockchain:Bridge:RealValueEnabled=false`, which keeps a fresh production node
bootable without custody secrets. Arming real value makes `/health` fail closed
unless the deployment explicitly selects the live Algorand provider and network,
configures a platform mnemonic and valid Algorand vault, and keeps the default
bridge mode on `Trusted`. It also requires an explicitly selected, available KYC
authority and `Kyc:SubmissionExpiryDays > 0`; real value cannot become ready
while approvals would be indefinite or new verification work is unavailable.
The general storage check covers the shared authoritative ledger. Wormhole is
not launch-ready and is not a configurable readiness bypass.
