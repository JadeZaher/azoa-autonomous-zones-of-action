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
`ClaimAdmissionAsync` returns the existing row for a repeated admission key, so two
concurrent begins converge on one provider session instead of two.

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

`RESUMING` is a claim, not an outcome, which is why it is non-terminal. Two
consequences follow and both are wanted: a resumption that FAILS releases the claim
with a compensating CAS back to `AWAITING_OUTCOME`, so the next callback retries and
a failed domain write never strands a terminal session; and a process that DIES mid
resumption leaves a `RESUMING` row that the next delivery re-claims, because
`RESUMING` is non-terminal and the version still matches. A stranded claim
self-heals; it does not need an operator.

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
