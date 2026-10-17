# Hosted-session flow (D11)

`HostedSessionFlow` is the reusable shape shared by KYC-begin and a future fiat-in
checkout: **begin → hosted redirect → untrusted callback → authoritative
server-side re-fetch → resume the suspended work**. It is deliberately NOT part of
`IKycProviderService`, because a payment provider implements exactly this lifecycle
and nothing KYC-shaped. Phase 3 step 15 is specified against this seam existing.

The extraction is clean: the KYC-specific parts are confined to
`IHostedSessionOutcomeHandler` (what "resume" means for this domain) and to the
provider implementation itself. `flow_kind` on the `hosted_session` table
discriminates flows, so a second flow adds a constant rather than a table.

## Why the ordering is the whole point

**Admission is claimed before the external create call.** The reverse order can
orphan a provider session: a crash between "create" and "persist" leaves a live
session at the vendor that no inbound callback can ever be matched back to, and the
retry creates a second one. Claiming first also makes the retry path a resume —
`ClaimAdmissionAsync` returns the existing row for a repeated admission key. Only
its original candidate owner may create the provider session; all other callers
resume an attached redirect or receive a reconciliation conflict.

**The callback is a trigger, never an authority.** `VerifyCallback` returns a
correlation only; `HostedCallbackTrigger` has no outcome field, so a provider
adapter *cannot* accidentally propagate the payload's claimed verdict. The verdict
comes from `FetchAuthoritativeOutcomeAsync`.

**A non-terminal verdict parks; it never terminalises.** A provider "still
deciding" state is written as `AWAITING_OUTCOME`, and the domain handler is not
called at all. Terminal state is what opens a value gate, so an undecided identity
must not be able to reach it.

**Exactly one terminal write, enforced by CAS.** `TryTransitionAsync` requires the
exact live `state_version` AND a non-terminal current state. Losing that race
returns a null row and is NOT an error — it is the expected outcome for every
redelivery after the first. Identomat documents no dedup or retry semantics
whatsoever, so this is the only thing between a retry storm and a repeatedly
applied outcome. The terminal check also means a late callback cannot flip a
`FAILED` session to `COMPLETED`.

**The resume CLAIM is taken before resumption, not after.** The terminal check near
the top of `HandleCallbackAsync` reads a SNAPSHOT, so two concurrent deliveries can
both pass it. If resumption ran before the CAS they would both resume: one writes
submission `REJECTED`, the other writes `APPROVED`, and the losing CAS is swallowed
as an ordinary redelivery *after* its ledger write has already landed. Net result:
`hosted_session = FAILED` while the submission says `APPROVED` — and the value gate
reads the submission. So the order is: authoritative fetch → CAS into the
non-terminal `RESUMING` state → resume on the winning branch only → CAS to terminal.

`RESUMING` holds a random token and a two-minute lease, renewed every thirty
seconds while the domain handler runs. A live lease cannot be reclaimed, even
when a second callback reads its latest version. An expired lease can be reclaimed
with a new token and version. Completion and release require both the current
token and an unexpired lease; generic transitions cannot change a RESUMING row.
Renewal failure cancels domain work and propagates infrastructure exceptions.
A valid lease serializes live resumption; it does not promise exactly-once vendor
calls after a crash. Domain writes must remain idempotent and honor cancellation.

**Domain resumption still runs before terminalisation.** If the handler fails, the
session ends up non-terminal (via the compensating transition) and the next callback
retries. Terminalising first would strand an approval that no ledger row backs.

## Callback failures are deliberately opaque

`HandleCallbackAsync` returns one identical failure for a bad secret, a malformed
body, and an unknown session id. Distinguishing them turns an unauthenticated
endpoint into an oracle: an attacker could enumerate valid session ids, or confirm
a guessed secret. The server-side log records which it actually was.

## At-rest posture

`hosted_session` holds provider session ids and redirect URLs. These are
secret-adjacent correlation, not credentials, and they are never projected to a
tenant. They inherit the deployment's storage-level encryption; AZOA adds no
field-level encryption here because possession of a session id alone authorises
nothing — the authoritative fetch additionally requires the company key.

