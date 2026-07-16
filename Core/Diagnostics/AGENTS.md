# Diagnostics boundaries

## One owner for unhandled request exceptions

`DebugExceptionMiddleware` is the outer exception boundary. It emits one
`Critical` structured log and shapes the generic HTTP 500 response. The later
`JsonlExceptionMiddleware` observes completed 4xx/5xx responses only; it must not
catch or log exceptions, or each failure is recorded twice.

Lower layers return `AZOAResult<T>` for expected outcomes that callers can act
on, such as validation failures, compare-and-set conflicts, unavailable remote
features, or a provider's documented error result. Unexpected database,
serialization, provider, and programming exceptions bubble to the nearest host
boundary. Request middleware, hosted-service loop boundaries, and command-line
entrypoints are the places that log unexpected exceptions. When touching a
method, prune blanket `catch (Exception)` blocks that merely copy `ex.Message`
into an `AZOAResult<T>`; retain narrow catches only where the exception is a
documented, expected domain outcome.

Anonymous evidence endpoints can opt into `SuppressDebugExceptionDetails`.
`DebugExceptionMiddleware` still records the original exception once at
`Critical`, but its wire response stays generic even when process-wide debug
responses are enabled. Use this marker only for public surfaces whose error body
must never reveal infrastructure detail.

## Severity and sinks

.NET `Logging:LogLevel` is the common severity knob. The base configuration is
the medium-volume `Information` default, Development emits `Debug`, and
Production emits `Critical` only; unhandled exceptions are always `Critical`.
Console logging is the production default and carries W3C trace scopes.

The JSONL sink is enabled in Development and Production. Production records only
`Critical` entries by default; disable it explicitly when the platform's console
collector is the sole durable sink. Configure it through
`Diagnostics:JsonlExceptionLogger` or environment variables:

- `Enabled` activates the sink.
- `Directory` selects an absolute or application-relative path.

Do not add a sink-specific severity setting. `Logging:LogLevel` filters every
registered provider consistently, including JSONL.

The writer uses bounded admission and drains every accepted entry during a
normal host shutdown. A full or closed queue rejects the new entry explicitly;
it never evicts an accepted older entry. Queue, write, and forced-drain failures increment
`JsonlExceptionWriter.FailureCount` and emit a generic message directly to
standard error so reporting cannot recurse through `ILogger`. Entry-size
fallbacks are re-serialized summaries (or `{}` for an unusually tiny limit),
never sliced JSON text, so each persisted line remains valid JSON.

The provider consumes external scopes, so request id, method, path, status,
trace id, and span id survive into JSONL records. Redaction applies to configured
structured values, JSON-shaped messages, inner chains, and captured Surreal
parameters. Do not put secrets in exception types, stack-frame names, plain-text
exception messages, or unstructured scope values.

## OpenTelemetry

OpenTelemetry exports ASP.NET Core, HTTP-client, and custom AZOA/Surreal traces
and metrics. It does not replace `ILogger` exception records. Configure the OTLP
endpoint and protocol under `OpenTelemetry:Otlp`; leave the endpoint unset when
no collector is available. Export exception type and error status, but never an
exception message, request body, query text, or parameter value as a span tag.
