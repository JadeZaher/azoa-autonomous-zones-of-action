---
type: plan-review
status: accepted-plan-source-only
reviewer: independent-adversarial-lane-B
---

# Round 3 adversarial plan review

Review snapshot: initial round3-plan.md plus round3-assessment-optimism.md and
round3-bridge-contract-input.md. The independent adversarial full-goal assessment has
not arrived at this snapshot. No implementation approval is implied; reread its delta
and the reconciled plan before a final verdict. No source edits or runtime gates ran.

The draft correctly preserves real protocol implementation, pending evidence, exact wire,
caller authority and the narrow Identomat-only external constraints. It still needs the
following concrete corrections to be executable without leaving important decisions to
individual provider authors after parallel code begins.

## Required corrections

### R1 � Stage the work and assign shared preparation/authority ownership

Four bullets currently contain more independent domains than four ownership lanes:
three DEX protocols, six directed bridge pairs, five action/value domains, compensation,
template authoring, native cleanup, metadata, runtime resources and delivery. Parallelism
alone does not resolve their shared signer, nonce, fee, schema and lifecycle dependencies.

Split each lane into explicitly ordered subwaves with a coherent source handoff. First
freeze domain intent/result contracts and provider preparation identity; then provider
modules; then domain state/store; then action/SDK/UI adapters. Name one owner for shared
preparation contracts, schema POCOs, Program, canonical chain/network/address helpers and
each client file. Name a reviewer for root-authored native/Emit work. Root cannot provide
its own independent source verdict. No package is complete merely because its adapter
returns Unsupported or NeedsOperator.

Acceptance: ownership/dependency table, per-subwave deliverables and reviewer, no competing
preparation helpers or shared-file edits, and registry coverage lists every handler's final
owner including graph generation, direct manager/controller callers and legacy endpoints.

### R2 � Specify generation and economic-authority invariants across every new domain

�Reuse preparations� is insufficient when DEX, bridge and native modules share a signer.
Define a domain-separated preparation key including logical operation, stage and generation;
use existing nonce allocation/exact-wire storage. Persist first-send authorization atomically
with the domain state and action fence. Provider recovery cannot submit prepared bytes until
that authorization exists. Separate actual effect signer, fee payer, token owner and logical
actor. Automatic observation/rebroadcast never acquires a fresh participant signature.

Acceptance: concurrent operations sharing one signer do not reuse a nonce; same preparation
retry does not consume another nonce; cancel-before-first-send wins atomically; crash after
authorization but before send can rebroadcast identical wire; lost response/expired blockhash
cannot silently become another economic generation. Unknown or incomplete history remains
unknown with an implemented evidence-based recovery path.

### R3 � Freeze a precise DEX execution and quote-consent contract before wire work

The proposed Jupiter �validated transaction execution� needs an exact supported instruction
and route grammar, not a blanket list of trusted program IDs. An approved program can still
move the wrong asset, authority or amount. Decide reviewed supported routes/versions and
reject unknown instructions, writable accounts, address-table substitutions and trailing wire.
For Ethereum name the concrete router/version/pool-fee/approval contract and supported token
semantics. For Tinyman pin current fee/reserves, application/pool identity and genesis.

The immutable user quote must bind chain/network, payer, recipient, full asset identities,
exact-in or exact-out mode, integer amounts, min-output/max-input, slippage, router/program,
all platform/protocol fees, expiry and refund/dust behavior. Expiry prevents fresh admission;
it does not erase a possibly sent swap or authorize replacement. Unlimited approval must
not be inferred from a bounded swap consent. Multi-transaction approval/opt-in stages require
recoverable accounting even when the swap later fails.

Acceptance: tampered instructions/accounts/lookup tables/recipient/allowance rejected before
signing; expired quote versus submitted recovery distinguished; exact integer outputs proven
from the finalized expected pool effect; fee-on-transfer or otherwise unsupported tokens
explicitly discovered, never interpreted as ordinary successful settlement.

### R4 � Make the bridge input's full invariants binding, including prerequisites

The bridge contract input is substantially stronger than the abbreviated lane bullet.
Incorporate it as the mandatory contract baseline, not optional research. Explicitly assign
asset attestation/wrapped registration, target account creation/opt-in, rent, recipient payer
and guardian transport/rotation work; these cannot become post-implementation refusals.

Require six directed route acceptance cases over the three protocol families, with reviewed
environment mappings and native/origin/wrapped asset identity. Pin uint64 decimal sequence,
normalization precision, dust disposition and net recipient amount after relayer fee. A
public protocol support table is neither a configured route nor local protocol evidence.
Canonical body identity and unique emitter/sequence conflict fence must both protect target
claim ownership. Settled proof plus bridge projection commit atomically; DB projection loss
must not reopen redemption. Guardian rotation/expiry and chain finality are distinct checks.

Acceptance: alternate valid guardian signature envelopes collide; conflicting same-message
bodies quarantine; unrelated/multiple publications cannot be selected by first-log heuristics;
attestation/opt-in and lost target response recover; outbound redemption remaining possible
prevents an automatic source refund. Return transfer requires actual current holder authority.

### R5 � Define compensation by confirmed forward facts and DAG causality

�Domain compensation with immutable receipts� needs a matrix: which operations are reversible,
which require recipient consent, which have irreversible external effects, and which have only
local projections. Compensation cannot merely walk EndedAt descending or mark the run Cancelled.
Parallel branches must settle their already-admitted effects first; unwind only dependencies
whose exact forward facts permit it. Preserve fulfilled forward receipts even after a reverse.