## UNIQUE-on-nullable, settled

`hosted_session_provider_session` is `UNIQUE(flow_kind, provider_key,
provider_session_id)` and `provider_session_id` is `option<string>`, NONE on every
freshly claimed row. That looks like it could brick KYC node-wide — one user
holding an un-attached claim would make every other `ClaimAdmissionAsync` CREATE
fail — and a failed `/begin` deliberately leaves exactly such a claim open for retry.

**It does not.** Verified empirically against SurrealDB v3.1.4, the version pinned in
`docker-compose.dev.yml`: a UNIQUE index does not collide NONE components, so many
un-attached claims coexist. A non-NONE duplicate does collide, so the index still
does its job. Explicit SQL `NULL` is unreachable here at all — a SCHEMAFULL
`option<string>` refuses it (`Expected 'none | string' but found 'NULL'`). The index
is therefore RETAINED; it is a real guarantee that two AZOA sessions never bind one
vendor session. `SurrealUniqueIndexNoneSemanticsTests` pins the consequences and the
`SurrealNote` on the POCO carries the finding into the generated `.surql`.

## Admission reconciliation

Identomat does not document an idempotent create key. A transport failure after
sending `/begin` is ambiguous, so an unattached claim is retained and immediate
re-creation is refused (round 4 adds a bounded orphan supersede, §recovery). The provider's `clientUserId` is correlation, not a uniqueness
guarantee. Operators must find and attach the existing vendor session, or obtain
positive evidence that none exists before clearing the claim. An expired redirect
also needs explicit provider renewal rather than an unguarded second create.
The external recovery contract is tracked in the active closeout `deferred.md`.

`PROVIDER_REQUEST_NOT_SENT` is reserved for proof that no external request was
sent. Identomat's local request-budget refusal emits it; the admission creator
then conditionally removes its untouched claim so a later attempt can retry.
Timeouts, 5xx, unreadable successful responses and attachment failures
never use that signal; a definitive 4xx/529 refusal uses `PROVIDER_REQUEST_REFUSED` and is
released the same way (round 4, see §recovery). The callback delivery counter uses bounded read/CAS retries;
contention can omit diagnostic counts, never identity decisions.

`RefreshAsync` shares authoritative polling and leased resumption with callbacks.
Its caller must authorize the subject/tenant first; the shared flow additionally
checks that the stored provider and flow kind match the supplied adapter.

## Operator recovery

`GET /api/operator/hosted-sessions/reconciliation` lists unattached admissions at
least five minutes old as `NeedsOperator`. The JWT-only Operator reconciliation
route requires a documented evidence reference and explicit reviewed-subject
attestation. Attachment requires a real server-side provider result and an
Identomat widget URL; it never trusts an operator-supplied verdict. Confirmed
provider absence retains the old row as ABANDONED and changes its admission key,
allowing a new attempt without deleting the actor, timestamp or evidence record.
Concurrent attachment/resolution loses its version predicate. The age floor
avoids interrupting an ordinary provider request still in flight.

The documented results do not promise an echoed unique client-attempt reference,
so callback-first automatic attachment is not enabled. Subject linkage is an
explicit audited operator decision. An undecided KYB result cannot prove session
existence with the current provider parser; recovery waits for a verifiable final
result rather than treating an empty provider response as proof.

## Creation ownership and reconciliation

Every admission carries an opaque creator token. A send-start CAS commits before
calling the provider. Operator absence may release only a token-bearing admission
whose send has never started; sent or legacy-unknown requests stay quarantined with
the admission key reserved. Original token responses can attach after quarantine.
Verified operator attachment records cumulative audit entries. Recovery accepts
recognized authoritative pending sessions, but never an unverified child alias.
The provider API has no documented request-id lookup or authoritative absence
barrier; automatic resolution of ambiguous creates remains vendor-dependent.

## Prepared identity adapters

