---
type: contract-amendment
status: accepted-with-binding-review-clarifications
owner: root
---

# Consent lifecycle and delivery atomicity

Source inspection found that ConsentManager writes grant, audit and event through
three independent calls. ConsentWebhookEmitter catches failed enqueue, and the
delivery worker resolves the latest destination and posts without a durable claim.
The existing claims of transactional enqueue and single-winner delivery are not
supported by the implementation. These are required fixes, not external deferrals.

## Lifecycle boundary

Independent lane B review requires stable caller/source command identity and immutable
grant intent (grantor, tenant, scopes, origin, participation reference and expiry).
Response-loss replay returns the original grant receipt even if subsequently revoked;
it never creates a second grant or restores authority. Fresh lifecycle admission joins
the actual grantor credential/auth watermark or exact internal source authority,
active tenant and membership witness in the transaction. Actor IDs and pre-reads
alone are insufficient. Preserve the Participation no-value-scope/required-reference
rules and UserExplicit distinction. Missing principal never implies internal authority.
Bulk offboarding also fences fresh admissions against the same membership boundary,
rather than only revoking a pre-enumerated snapshot.

Replace mutation-side Upsert plus audit plus emitter with a typed lifecycle store
operation whose transaction commits the grant transition, immutable audit and
deterministic notification together. New grants use create semantics; revoke uses
an exact existing grant identity and live-state conditional mutation. Concurrent
revoke cannot resurrect or overwrite a newer grant. Replayed revocation returns the
existing terminal fact without another notification. Offboarding uses the same
per-grant transition and exact tenant/participation filter. Bulk sovereignty claim
revocation must retain its existing atomic authority removal while recording each
corresponding notification; do not split that safety boundary for convenience.

Expiry notification is distinct from expiry enforcement. Signing continues to
enforce the expiry timestamp live. A bounded expiry scan records a deterministic
per-grant expiry audit/event with a conditional expired/nonrevoked grant claim.
It never extends or reactivates the grant. All production mutation callers must
migrate; retaining an unrestricted Upsert bypass is not completion.
Expiry and revoke use deterministic distinct transition identities and the exact
persisted transition times. Expiry cannot emit after revoke wins its conditional
claim. Each immutable event describes the transition that committed, never a later
mutable grant snapshot.

## Destination and payload

Within lifecycle admission, freeze the exact canonical body, stable event identity,
tenant, current versioned registration identity, destination revision and URL. Lock
the registration version in that same transaction. Missing/disabled/unversioned
registration records an explicit undeliverable notification; it does not block a
user from revoking consent. Later registration must not silently retarget historical
events. No plaintext signing secret is copied into the event. A lease captures the
protected current secret revision only while the pinned destination remains active.

## Delivery and management

Use the action delivery discipline: bounded due scan, database-time exact-epoch
claim, unique expiring lease, attempt consumed before HTTP, fixed body and original
destination, protected secret revision, finite HTTP timeout, headers-only response,
and exact live-lease completion. A stale worker cannot finish a newer attempt.
Unexpected database failures reach the worker boundary rather than becoming an
HTTP failure or triggering a second finish. Symbolic errors cannot disclose bodies,
secrets or receiver text. Delivery never changes consent authority.

Reuse the common safe transport/signing helpers and extract a shared leased sender
where the two workers would otherwise duplicate wire behavior. Keep lifecycle
evidence separate: action receipts and consent transitions are not interchangeable.
Tenant/operator metadata and audited retry follow the same current-management and
original-destination rules as action delivery. No silent migration of legacy events
to a new receiver; unverifiable historical delivery evidence stays explicit.

## Verification

Implementation checkpoint: explicit admission has bounded independent source
acceptance. Owner revocation and expiry observation now have atomic store methods
and shared destination capture; their independent review is pending. Original
grant receipts are checked against the persisted immutable fields. Legacy revoked
facts report missing notification proof explicitly. Real-store fixtures cover
concurrent revoke, inactive tenant, ownership isolation and expiry/revoke ordering.
These methods remain unwired until all mutation callers and delivery are migrated.
No runtime verification has run.

Participation admission now advances a persisted tenant/reference membership epoch.
Atomic offboarding closes that epoch and revokes the exact matching set with all
audit/event/transition records, or writes a closed tombstone when the set is empty.
Its original closure receipt preserves time and count on replay. One frozen receiver
snapshot is shared across each batch. Independent review and execution are pending.

Independent review identified that participation grants also confer governance
membership. Direct user willingness alone cannot establish enrollment. The store
now requires tenant-approved subject/reference evidence; provisioning creates this
evidence under actual tenant credentials and a transactional child ownership,
external identity and authentication-watermark check. New self-enrollment refusal
and provisioning/claim fixtures are authored. Caller migration and independent
review of this correction remain required.

Author real-store races for grant/audit/event atomicity, concurrent revoke, expiry
versus revoke, bulk authority removal, registration rotation versus enqueue/claim,
lease expiry and stale finish, attempt exhaustion, and exact retry replay. Preserve
the signing seam revocation invariants. Transport fixtures cover stable payload,
per-attempt signatures, response loss and database finish failure. Execute these
with the final integrated sweep after all fixes; source review alone is insufficient.
