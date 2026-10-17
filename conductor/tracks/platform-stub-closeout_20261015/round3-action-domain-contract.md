---
type: contract-amendment
status: accepted-for-staged-implementation
owner: lane-B
---

# Round 3 action and value-domain contract

Root independently accepted this contract on 2026-10-15 for staged implementation.
B1 requires its narrower provider-wire amendment before new wire code. No runtime
acceptance is implied; integrated gates remain unrun.

This is the concrete Lane B contract for the accepted Round 3 plan. No domain source
implementation, build, schema or tests ran for this amendment. Reuse the existing frozen
Version=1 envelope, canonical intent hash, ActionExecutionFence, typed/scoped Surreal
transactions, provider preparation journals and participant NFT lifecycle. Do not rebuild
the workflow interpreter or create another generic transaction signer.

## Ordered subwaves and ownership

| Order | Deliverable | Source ownership | Independent review |
| --- | --- | --- | --- |
| B1 | Fungible prepared ASA creation, confirmed authority and atomic reserve/Holon projection | New Services/Fungible lifecycle/store/POCOs, manager/controller/action adapter; new Algorand partial only | Root; protocol-specific amendment reread before wire code |
| B2 | Hold funding/open/release/refund/chaining durable domain and action adapter | Services/Hold, its store/POCO, new action resolver; reuse value-transfer modules | Root |
| B3 | Scoped existing-operation observation, graph-generation receipt, registry coverage, revoke gate alignment | Action handlers/resolvers; generation store seam coordinated with Root/C | Root |
| B4 | Original-key cancellation/rejection, keyed template authoring and clients | Action admission/stores/manager/controllers, template requests/receipts, workflow SDK/UI sections | A |
| B5 | Per-run causal compensation, participant/recovery controls and receipts | New action compensation domain/store/DTOs; existing saga compensation coordinator | A; Root reviews cross-domain joins |

Root owns shared provider preparation contracts, Program and shared schema/index composition.
A owns swap action/domain/client and C bridge/back; they request registry integration from B.
Root owns Emit/native and supplies their forward-effect/compensation descriptors. B announces
new interface/POCO names before edits; existing native/DEX/WTT provider files are not B-owned.
Each B subwave has a separate source handoff before the next expands the change surface.
These are implementation deliverables, not grounds for new deferred.md entries.

## Common operation identity and result

A domain operation binds Version, domain, logical ID/key, immutable canonical intent/hash,
actor, tenant, actual signer roles, provider/chain/network/protocol identity, policy revision,
consented economics, optional ActionEffectAuthority, and generation. Original request replay
resolves the saved operation before mutable admission gates. New effects revalidate current
actor/tenant/custody/policy and atomically fence action cancellation at authorization.

PreparationKey = domain/logical-operation/stage/generation/intent-hash. Provider-owned prepare
returns canonical signed wire and tx identity without sending. The domain persists it before
first-send authorization; only Submitted authorization permits exact-byte broadcast/rebroadcast.
Unknown history/timeouts never allow a new generation. Authoritative final no-effect permits
an audited generation change under the applicable budget and fresh-signature authority.

Use outcomes Admitted, AwaitingParticipant, Prepared, Submitted, Confirmed, ProjectionPending,
Completed, FailedNoEffect, Cancelled and NeedsOperator. Public projection exposes operation ID,
version, participant action, chain/network and authoritative evidence; signed wire/custody
material stays internal. Confirmed means chain effect final, Completed means all promised local
projections committed. Neither means a transaction was merely accepted by RPC.

Worker recovery can observe/rebroadcast authorized wire and commit already-authorized evidence
projections. It cannot prepare a fresh participant signature. An operator can retry observation
or attach verified domain evidence with an audit; cannot assert success/absence or replace IDs.

## B1 — Real fungible creation and exact local projection

Current concrete route is the Algorand ASA creation used by FungibleTokenManager. Its pending
tx ID currently flows into AssetId/reserve registration; replace that ambiguous string boundary
with provider-owned prepared creation and typed evidence. Inventory the final capability matrix
for other advertised fungible creation routes; an ASA implementation is not proof of parity for
a separate advertised ERC/SPL creation route.

