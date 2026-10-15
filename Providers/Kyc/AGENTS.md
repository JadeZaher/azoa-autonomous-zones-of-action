# KYC provider boundary

The provider advertises availability and flow shape before Azoa writes an active
submission. A configured-but-unavailable adapter fails closed; it never leaves a
pending row that no provider can process.

The manual provider accepts document references only. References must be
absolute HTTPS URLs without credentials, query bearer tokens, fragments, or
loopback hosts. Raw bytes and arbitrary metadata are not accepted through the
tenant contract. Manual approval/rejection remains on the JWT-only `Operator`
surface; a tenant API key can begin/submit but can never self-approve.
It is registered only in the Development host environment. Base/production
configuration selects an unavailable provider, and an explicit production
`Kyc:Provider=manual` still resolves to unavailable until Azoa owns a private
upload lifecycle, malware scanning, retention/deletion, and reviewer operations.

Hosted adapters return non-sensitive session/redirect metadata from `Begin`; no
provider credentials belong in that response. Note that the tenant projection does
NOT omit `ProviderSessionId` — `KycManager.ToSessionStart` copies that field
straight through — so an adapter must place an AZOA-side correlation id there and
keep the vendor's session id server-side. The Veriff adapter remains an explicit
unavailable stub until a reviewed integration exists.

Provider selection is strict: `manual`, `veriff`, `hosted`/`generic-hosted`, and
`identomat` are the only registered keys. Any other `Kyc:Provider` selects
`UnavailableKycProviderService`; it never silently inherits manual review.

## §identomat

`IdentomatKycProviderService` is the first real adapter. It implements both
`IKycProviderService` and the reusable `IHostedSessionProvider` seam, and it
satisfies the lifecycle contract above through `HostedSessionFlow` rather than
re-implementing it (see `Services/HostedSessions/AGENTS.md`).

**Two base URLs is deliberate, not a duplicated setting.** `/begin` and all five
AML screening endpoints live on `external-api.identomat.com` with camelCase
`companyKey`. The authoritative `/result/` fetch lives on the legacy
`widget.identomat.com` host with snake_case `company_key`. They are separate
configured values because they are separate hosts with separate conventions;
collapsing them into one origin breaks one call or the other.

**The callback is an untrusted trigger.** `VerifyCallback` compares the
body-borne `callbackApiKey` in constant time (hash-then-`FixedTimeEquals`, so the
secret's length does not leak either), then returns correlation ONLY. The payload's
own `result` field is named `UntrustedResult` and is never read: a verdict
authenticated by a shared secret in a request body is not an authority. The
`/result/` re-fetch is. A forged callback therefore cannot approve anyone; the worst
it can do is trigger a redundant server-side poll, which the shared request budget
and the endpoint's own rate limit bound.

**`MANUAL_CHECK` is non-terminal, and so is anything unrecognised.** Only
`APPROVED` and `REJECTED` map to a terminal outcome. Every other value — including
vendor states that do not exist yet — is non-terminal, which is the fail-closed
direction: a non-terminal session never reaches the ledger write that opens a value
gate.

**The provider session id never leaves the node.** `KycManager.ToSessionStart`
projects `KycSubmission.ProviderSessionId` into the tenant-facing response, so the
adapter puts the AZOA hosted-session Guid in that field. Identomat's parent `id`
and child `additionalSessionId` are persisted on `hosted_session` only. Both are
kept because the callback names one and the authoritative fetch wants the other.

**Capabilities report key NAMES.** `Available` is true only when both origins are
valid HTTPS and all three credentials are present; otherwise
`MissingConfigurationKeys` lists `Kyc__Identomat__*` names. No configured value is
ever interpolated into a capability or error string.

**Present is not configured, for the callback secret.** `CallbackApiKey` is the ONLY
authentication on an anonymous internet-facing endpoint, so it carries the same
32-character floor `KycPersonKeyDeriver` already enforces on the person-key root
secret. `"test"` is present and guessable in a handful of attempts against a 60/60s
rate limit; a presence check would report it as fully configured.

**Origins refuse internal address space.** A vendor origin is operator configuration
rather than attacker input, so this is hardening rather than a boundary — but
`169.254.169.254` is a cloud metadata endpoint, and an adapter that POSTs a company
key at whatever origin it is handed must not be willing to POST it there.

**The callback endpoint answers 202 on every path.** Unconfigured adapter, unreadable
body, rejected secret, unknown session, parked verdict, applied verdict: one status
code, one body. A distinct 503 for "credentials not installed" answers a question
about the node's configuration for any anonymous caller — the same class of oracle
the opaque failure message closes, just spelled in the status line.

**One shared request budget.** Every Identomat call goes through
`IIdentomatHttpTransport`, which is the only egress point. That is what makes the
600 req/min per-IP ceiling enforceable across KYC session calls, AML screening, and
monitoring polls at once — they are one budget, and exceeding it bans the node's IP
for an hour, taking the node's own KYC offline. An observed HTTP 529 opens a
circuit for the configured cooldown.

`GenericHostedKycProviderService` is an operator-configurable, unavailable
scaffold. Even complete configuration does not advertise availability. A real
adapter must first add a durable, idempotent verification-attempt record,
persist the hosted session before returning a redirect, verify signed webhook
headers over the raw body, deduplicate provider event ids, map events to the
attempt/user, and CAS terminal status. Until that lifecycle exists the stub
prevents the current begin/submit shapes from creating two provider sessions.

Only `MANUAL` submissions may be decided by Azoa operator approve/reject.
External providers must decide through their future verified-event path. An
expired approval is not verified and expired active submissions can be replaced.
Submission plus document rows are created in one Surreal transaction.

Manual review uses `IKycStore.TryReviewAsync`, a conditional update whose WHERE
clause requires provider `MANUAL`, active status, and unexpired timestamp. Two
reviewers may read the same pending row, but only one transition returns a row;
the loser receives a conflict. No `User.IsVerified` projection is updated—the
submission ledger is the sole authority, so a partial second write cannot split
identity state from the KYC gate.

Begin is an ensure-active operation backed by the same KYC ledger. It persists a
pending attempt with secret-bearing provider correlation kept server-side and
returns only safe flow metadata. An unexpired attempt is resumed; rejection or
expiry allows the node to create the next attempt while the tenant keeps one
stable idempotency key forever. Manual approval is refused until documents have
been atomically attached. Hosted providers remain disabled because their future
adapter must claim durable admission before making an external create call.
