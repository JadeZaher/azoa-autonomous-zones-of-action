---
type: Contract Amendment
title: AZOA hosted identity protocol v1
status: Proposed; independent review required before trust implementation
---

## Trust and scope

This is an AZOA-defined interoperability protocol for explicitly configured trusted identity-verification services. It is not a claim that arbitrary existing vendor endpoints implement it. Node configuration fixes provider identity, API origin, redirect origins, credentials, protocol version and assurance/policy revision. Users cannot choose or alter those values. Only `kyc` is supported in v1; KYB remains the separate reviewed business-verification contract.

API and callback authentication use distinct 32-byte-or-stronger shared secrets. HTTPS is mandatory outside an explicitly isolated test handler. Origins reject credentials, fragments, internal IP space and redirects to unconfigured origins. HTTP clients do not follow redirects while carrying credentials. Credentials, subject document fields and vendor session identifiers never appear in tenant projections or logs.

Callbacks require `X-AZOA-Key-Id`. Configuration has one active signing key ID and optional retired keys with explicit UTC grace expiry (at most 24 hours); unknown IDs and expired retired keys fail authentication. The five-minute timestamp window still applies to every key. API credentials are separate and rotate without changing the admitted request. Secrets never become persisted request fields.

## Create and immutable correlation

`POST /azoa/identity/v1/sessions` takes JSON `{ protocolVersion: "1", provider: configuredKey, attemptId: uuid, subjectId: uuid, tenantId: uuid|null, flow: "kyc", policyRevision: string, returnUrl: configuredHttpsUrl|null }`. The AZOA hosted-session ID allocated before egress is `attemptId`. An `Idempotency-Key` header equals that attempt ID. Authentication is `Authorization: Bearer <apiSecret>`.

The provider MUST atomically bind that key to the entire canonical request and return the same session after every accepted retry. A conflicting request is 409 without creating a session. The key/session tombstone is permanent; an expired session cannot be recreated under the same key. Successful 201/200 response includes the exact protocol/provider/attempt/subject/tenant/flow/policy correlation, `sessionId`, `redirectUrl` and `expiresAt`. Redirect origin must match the configured allowlist. Malformed or mismatched create responses are ambiguous effects, never proof that no session exists.

AZOA persists the exact UTF-8 create body before first egress, with fixed property order as listed above, lower-case UUID `N` strings, explicit JSON nulls and invariant strings; retries send those same bytes. Provider equality compares the decoded named fields exactly, rejects duplicate/unknown properties and does not reinterpret URL/string normalization. Persist origin/provider/version/policy/request digest with the body. A configuration change cannot reconstruct another body under that key; incompatible origin/provider policy requires quarantining the original attempt, not sending its key to a different provider. Callback endpoint configuration is out-of-band and not client-supplied.

`GET /azoa/identity/v1/attempts/{attemptId}` is an authenticated linearizable lookup returning the same immutable session/correlation. 404 is not permission for AZOA to release and send a new attempt: it may race accepted creation. Retrying the original key is safe because permanent idempotency is part of this opted-in protocol. Implement automatic leased reconciliation now: look up the original attempt, attach an exact matching session, or replay the exact persisted create body with its original key. Recovery never allocates a replacement key after ambiguity. Provider 5xx/transport errors remain nonterminal and back off fairly. Audited operator attachment/quarantine supplements automatic recovery rather than replacing it.

## Authoritative result

`GET /azoa/identity/v1/sessions/{sessionId}/result` returns the exact immutable correlation plus `status` from `pending`, `review`, `approved`, `rejected`, `expired`, `decisionId`, `decidedAt`, and `subject` when approved. Only an approved terminal result with all required evidence is eligible for AZOA's independent identity/AML gate. Unknown enums, missing fields, wrong identifiers, future/invalid decision times or inconsistent terminal evidence fail closed and remain reconcilable.

`pending`/`review` omit terminal decision fields. All terminal results require nonempty `decisionId`, UTC `decidedAt` no later than server time plus five-minute skew and no earlier than the admitted attempt, and `evidencePayload` (base64 of the exact terminal evidence UTF-8 JSON bytes) and `evidenceDigest` (lowercase SHA-256 of those decoded bytes). Approved results also require `verifiedAt` and `validUntil` with admittedAt <= verifiedAt <= decidedAt < validUntil, and the latter strictly in the future. The terminal evidence JSON carries the status, immutable correlation, subject/checks and these times; outer fields must equal it. Persist the first accepted terminal identity/digest before domain effects; changed terminal identity/content is quarantined and cannot reverse or overwrite an already applied outcome. Session identifiers are bounded opaque strings escaped as one URL path segment, never interpolated as routes or accepted absolute URLs.

Approved `subject` requires verified first/last name, ISO birth date, issuing country and document number, plus `checks: { documentVerified: true, biometricMatchVerified: true, livenessVerified: true }` in the terminal evidence. The protocol asserts the configured vendor performed those checks; a callback or client cannot assert them. Stable decision identity and terminal content are immutable. The shared KYC handler preserves the existing provider-neutral person-key derivation from scope/document/issuing state, records validated provider evidence attribution separately, screens the exact returned subject and registers monitoring with immutable lineage before lease-fenced active-attempt completion. Cross-provider person uniqueness is unchanged. Generic approval never skips independent AML or person binding. Rejection/expiry does not fabricate approved person attributes.

## Callback authentication and replay

`POST /api/kyc/callback/generic-hosted` carries `{ protocolVersion: "1", provider, eventId, sessionId, attemptId }`. Headers: `X-AZOA-Timestamp` decimal Unix seconds, `X-AZOA-Signature` lowercase hex HMAC-SHA256 of ASCII(timestamp) + `.` + exact UTF-8 request body. Use a five-minute clock window and constant-time digest comparison; missing/duplicate/malformed headers fail authentication. Body size is bounded before hashing/parsing. No callback outcome or subject is trusted.

After authentication the callback only locates the matching provider/flow/admission and triggers authoritative refetch under the existing hosted resume lease. Replay is harmless through the durable session/attempt CAS; event IDs are audit correlation, not a second approval authority. Delayed valid events outside the timestamp window require a newly signed retry or authenticated polling. A provider must retry retryable processing failures; HTTP acknowledgement must not falsely acknowledge a required but unattached active attempt as completed.

## Recovery and configuration changes

Operator list/detail projections expose safe correlation and reconciliation state. Verified attachment fetches the candidate result/session and checks exact original subject, tenant, attempt, policy, provider and flow before CAS attachment. Proven-unsent release uses only local transport/admission evidence. Possibly sent requests stay quarantined until authoritative attachment or same-key protocol replay; operator absence attestations never permit a replacement send.

Provider/policy changes expire or refuse old active attempts under the existing selection-revision rules. Existing completed outcomes do not become approval under another provider identity. Generic provider identifiers cannot collide with reserved Identomat, Veriff, manual or KYB registrations. Shared KYC outcome handling must replace hard-coded Identomat attribution with the persisted attempt's validated provider discriminator while retaining all current subject/AML/lease checks.

## Executable acceptance fixtures

Author an in-process protocol service fixture with permanent key binding and authoritative result/attempt routes. Exercise duplicate/concurrent creation, changed request under one key, response loss, callback-before-local-attachment, wrong provider/tenant/attempt/policy, header/body tampering, timestamp/replay handling, redirect rejection, missing verification evidence, unknown enums, delayed approval after attempt replacement and AML/monitoring failure. The fixture implements this versioned contract; it must not replace the production adapter or mark real external readiness verified. Tests are authored now and executed only in the final integrated sweep.