Frozen creation intent includes uint64 total as decimal text, decimals, name/unit/url/metadata
hash/default frozen and manager/reserve/freeze/clawback addresses, exact creator/signing context,
chain/network/genesis, intended platform-reserve or caller target, user/tenant, wallet identity,
optional Holon target and immutable fee/network budget. Platform signer and caller wallet are
not interchangeable. Platform-reserve creation requires authorized platform custody; caller
creation requires that exact wallet's custody authority. Logical issuer remains immutable.

Reuse the Algorand prepare/sign/validate/submit/observe infrastructure in a new capability
module for fungible creation; do not route through the NFT amount-one implementation. Observe
the original canonical create transaction and finalized created asset index. Verify all ASA
parameters and creator at the exact effect; a latest unrelated asset lookup or tx string is
not creation evidence. AssetId is absent until finalized index exists and is canonical uint64
text. Pending remains queryable/recoverable after restart or lost response.

The final projection transaction writes creation authority, configured reserve asset/partitions,
optional Holon link and completed domain receipt together. It joins existing local Holon policy/
row fences and reserve uniqueness/accounting guards. Reuse typed plan methods from reserve
registration, not a second independent reserve model. Existing same asset registration must
match every immutable field; a conflict remains ProjectionPending/NeedsOperator, never remints.
Holon linking cannot overwrite a nonblank asset binding without this exact prior receipt.
Persist network/protocol identity into reserve projection instead of resolving today's default.

If reserve registration fails, retain Confirmed/ProjectionPending and repair the same asset.
The direct API and action resolver report pending until promised registration/linking completes;
an irreversible chain success is never erased because local projection failed. Existing generic
idempotency rows migrate to typed operation correlation; ambiguous old InProgress remains
quarantined unless exact source evidence establishes the created asset. No blind replay.

Acceptance: pending-with-hash has no AssetId or reserve row; concurrent create one wire; crashes
before send/after send/after finality recover; mismatched index/creator/params rejected; reserve
and Holon races roll back local completion; replay after configuration changes retains identity.

## B2 — Hold domain semantics, including funder source

Current hold reference is chain+ID and resolves network from configuration; extend immutable
reference with network/protocol identity and tenant/owner authority. Caller-provided HoldId is
not authorization to control someone else's hold. Direct and action endpoints carry the actual
caller and explicit tenant; never infer node-operator reserve authority from missing tenant.

Hold commands have their own stable scoped command key/receipt distinct from aggregate HoldId.
Open, Release, Refund and ReleaseIntoHold replay exact immutable terms; reuse of a command with
changed terms conflicts. The action resolver observes the command's outcome, not whatever the
aggregate's latest state later became. Successful Held/Open and released-into-held are valid
committed custody outcomes, not claims that the beneficiary already owns transferable funds.

Reserve-sourced open atomically commits the named partition hold draw, Held row and command
receipt under current reserve authority/action fence. Refund before release atomically restores
that commitment once. ReleaseIntoHold atomically transfers commitment from parent to child and
records the immutable parent-child relation; child's window cannot shorten the parent's. No
intermediate unreserved units, duplicate child or refundable parent after continuation.

Funder-sourced hold is an existing declared refusal and is required implementation: prepare a
real exact-unit inbound transfer from the authenticated funder's wallet to the bound custody
account using IValueTransferTransactionModule. Held begins only after final inbound evidence.
Bind funder, beneficiary, asset, exact net amount, network and custody/refund destinations; funder
consent cannot be replaced by reserve authority. Refund of funded custody is its own exact-wire
outbound operation, not a ledger-only partition update. Transfer fees/token mechanics must be
explicit; unsupported mechanics fail before admission, never credit an unobserved amount.

Release reserves the unique outbound claim before signing; it uses existing prepared value
transfer modules and exact authoritative effect observation, replacing direct TransferAsync.
Beneficiary account acceptance/opt-in requires their actual signing grant when necessary and
is exposed as AwaitingParticipant. AutoRelease permits only the original explicit authority,
window, recipient and budgets; workers do not fabricate new beneficiary consent. Releasing
cannot race a refund or chain to a second hold. Possibly sent release permanently excludes a
ledger-only refund until authoritative no-effect proof permits a new reviewed transition.

