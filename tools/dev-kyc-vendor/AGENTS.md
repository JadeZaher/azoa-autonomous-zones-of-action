# tools/dev-kyc-vendor — DEV ONLY Identomat sidecar

A zero-dependency Node (>= 20) service that speaks the Identomat vendor protocol the node's
adapters call, so a **Development** node can drive custodial KYC (and tenant KYB) to
`Approved` without real vendor credentials. It is a standalone port of the integration
tests' in-memory fake, `tests/AZOA.WebAPI.IntegrationTests/Http/Infrastructure/IdentomatVendor.cs`
— keep the two in lockstep; the C# fake is the reference.

Written in Node on purpose: the root `AZOA.WebAPI.csproj` globs `**/*.cs`, so a C# service
here would be compiled into the WebAPI.

Run: `node tools/dev-kyc-vendor/server.mjs` · test: `node --test tools/dev-kyc-vendor/server.test.mjs`.
Normally started by `tools/dev-node/dev-node-up.ps1`.

## Protocol map

Two listeners, because the adapter genuinely spans two hosts with different casing:

| Listener (default) | Node setting | Paths |
| --- | --- | --- |
| API host `http://127.0.0.1:8031` | `Kyc:Identomat:BeginBaseUrl` | `POST /begin` (KYC + KYB, by `configId`), `POST /result` (KYB, camelCase), AML: `/search-screening-person`, `/monitor-screening-query`, `/remove-screening-query-from-monitoring`, `/list-screening-monitoring-records` |
| Widget host `http://127.0.0.1:8032` | `Kyc:Identomat:ResultBaseUrl` | `POST /external-api/result/` (KYC authority, snake_case), `POST /external-api/delete/`, `GET /?session_token=` (applicant page with verdict buttons) |

Every vendor call checks the company key (`companyKey` / `company_key`) and answers 401 on
mismatch; an unknown `configId` answers `{"argumentError":"unknown configId"}` — as the fake does.

Callbacks: on any non-Pending verdict the sidecar POSTs
`{callbackApiKey,type:"session-change",sessionId,result}` to
`DEV_KYC_NODE_BASE_URL + DEV_KYC_CALLBACK_PATH` (default `http://127.0.0.1:5000/api/kyc/callback/identomat`,
the shared KYC/KYB ingress) at each delay in `DEV_KYC_CALLBACK_DELAYS_MS` (default
`1500,5000,15000`). Repeats are deliberate: the first may land before the node has committed
the hosted session, and the node re-fetches the authoritative result and ignores replays.

## Modes and control

`DEV_KYC_MODE`: `approve-on-begin` (default; verdict Approved at creation + callbacks),
`approve-on-poll` (first authoritative result read approves), `manual` (Pending until set).

Control endpoints (both listeners, unauthenticated — bind stays `127.0.0.1`):
`GET /__dev/health`, `GET /__dev/sessions`, `POST /__dev/mode {mode}`,
`POST /__dev/sessions/{id|sessionToken|clientUserId}/verdict {verdict}` with verdict
`Pending | ManualCheck | Approved | Rejected` (ManualCheck → vendor `MANUAL_CHECK`, which the
node treats as non-terminal).

Env: `DEV_KYC_HOST`, `DEV_KYC_API_PORT`, `DEV_KYC_WIDGET_PORT`, `DEV_KYC_COMPANY_KEY`,
`DEV_KYC_KYC_CONFIG_ID`, `DEV_KYC_KYB_CONFIG_ID`, `DEV_KYC_CALLBACK_API_KEY`,
`DEV_KYC_NODE_BASE_URL`, `DEV_KYC_CALLBACK_PATH`, `DEV_KYC_CALLBACK_DELAYS_MS`, `DEV_KYC_MODE`.
Logs: one JSON line per request/callback on stdout; key values are never logged.

## origin-allowance

The adapters refuse any vendor origin that is not public HTTPS (no loopback, no private
ranges — `IdentomatKycProviderService.ValidHttpsOrigin`, `IdentomatKybProvider.ValidPublicUrl`).
A loopback sidecar is therefore admitted only by a narrowly-scoped allowance,
`KycRuntimeSafety.IsInsecureDevVendorAllowed(environment, configuration)`, which is true only when ALL hold:

1. the host environment is `Development` (Staging/Production never qualify);
2. `Blockchain:Mode=Simulated` and real-value bridging is off (the same predicate that gates
   the manual simulator, `KycRuntimeSafety.IsManualSimulationAllowed`);
3. `Kyc:Identomat:AllowInsecureDevVendor=true` is set explicitly.

Even then only a **loopback** http(s) URL without credentials, query (for origins) or fragment
is accepted (`KycRuntimeSafety.IsLoopbackDevVendorUrl`) — private/link-local/metadata addresses
stay refused. Both the KYC and KYB widget redirects must additionally sit on the configured
`ResultBaseUrl` scheme+host+port — one shared predicate, `KycRuntimeSafety.IsPinnedLoopbackRedirect`,
so the two adapters cannot drift apart on this pin again (M1, A4 adversarial review 2026-10-24).
`Kyc:Identomat:ReturnUrl` / `Kyb:ReturnUrl` are NOT relaxed.
`KycProviderRegistry` delegates its Identomat missing-key check to the adapter so the
readiness surface and the adapter can never disagree.

Round-4 A-4 stays intact: the provider is still `identomat` and value gates still never accept
MANUAL — only the vendor host is fake. Regression tests:
`tests/AZOA.WebAPI.Tests/Kyc/IdentomatDevVendorOriginTests.cs`.
Reversal: delete this directory and the allowance; the gate fails closed again.

Not relaxed (deferred until a demo needs it): the operator recovery redirect check in
`HostedSessionReconciliationController` (https-only).

**L3 (A4 fix batch, considered and deferred):** `IsLoopbackDevVendorUrl` accepts any loopback
*hostname* (e.g. `localhost`), not just an IP literal (`127.0.0.1`/`::1`). Restricting to IP
literals would close a theoretical DNS-rebinding-style gap where a hostname's resolution could
change between the config read and the request — but every seam here (adapters, sidecar bind,
sidecar Host-header check in M2) already treats a resolving-to-loopback hostname as
interchangeable with the IP literal, and the dev stack's own scripts reference the sidecar by
`127.0.0.1` already, not by a hostname. Tightening this one predicate without also touching the
Host-header string comparisons elsewhere would just be inconsistent, and the realistic attack
needs control over local DNS/hosts-file resolution, which the dev box's threat model accepts.
Deferred; revisit if the dev vendor is ever exposed past a single developer's machine.