Define durable compensation operation/reference, actual actor/tenant and participant grants,
fee quote/caps, current ownership/versions, partial reverse status and operator discovery.
Failures or account changes cannot grant the original actor the recipient's signing authority.
Local rollback needs exact current-version proof; it cannot overwrite intervening legitimate
updates. Child reversals complete only on their own authoritative evidence.

Acceptance: overlapping compensation workers execute one reverse; crash between reverse
settlement and projection recovers; one branch pending prevents early final compensation;
irreversible delivery/deployment and partial reverse truth remain visible; no �settled� or
�refunded� status without actual effect proof. The current truthful park is mitigation only.

### R6 � Separate preadmission cancellation from post-admission rejection/recovery

A stale preview refusal does not establish that another suspended request using its key
cannot still admit. Specify cancel-by-key tombstones that atomically compete with workflow
admission under actor/tenant/action/request identity. A winning admitted row is observed,
never overwritten by the tombstone. Within the engine, an immutable pre-dispatch rejection
marker or atomic Pending-to-Failed epoch mutation must survive a crash before projection.

Acceptance: late suspended admission loses to cancellation; cancel loses to existing run;
changed consent/hash/key payload cannot overwrite either result; no HTTP timeout or missing
lookup permits key rotation; validation refusal recovery never invokes the effect handler.

### R7 � Close keyed template semantics before restoring fluent convenience

Bind template identity/revision, canonical substitutions, author principal/tenant, intended
runner scope and stable copied graph IDs before create. Replay resolves the original authored
action before reading mutable template state. Decide explicitly whether a parent authors an
action for a child or the child authors it; then establish visibility/run access and economic
consent accordingly. Parent authorization cannot silently become child custody authority.

Acceptance: concurrent template requests create one graph; lost instantiate and lost start
responses return the same action/run; template edits and changed substitutions conflict;
child-running/parent-authoring and direct child-authoring positive/negative cases are separate.
Persisted action discovery must survive a cleared browser, not only retain one in-memory ID.

### R8 � Native cleanup and delivery need separate completion/evidence contracts

Native successful deployment followed by failed authority removal is successful deployment
with pending hardening, not generic Failed. Cleanup must bind original buffer/program,
authority, refund destination and actual recovered rent. A separately consented recovery
cannot restart the original effect or reinterpret exhausted old budgets. Full-width AVM IDs
must round-trip as exact decimal strings through API, SDK, stored intent and all metadata/DEX
callers, with no lossy JSON number intermediate.

Emit needs an atomic local outbox+action receipt and immutable payload/destination identity,
explicit delivered/accepted semantics, receiver-stable event ID and retry/dead-letter/operator
journey. Define whether a workflow successor waits for receiver acceptance or outbox admission;
choose and disclose the actual API promise. External receipt replay is at-least-once, not an
exactly-once receiver guarantee. Validate destinations and prohibit credentials/internal
endpoint access being manufactured by arbitrary action-authored URLs.

Acceptance: partial native failures retain actual resources and costs; cleanup budget changes
need fresh bound authorization; delivery crash after receiver acceptance does not create a new
event identity; permanent receiver failure remains discoverable and cannot appear delivered.

## Evidence and deferrals

Retain only the two evidenced Identomat external limits as deferred, and distinguish source
implementation from verified behavior. Explicit public protocol support/configuration and
missing keys can constrain external acceptance, not excuse missing codecs/state machines.
Final gate inventory must include the actual provider adapters and all directed bridge route
compositions, durable store concurrency, caller/browser encoding, account/tenant changes and
bounded authenticated Stripe DEV callbacks/recovery. Local mock success is not public protocol
or vendor acceptance. Main commit/push/ancestry cleanup follows independent runtime evidence.

## Verdict

CHANGES REQUIRED on R1-R8. The architecture is reusable and the scope is appropriate, but the
above authority, consent, reversal and dependency decisions must be explicit before parallel
implementation. Final verdict also waits for the adversarial full-goal assessment and its
reconciled plan delta; this review is not a substitute for that assessment.

## Reconciled assessment delta

The later round3-assessment-adversarial.md and reconciled plan were read after the
initial review. The new findings are covered in scope: direct fungible finalized asset
projection, durable quote admission, source publication plus actual redemption, registered
handler gaps, audited per-run compensation, original-key renewal fences, precision and
capability evidence. Advance/signal changed-payload retry is additionally required.

The reconciliation explicitly addresses part of R5/R6: per-run compensation audit and
retention semantics, and original-key cancellation competing with delayed admission. Keep
those clauses. The remaining corrections above concern concrete invariants, ownership and
acceptance rather than adding unrelated scope. The plan can incorporate R1-R8 as normative
requirements plus named ordered subwaves/reviewers; it does not need finished wire codecs
or every SDK research result before approval. Each later protocol amendment must resolve
its exact route/parser/authority design and pass independent review before corresponding
source implementation. Do not treat that future review gate as permission to defer a
required capability indefinitely.

## Final revised-plan verdict

PLAN_VERDICT: ACCEPTED

The reconciled dual-assessment plan and its binding R1-R8 section were independently
reread. The ordered 3.0-3.3 table names authors and separate reviewers, assigns shared
files, preserves concrete contract review before implementation, and includes every
required source gap from both assessments. All eight review corrections are now binding:
preparation/first-send generations; concrete DEX validation/consent; the normative full
WTT contract and prerequisites; causal compensation; durable cancellation/rejection;
explicit template author/runner/publication authority; native partial results/cleanup;
and honest atomic outbox acceptance versus receiver delivery.

No remaining blocker to beginning subwave 3.0 research and concrete contract amendments.
This is approval of the implementation plan, not approval of unreviewed protocol details
or completed implementation. The plan's independent contract, source-review, Round 4
assessment and final integrated runtime gates remain mandatory. No tests/build/schema or
external economic operation ran for this review.