Acceptance: two different release keys cannot spend one hold; due cutoff uses server transaction
time; release/refund/chaining races conserve partition units; inbound response loss no double
charge; wrong tenant/funder/beneficiary refused; failed acceptance remains a usable pending
journey; terminal evidence and reserve accounting agree after restart.

## B3 — Existing operations, artifact generation and registry completeness

BlockchainExecute's current IdConfig is an observation request for an existing operation,
not permission to create one. Bind actor/tenant, immutable original operation/domain ID,
network and intent. Use actor-scoped manager lookup, then the original domain resolver.
Pending/unknown stays pending, finalized failure stays failed, and only authoritative completed
operation plus promised projections can supply output. Unsupported legacy records are verified
through an audited domain recovery path; absence of a new domain ref never grants signing.
Fresh Deploy/Call uses the existing deployment action envelope, not this observer.

Graph generation is an artifact effect distinct from deployment. Add a deterministic generation
command receipt binding bundle/source snapshots, target, native artifact/compile request,
canonical mappings/resources, actor/tenant and action hash. Source authority/revision/resource
witnesses and final artifact+receipt commit atomically. External compilation can precede commit
but cannot publish mutable latest source under the old hash. Replay returns the exact artifact
without regenerating or modifying the bundle's current deployment. Root/C own compiler/native
semantics; B owns the action generation receipt integration and requests their narrow store seam.

The registry coverage test enumerates actual production registrations. Every advertised handler
must be pure/read, local atomic, governance, NFT, hold, fungible, deployment/artifact, swap, bridge,
existing-operation observer or Emit, each with a Version=1 reference and recovery contract.
No generic successful result with a hash substitutes for settled evidence. Revoke activation
uses the accepted issuer provenance/holder and action-fence fixtures; remove the stale gate and
legacy prose after independent parity review, before claiming this subwave complete.

## B4 — Request disposition, rejection and template authoring

Workflow request disposition uses the same deterministic actor/tenant/action/key identity as
run admission. Cancellation is an audited tombstone atomically competing with creation of the
run/outbox. It contains original request hash, consent and expected publication. If admission
won, return that run, including terminal outcome; do not replace it. If cancellation won, any
late request with that key returns Cancelled and cannot create a run. Different payload under
the key conflicts. GET scoped original-key status is read-only; absence alone is never permission
to clear a client key. Clients obtain new preview/consent only after cancelled/rejected proof.

Engine deterministic refusal is persisted atomically with Pending-to-Failed and run-epoch
advance, or an immutable rejection marker in the admission transaction. Its receipt pins the
reason and exact frozen input and proves no handler dispatch; replay closes that rejection
without invoking a resolver. A current authorization/read conflict is not deterministic invalid
input and does not pin a false permanent rejection. Advance/signal requests also bind scoped
original message key+payload, return exact prior result and refuse changed payload.

Keyed template creation records template and every referenced node-template revision/hash,
canonical substitutions, intended runner, author, tenant, scoped key and copied action/node/edge
IDs. Validate complete graph references; missing templates/edges cannot silently become default
HolonGet or disappear. Structured substitutions must preserve JSON meaning and reject malformed
or duplicate inputs. Stable IDs derive from the command and immutable template slot identity.
One atomic transaction fences source visibility/revisions, creates draft graph and receipt.
Replay reads receipt before mutable template; it never instantiates a second graph.

Default fluent forActor authoring uses the child's credential for both creation and execution.
Parent-author/child-run mode is explicit and requires actual published visibility/invitation;
credential delegation does not convey arbitrary child signing authority. Expose prepare-from-
template returning discoverable draft/action ID and exact manifest. Publish remains a distinct
explicit authorized action; start requires original persisted invocation key and consent to the
published hash. The convenience resumes these explicit stages and reports AwaitingPublication/
AwaitingConsent rather than automatically publishing or charging. Browser loss can discover
original template receipt and authored action by scope; start ambiguity retains original key.

