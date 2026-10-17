# Deployment operations

Generation v2 distinguishes PlatformRuntime from NativeContract. The immutable
payload hash feeds the fixed executable loader template; artifact hash binds payload
and loader hashes. The deployment ID is supplied at invocation, never patched into
hashed bytes. Ordered occurrences own distinct node identities and binding names.
Internal dependencies remain exact prior-occurrence requirements in the frozen run.

Occurrence boundaries retain each source DAG's branching and failure handling.
The next occurrence waits for all previous nodes to settle; unselected skipped arms
do not veto successful completion. Cross-occurrence input mappings require a proven
successful output, not merely a node that became terminal.

An already confirmed root artifact remains immutable after a newer generation.
New invocation admission checks root visibility/activity, selected nested artifact
descriptors and complete bound resource content atomically through deployment
authority witnesses. Exact original invocation replay precedes mutable access checks
and returns its existing run without admitting another effect.

Request-key cancellation claims a terminal tombstone at the same deterministic ID
as admission, fencing a delayed creator even after quote expiry. An existing operation
uses the normal no-effect cancellation CAS; unknown or submitted outcomes retain
their journal. Discovery and request lookup are actor/tenant scoped.

An artifact is not an activation. Confirmed platform registration is the durable
deployment row resolved by the invocation endpoint; runtime execution uses the actual
caller through IFrozenActionRuntime. Current source revision/access and reference
authority are rechecked. Native operations preserve immutable requests and exact
provider-owned stage bytes before broadcast. Prepared is not submission authority:
only an interactive guarded CAS sets SubmissionAuthorized; observers may replay
that saved authority but never prepare or authorize a new stage.

Server-issued native quotes bind the full intent and caller before signing/nonce
allocation. Providers preflight their complete stage plan and obey aggregate fee,
rent and value caps. Actual cost and confirmation evidence must not be confused with
reserved limits or an assurance that arbitrary programs have no other authorized effects.
All source and fixtures remain unexecuted until the integrated closeout sweep.

Action deployment configuration freezes artifact, target, actor custody and explicit
fee/rent/value ceilings before effect admission. A run/node-specific server quote is
claimed once; concurrent or restarted read-only quotes use the winning saved plan.
Only operation identity, protocol identity and quote fields are server-derived.
Wallets, instruction input and ceilings cannot change. Quote expiry limits new API
admission, not recovery of an already claimed action request; current costs remain
bounded before any new signature. Recovery can claim a discoverable request but
cannot grant its first submission authorization.

Action-bound deployment admission, preparation ownership and first submission
authorization share a transaction with ActionExecutionFence. Cancellation or a fork
cannot race a new authorized effect past that fence. Observation of already
authorized bytes remains available after action termination. AVM/Solana providers
claim their immutable blockhash/validity plan in native_preparation before custody;
the signed candidate is filled once and the record never authorizes egress itself.

Linked recovery quotes reconstruct the original immutable request and the complete resolved history from the owned parent row. They never accept replacement business instructions. Admission pins parent version/history and exact saved quote; the store checks quote expiry using database time in the same transaction as the exclusive parent-child link. Replays resolve the existing child before mutable quote checks. Confirmed children remain recoverable until their evidence is projected atomically into the parent, retaining original economic evidence.

Withdrawing an unadmitted recovery quote checks its server-issued identity and exact
original operation before any success response. It reads the parent then the child:
an existing child must be reconciled, while an advanced parent version excludes a
future admission of the old quote. At the original version, the ordinary parent CAS
advances that fence and competes with child admission. This invalidates all quotes
for the same parent version without altering the original intent or signing a wire.
Lost responses retry the exact withdrawal; transport failure never proves withdrawal.

Ancestry inspection is an explicit one-page POST and never prepares/signs a wire.
The service derives the audit from the owned current head, rather than accepting a
caller-provided audit ID or cursor. Replacement quoting and new admission require
Complete for that deterministic head and its immutable intent/predecessor; the
completed digest is part of the quoted predecessor witness. Each new head has its
own audit. Withdrawal compares the same frozen witness when its parent version is
still current; existing advanced-version nonadmission proofs remain valid.