PreparedHostedKycProvider persists exact request bytes before admission. Recovery
retains the original attempt key, origin and policy, and uses a durable send lease
under the current KYC authority fence. Generic v1 supports automatic same-key
lookup/replay; Veriff permits only authoritative candidate attachment after an
ambiguous create. Worker scheduling advances before each attempt, including
failed authority checks, so unresolved rows cannot starve later work.
Callbacks authenticate raw bytes before storing minimized receipt identity and
refetching provider authority. Callback receipt replay is safe after a crash;
domain completion still requires the shared renewable lease and terminal evidence
pin. Operator recovery uses recent node-operator authentication and an audit reason.

Authenticated Veriff/generic callbacks receive 503 until their trigger has been processed; invalid callbacks retain opaque 202 responses. This includes callback-before-attachment authoritative lookup outages. The saved callback body digest is not a replacement for the vendor retry obligation: unprocessed candidate session identifiers are not acknowledged as durable recovery work. Completed duplicate callbacks receive 202.


Tenant custody first-send authorization
--------------------------------------
HostedSessionRequest.CustodialAuthority is internal and excluded from JSON. The
flow carries it to ICustodialHostedSessionStore at the first-egress CAS. Prepared
provider recovery may reuse only a session whose first-egress authority was
already persisted; preparing a request is not permission to send it. The claim
can remain inert after custody revocation and is handled by audited reconciliation.

## §recovery (round 4, F-H1 + IDENTOMAT-RECOVERY)

Identomat persists no `HostedProviderAttempt`, so the Veriff/generic recovery worker never sees
it, and the operator queue listed only unattached `CLAIMED` rows. Round 4 closes that gap
without relying on vendor behaviour the captured docs do not state (no documented callback
retry, no lookup by `clientUserId`, no create idempotency key).

- **Outcome poll.** `HostedOutcomePollWorker` lists attached, non-terminal KYC/KYB sessions
  whose `next_poll_at` is due (NONE = due), claims each by CAS on its observed `next_poll_at`
  (one winner across replicas), sets the next poll by exponential backoff (60 s doubling to
  60 min) and refreshes through the shared flow. Prepared providers (Veriff/generic) are
  skipped. Every call spends the shared `IdentomatRateBudget`; the sweep stops when the
  circuit is open or the budget refuses. Sessions undecided after `MaxAgeHours` (720) close as
  `ABANDONED`/`POLL_EXPIRED`. Settings: `Kyc:Identomat:OutcomePoll:*`, on wherever a config id
  is set.
- **Refresh.** User: `POST /api/kyc/refresh`. Operator: `POST
  /api/operator/hosted-sessions/{id}/refresh` now re-fetches Identomat sessions (previously 404)
  through `HostedIdentityProviderResolver`, which binds the session to the provider of the
  CURRENT authority (node default or tenant selection) and audits `OperatorRefresh`. The
  reconciliation list adds attached sessions untouched for an hour as `AwaitingProviderOutcome`.
- **Definitive refusals (F-M1).** `PROVIDER_REQUEST_REFUSED` (4xx, and the documented 529
  block) means nothing was created, so the admission is released like an unsent one; 5xx,
  timeouts and unreadable bodies stay quarantined.
- **Orphan supersede.** A sent create with no attached session past `OrphanWaitMinutes` (5) is
  an orphan the vendor cannot look up. The subject's next begin supersedes it (`ABANDONED`,
  `SUPERSEDED_ORPHAN`, admission key retired, audited) and starts a fresh attempt, at most
  `OrphanSupersedesPerDay` (1) per subject and flow; past that, `NeedsOperator`. KYC
  (`KycManager`) and KYB (`KybService`) both do this. The budget count is advisory (a rate
  limit, not a money invariant); the per-orphan CAS is the single winner.
- **Late responses.** If the original create returns after its admission closed, the attach
  CAS loses and the flow deletes the vendor session through the documented
  `/external-api/delete/` (`IDeletableHostedSessionProvider`), auditing
  `LateProviderSessionDeleted` (or `…DeleteFailed`; the session stays inert either way).
- **Inert callbacks.** A callback for a session the node never attached (an orphan, a deleted
  late session, a forged id with the right key) finds no row and decides nothing; the callback
  endpoint keeps its single opaque 202 because the vendor documents no retry semantics a
  status code could trigger — recovery is the poll above, not redelivery.