Acceptance: one graph under concurrent requests, template/node-template edit races, same-key
changed substitutions conflict, missing graph refs rejected, parent/child positive and negative
authority, publication/consent renewal, lost create/start responses and account changes.

## B5 — Causal compensation with explicit retention

Create one compensation aggregate per failed/cancelled run, versioned by frozen graph hash.
It records every admitted node's original domain reference, immutable forward receipt and
observed outcome. Wait for all Running/possibly-sent siblings to settle before deciding a
reverse. Traverse reverse dependency order over actual confirmed effects, not EndedAt order.
Parallel independent reversals may proceed only when neither depends on the other's result.

Each effect disposition is Unresolved, NoEffect, AwaitingParticipant, Reversing, Reversed,
RetainedPendingAuthorization or Retained. Every reverse has a distinct deterministic operation
identity and exact original forward reference; it can never rerun the forward handler.

| Forward domain | Compensation rule |
| --- | --- |
| Pure reads / proven pre-dispatch rejection | NoEffect; no external action |
| Local Holon/wallet metadata/loan changes | Reverse only using saved before/after witnesses and exact current versions; conflicting later updates require explicit retention or a newly authorized corrective command |
| Governance ballots/finalization | Preserve election history; a separate current-authority governance command is not retroactive rollback. Retain with authoritative receipt and authorized resolution |
| Reserve hold | Refund exact untouched commitment; funded/released hold uses actual custody/holder-authorized return and final evidence |
| NFT gift/transfer | Existing refund lifecycle tied to original confirmed transfer, actual current holder consent and ownership; issuer powers cannot substitute for holder consent |
| NFT/fungible creation | Creation remains an irreversible historical fact. Optional authorized destroy/burn only where protocol/current balances permit; otherwise explicit retention |
| Swap | Completed swap retained unless actor explicitly authorizes a new bounded reverse market quote; no guaranteed original price/amount refund |
| Bridge | Pending outbound cannot auto-refund. Completed return is a new holder-authorized WTT transfer per C contract |
| Deployment / artifact | Preserve deployed/published truth; cleanup only through Root's linked exact-resource protocol. Removal is a separate effect, not erasure |
| Emit | Outbox admission/delivery cannot be un-sent. Retain event receipt; a correction event is separately authorized and independently delivered |

Compensation authority has its own fence: original run/graph/forward effect, compensation
version/lease, actor/tenant and current participant grants. It cannot reopen the cancelled
forward run or bypass ActionExecutionFence by fabricating Running. First reverse submission
has the same prepared-wire/authorization discipline as any new economic operation.

Expose authenticated per-run compensation GET/discovery and participant-specific commands
with immutable quote/reason and expected version. Operator recent-session policy can inspect,
request evidence recovery and audit a proposed disposition; operators cannot sign for users
or assert missing funds. Retention requires exact authoritative forward evidence plus the
run actor's authorization (and affected owner when a disposition changes their rights).
Unknown effect cannot be retained as known completed. Preserve every proposal/decision audit.

ActionRun remains failed/cancelled according to its forward outcome; a separate public
CompensationResolution is Unresolved, FullyReversed, FullyRetained or PartiallyReversedRetained.
Only the latter three are settled compensation outcomes. Do not label retained execution
“rolled back”, and never mark Completed merely because compensation work was enqueued.
A cancelled run may still have pending forward evidence; its compensation status stays visible.

Acceptance: duplicate coordinators/reverse requests, crash before/after reverse send and local
projection, uneven parallel branches, changed ownership/tenant/consent, irreversibility, partial
reversals and multiple retention approvals, loss of browser state, audited per-run wake of the
actual compensation saga rather than the unrelated original-node retry endpoint.

## Review and final acceptance boundary

Root reviews this amendment before B domain code. Each provider wire extension gets pinned
official contract evidence and narrow review before authoring. Fixture authoring is allowed;
execution remains the integrated sweep after Round 4. The final capability matrix and runtime
evidence must distinguish source-complete, configured, exercised locally and externally
verified behavior. None of the incomplete domains above is an external deferral.
