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
