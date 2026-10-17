---
type: Implementation Handoff
status: Partial source slices; integration and runtime gates open
---

# DEX source slices

## Pure protocol arguments and arithmetic

Author A added `TinymanV2Math.cs`, `JupiterRouteData.cs`, focused unit fixtures and
provider documentation. C independently reviewed the source against the captured
official validator/IDL/Orca utility and accepted this bounded slice on 2026-10-15.
No blocking finding remained. This accepts integer arithmetic and argument
decoding only, not accounts, custody, protocol execution or settled swaps.

Tinyman preserves the validator's unconditional extra output rounding unit,
current pool fees, wide intermediates and uint64 reserve limits. Jupiter accepts
only one-hop Whirlpool variants 17/47, direct/shared route discriminators,
canonical boolean/option fields, bounded supplemental arrays, exact-input
topology, zero aggregator fee and complete byte consumption. Authored fixtures
cover every truncation, appended bytes, changed topology/fees, unsupported hooks,
overflow and exact-division rounding. They have not run.

## Quote contracts and persistence

A added `Models/Dex`, `IDexTransactionModule`, `IDexQuoteStore`, `DexQuoteRecord`
and `SurrealDexQuoteStore`. This slice is submitted to Root for independent review.
Root found prerequisite disclosures outside the quote hash and ambiguous empty
tenant identity. A moved prerequisites into required immutable terms, rejected
Guid.Empty tenant and added focused real-store regressions. Root independently
reread and accepted the corrected bounded source slice. The store uses typed
create/keyed read, rejects mismatched canonical hash/identity,
and preserves a consumed quote on exact replay. The new real-Surreal fixture
covers concurrent identical claims, changed self-consistent terms, reconstructed
store reads, consumption preservation and corrupt scope/hash/expiry refusal.
Schema generation and test execution remain held for the final sweep.

## Request admission and no-send cancellation

`SurrealSwapOperationStore`, `DexSigningAuthorityFence`, `SwapOperationRecord`,
the store interface and identity/authority models are now authored for independent
review. One transaction joins current owner/wallet/grant/action authority, database
quote expiry, single consumption and typed operation creation. Exact request replay
precedes current admission gates. A cancellation tombstone competes on the same
request identity; direct current owners can cancel unprepared operations after
wallet deletion or tenant-grant revocation, while tenant callers retain the grant
requirement. Any current stage prevents this no-send cancellation path.

`SurrealSwapAdmissionTests` authors real-store cases for distinct-key quote races,
cancel/admit races, delayed admission, current user/wallet/credential refusal,
tenant grant expiry/revocation/scope/origin, owner cancellation, changed request and
original replay after expiry. These tests have not run. Stage creation/first-send
transactions and their race coverage are the next bounded store slice; admission
is deliberately not wired to any provider egress yet.

Root found an existing-request replay identity bypass. The store now checks the
authenticated actor and tenant context before replay; only explicit direct-owner
no-send cancellation permits null tenant context for an original tenant request.
Required string fields reject null before dereference. Added scope-replay tests
cover strangers, other tenants, direct ordinary replay and owner cancellation.
Root independently accepted this corrected bounded source slice; no stage,
egress or runtime acceptance is implied.

## Explicit swap fee denomination

The persisted schedule, audited update request, response/public transparency and
SDK disclosure now carry `input-base-units-v1`. Unknown/missing policy cannot
silently quote zero. Legacy zero and nonzero schedules both have an explicit
authorized versioned adoption path; no automatic reinterpretation occurs. The
touched schedule store now uses typed policy/audit writers inside its atomic CAS
transaction. Unit and real-Surreal fixtures cover explicit adoption, unknown
denomination, concurrent first creation, audit consistency and stale-version
refusal. Root independently reread and accepted the bounded manager/store source
on 2026-10-15; runtime execution remains pending. DEX service
integration and operator client update controls remain required later slices.

## Initial stage journal and shared integer parser

Root independently accepted the narrow `UnsignedIntegerText` extraction and
NativeOperationAuthorization compatibility wrapper. The shared parser preserves
canonical arbitrary-precision nonnegative decimal text; protocol callers impose
their separate width limits. Boundary fixtures are authored and unrun.

`SwapStageRecord` and `SurrealSwapOperationStore.Stages` now claim the initial
unsigned plan, retain one validated signed candidate, and separately authorize
first-send or cancel the unsent stage. Both operation and stage CAS writes share
one transaction, and positive authorization joins the current authority fences.
Private storage of signatures does not authorize any provider egress. Real-store
fixtures cover competing plans, signed-state cancellation, authorization/cancel
races, current-user revocation, original authorized replay and immutable wire/
budget refusal. Root independently accepted this bounded stage source slice;
service integration must still recheck authority before custody and refuse
signing a delayed plan replay after cancellation or advancement. Subsequent stage
advancement, no-effect replacement, actual economic completion and cleanup remain
explicit required implementation, alongside the shared authority adapter owned
by B. Nothing in this slice enables a DEX provider capability.

## Tinyman pool and quote slices

C independently accepted TinymanPoolState and its fixtures, including the later
strengthening that derives validator authority using pinned SDK
Address.ForApplication rather than accepting a caller-supplied expected address.
This accepts snapshot parsing/math only; genesis/code/freshness/wire/settlement
remain separate boundaries.

AlgorandProvider.DexQuote now authors the read-only quote boundary with explicit
network/code pins, current pool and asset evidence, available holdings, network
and rent caps, and server-derived retained opt-in disclosure. Its new fixtures
exercise pins, unavailable/stale data, full-width assets, current fees and
opt-in costs. C accepted the corrected bounded quote source, as recorded below. The provider does not yet
implement or advertise IDexTransactionModule; wire and settlement work remains
active. No fixture has run and no public-chain economic operation occurred.

Remaining stage lifecycle integration, actual provider settlement, executor
integration, operator fee update UI, API/action/SDK/UI and real local
protocol fixtures remain active implementation packages, not deferrals. No DEX
capability has been enabled by these disconnected source slices.

## Ethereum executor and actual protocol fixture source

`contracts/ethereum/src/AzoaDexExecutor.sol` now contains the independent policy
permit, exact-input classic Uniswap V3 call, atomic input-asset platform fee,
exact allowance reset, replay domain and balance-delta checks. The existing
pinned compiler script adds this fixed source name alongside AzoaNft. Root's
independent executor source review is pending; no contract was compiled.

`dex-protocol.test.mjs` deploys actual pinned Uniswap factory/pool/router/quoter
and position-manager artifacts on in-process Ganache when the final gate runs.
Authored cases cover exact quoted settlement, slippage rollback, forged policy,
cross-domain replay, native wrapping/unwrapping, prior token dust, changed owner,
trailing ABI bytes, valid second-operation callback reentrancy and refused native
treasury rollback. The fixture provenance and WETH digest are in its directory
AGENTS.md. These authored tests have not run; .NET custody/store integration
remains a separate required gate.

Exact Uniswap dependency lock metadata was resolved with package-lock-only and
ignore-scripts, without installing or running packages. Its metadata audit
reported 10 findings (1 low, 2 moderate, 6 high, 1 critical); final dependency
review must classify and remediate these without conflating local test tooling
with deployed contract runtime.

C independently accepted the Tinyman read-only quote correction: required
output opt-in refuses frozen or unknown asset defaults, while an existing
unfrozen holding remains valid. Its new fixtures remain unexecuted. This closes
the bounded quote source review only, not signing or settlement.

## Immutable stage observations

The additive observation store slice validates saved transaction/network identity,
canonical economic amounts and the stage's reserved costs before committing
operation and stage epochs together. Pending/needs-operator evidence cannot carry
economic completion. Failed-on-chain and expired-absent outcomes cannot claim
output or platform fees; expired absence has no network expenditure. Terminal
stage evidence is immutable, and only moves the operation to StageResolved.
Provider-controlled history evaluation and final settlement remain required.

Real Surreal fixture source covers pending versus completion, pre-authorization
refusal, changed terminal evidence, invalid evidence/costs and competing terminal
verdicts. Root review is pending; no fixture or build has run.

Continuation now uses exactly-once canonical cost aggregates in the terminal
observation transaction, so remaining budgets never assume an unknown cost is
zero. Root rejected an initial lifetime 64-stage ancestry limit; that limit was
removed before activation. The retained predecessor audit pages 25 rows at a
time, while current-stage admission uses the immutable previous proof and
operation epoch. A real-store fixture authors 65 resolved generations followed
by another prepared generation and reads all 66 stages across three pages.
Cleanup has its own cap and monotonic purpose marker; confirmed stages cannot
be regenerated. Source review and runtime validation remain pending.

C independently accepted the actual Uniswap fixture mechanics after adding an
outside-callback success assertion for the same second signed request. This
proves the nested refusal fixture is not merely malformed or unfunded. This is
source acceptance only; executor implementation review and all runtime gates
remain separate.

The Tinyman pure wire slice now builds separate opt-in or canonical fixed-input
groups, including the exact atomic platform fee and preparation-bound notes.
Signed envelopes must reproduce every canonical unsigned byte and carry the
saved owner's actual single-key signature. Its fixtures cover full-width asset
IDs, native/token inputs, zero fee, group order, recipient/rekey/fee/genesis
mutation, trailing bytes and signature corruption. C accepted this bounded slice, as recorded below. The
single-sender signature helper was extracted with B's ownership agreement from
the existing fungible provider; B independently accepted the extraction and
confirmed no fixture depends on its generalized diagnostic text. No runtime
checks were run. New operation admission stamps cost-accounting version 1;
missing legacy versions cannot silently reinterpret default-zero aggregates.

C independently accepted the pure Tinyman wire slice, then the provider
preparation/browser-validation slice after two required fixes: custody must bind
the separately supplied signing context's exact wallet and chain before access,
and saved-plan validation must enforce the original rent cap. Strict mock
never-custody cases and a self-consistent forged underfunded opt-in plan exercise
those fixes. Quote readiness also refuses delegated/rekeyed owner accounts outside
the single-key grammar. The module is still not advertised; submit, authoritative
settlement, cleanup and actual local protocol fixtures remain active work.

Root accepted the shared SigningAdmissionAuthority factory/fence source. DEX's
temporary authority DTO and raw credential/grant fence are now removed. Positive
store operations use the shared exact wallet/credential/grant/workflow witness;
the remaining narrow adapter verifies the immutable quote's exact participant and
workflow run/node/intent before calling the shared fence. Cancellation takes
SigningConsentWitness without inventing wallet authority for a no-effect cancel.
Authenticated actor/tenant identity still precedes replay. Integration fixtures
now use the shared witnesses and cover altered wallet/scope/workflow/grant time.
This migration awaits independent review; service custody/egress integration is
still required.

Root accepted an accounting refinement needed for concurrent external opt-in:
effect confirmation can coexist with unknown actual incremental rent only if the
full original reservation remains separately retained and public accounting
completeness stays false. Reservation must never be encoded as actual rent or
zero. Exactly-once authoritative historical/order proof resolution remains required.
The observation/store refinement is now authored: unresolved reservation/count
are separate durable aggregates; ResolveRentAccountingAsync appends the exact
provider proof and updates known actual/remaining reservation atomically once.
The immutable original observation keeps its unknown actual. A public accounting
DTO distinguishes completeness. A competing prior-opt-in fixture covers retained
cap, duplicate proof races, unchanged original evidence and contradictory proof.
The actual provider history parser and service/API projection remain required;
these store-only proof fixtures are not evidence of protocol execution.

Tinyman submission source is now authored in AlgorandProvider.DexSubmission.cs:
complete saved group/signature/identity revalidation, fresh genesis/validity check,
exact concatenated bytes, and matching first transaction ID response. Expired
wire is not sent; ambiguous send/response failures preserve the original saved
identity. Added actual SDK-signed wire tests for response loss/exact replay,
tampering, genesis mismatch, expiry and wrong remote transaction identity. C has
accepted this bounded source and fixture slice independently. Observation, accounting history,
cleanup and real protocol execution are still required before capability wiring.

Root independently accepted the shared-authority adapter and paired observation,
continuation and late-rent store source. Requested XML clarification now describes
conservative unresolved reservations; transaction construction is outside retry
delegates. This acceptance does not cover arbitrary public evidence injection,
custody fencing, cancellation-to-send races or provider protocol proof.

Tinyman receipt/history source is now authored and independently accepted by C:
TinymanSwapReceipt verifies the exact full-block group, inner output and fixed-input
logs; TinymanOptInAccounting reconstructs prior-round holding state through earlier
outer/inner effects. The provider separates terminal effect observation from later
rent resolution, and requires complete per-member indexed absence through expiry.
Official Indexer schema and converter are pinned at
f84ad3e337a2e6eb111ac548f21dc65df8cc5e70. The converter's repeated parent offset on
inner transactions is explicitly accounted for and tested. Pure mutation and
provider HTTP fixtures are authored, not executed; real local protocol evidence
and domain orchestration remain outstanding.

The first orchestration slice is now authored and awaiting Root review:
`SwapStageCoordinator`, paired live `AdmitCustodyAsync`, exact economic
`CompleteAsync` and immutable settlement receipt. Custody admission never permits
egress; cancellation during signing makes the subsequent save fail its epochs.
Browser envelopes pass the same canonical provider validation and authority
boundary. Recovery observes before replay and never signs new transactions.
Accounting proofs come only from the original module. History is traversed in
pages without a lifetime retry cutoff. Tinyman stage selection validates every
terminal saved candidate and requires exact gross debit, minimum output and
platform fee before completion. New real-store and coordinator/provider fixtures
cover these boundaries but remain unexecuted. Cleanup is separately proposed in
`round3-dex-cleanup-amendment.md`; no structural DEX capability is advertised yet.

C independently accepted Tinyman stage selection source and its fixtures. Root
accepted the cleanup amendment after requiring explicit consent to the current
zero holding and attribution through finalized position. The first cleanup
source seam now exists: immutable new participant command, direct current
authority independent of a historical workflow's Running state, canonical
self-close wire and exact zero-close receipt. Its plan requires fresh proof of
this operation's original 100000 rent allocation, a current zero noncreator
holding and its separate accepted fee cap. Stage identities bind the new consent
hash. Source review, release history/accounting, public orchestration and real
local protocol fixtures remain open; the new interface is not advertised by
providers until those pieces are complete. Release history will persist bounded
page progress through a fixed finalized target position, with no lifetime retry
cutoff or missing-holding shortcut.

The release-history slice is now authored, awaiting independent review. The
provider queries fixed allocation/cleanup rounds in pages of 25 and validates
each returned root against its same-genesis finalized block, walking actual
outer/inner execution order. Inner effects use the root transaction identity
plus a recursive index path because the pinned Indexer does not emit inner IDs.
Each page persists its cursor, digest chain and earliest close. Exhaustion
classifies release as cleanup or an earlier external close; a close/reopen never
gives the later cleanup false attribution. The original allocation row is the
unique release identity. A three-record epoch transaction advances allocation,
cleanup and operation together, preserving historical allocated rent and a
separate cumulative released total. Unknown actual allocation remains reserved.
Provider pagination/earlier-incarnation fixtures and a real-store 66-page,
concurrent-final-proof fixture are authored but unexecuted. These are source
gates; actual LocalNet execution and public domain integration still remain.

C independently accepted cleanup/release provider source after corrections for
unknown history-purpose rejection and persistent all-prior cursor digests that
detect multi-page cycles. Root independently accepted the triple-epoch release
store/proof-only coordinator ingress and full-width swap fee quote arithmetic.
These are bounded source verdicts; no execution or complete capability verdict
is implied. Public quote integration is now being authored in DexQuoteService
and DexModuleResolver, retaining actor/wallet ownership, exact concrete network,
server fee policy and immutable persisted provider evidence.

The public custody-only boundary and fair recovery slice are now authored and
awaiting independent review: `DexController`, `DexQuoteService`,
`SwapOperationService` and its command/cleanup partials, `SwapRecoveryService`
and `SwapRecoveryWorker`, plus store recovery and cleanup-completion partials.
Requests capture actual validated credentials and use immutable scoped keys;
views exclude signed and unsigned executable wire. Cleanup replay preserves
its exact new consent without requiring an old completed action to run again.
An immutable cleanup receipt requires finalized cleanup and complete attributed
release, while preserving any original economic settlement receipt.

Recovery claims advance due time before provider access. Accounting persists a
25-stage traversal cursor before external work, avoiding lifetime lineage caps
and starvation by one unresolved ancestor. Real Surreal fixtures now cover 55
due operations, competing epoch claims, cursor CAS, incomplete release refusing
cleanup completion, and exact completion replay. Public service fixtures cover
lost admission response, actor isolation and cleanup consent replay. All remain
unexecuted. Browser exposure, remaining chain modules, actual protocol fixtures,
action/SDK/UI parity and old-route cutover are still required implementation.

C independently accepted the pure Jupiter account slices:
`WhirlpoolAccountState` (pool identity plus fixed/dynamic tick framing and PDA)
and `JupiterRouteAccounts` (nine/thirteen fixed headers, actual AMM program
prefix, v1/v2 direction and token/vault authority roles). Adaptive fee-tier
identity has a dedicated regression. The official dynamic manager grows and
shrinks account size by exactly 112 bytes per initialized tick, supporting exact
framing. Live account ownership, token authority/balances, compiled privilege
union and actual route settlement remain separate required gates. The bounded
official Build response parser and malformed/contradictory response fixtures are
now authored, awaiting independent review. It does not trust API lookup maps or
advertise executable instructions as settlement.

### External exposure journal and unsigned absence (source review pending)

Browser exposure is now represented independently from server submission authority. The journal seals potential egress before disclosure. An exact real signed candidate may be attached after upload or authoritative on-chain discovery, without setting `AuthorizedAt`. An exposed stage cannot be canceled as unsent. Terminal absence with no signature uses `SwapStageHistory.ExposedPlan` and the provider's original-plan proof, never a fabricated `PreparedSwapStage`; confirmed settlement still requires the actual recovered candidate. Continuation and cleanup validate both kinds of history.

`ServerSubmissionWithdrawnAt` permanently stops subsequent server authorization/replay for that stage while preserving existing admission and exposure evidence. It does not recall an already admitted/in-flight network request, prove chain absence, release budgets, or stop observation. The participant must await authoritative terminal evidence before replacement or cleanup.

New authored fixtures cover exposure versus cancellation, unsigned terminal absence without signed wire, exact candidate attachment and separate live egress authorization, withdrawn-stage observation without rebroadcast, and Tinyman continuation from the exact exposed-plan expiry proof. They remain unexecuted. Public browser disclosure is still gated pending completion and independent review of its transport boundary.

C independently accepted the bounded Jupiter BuildPlan/RouteAuthority source and Tinyman exposed-plan observation source. This covers the closed parser/role unions and actual-signature recovery respectively; it does not claim full-message integration, public admission, or protocol runtime evidence.

The exposure transport boundary is now authored: authenticated `external-signing` explicitly consents and persists exposure before returning only unsigned wire, signer addresses and budget/hash identity; provider recovery metadata is not returned. `external-signature` validates exact wire without server submission authority; `submit` remains separately fenced. `withdraw-submission` preserves potentially-sent history and cannot claim cancellation. Response-loss replay returns original exposed wire for the same actor/tenant without refreshing grants or the plan. Provider structural registration/cutover remains gated until the pending complete-module review; no runtime acceptance is claimed.

### Jupiter current-state preparation

The pure Oracle layout and shared loader-v3 program-data validation extraction received C's bounded source acceptance. `SolanaProgramDataEvidence` replaces the WTT-private helper without changing owner/executable/tag/pointer/full-code-hash behavior. `JupiterRpcSnapshot` and the provider's new `DexState` reader collect actual accounts at one finalized context, require matching Clock, pin both program-data hashes, and apply account grammar and privilege union to the same decoded instruction. Lookup table contents are independently fetched and compared to Build's map. Token holdings, setup instructions, whole-message privilege union and custody/settlement integration remain required; the reader is not a complete signing verdict.

`DexBuild` now uses the documented fixed `/swap/v2/build` endpoint, a request-local API-key header, exact-input request values, no Jupiter fee/tip/native wrapper, bounded response bytes, and a closed response parser. It disables production redirects. Known malformed JSON/route shapes become typed unavailable; transport exceptions are not swallowed in parsing. These new RPC/build integration helpers await focused tests and separate source review. No runtime checks have run.

## Jupiter setup boundary

Authored JupiterSetupInstructions and mutation fixtures for exact owner ATA CreateIdempotent roles and bounded compute limit/price instructions. Extracted WttSolanaCompute to SolanaComputeBudget with unchanged encoding/framing and mechanical WTT callsites; independent C review requested. Program identity verified against installed Solnet XML. No checks executed. Complete account-state and whole-message integration remains required; this pure boundary does not advertise a DEX capability.

C independently accepted the shared compute extraction and ATA/compute pure grammar by source inspection; runtime remains unrun. JupiterCompiledMessage now checks exact ordered instructions, complete resolved static/ALT account set, full signer/writable union, payer and packet bound against provider-reviewed instructions. Mutation fixtures cover changed data/order/privilege/payer/trailing bytes. Actual provider compilation integration remains next.

Root independently accepted the bounded coordinator/exposure/withdrawal source slice: exposure is durable before disclosure, upload does not authorize server send, recovery validates saved wire, and paired mutations preserve operation/stage identity. Withdrawal blocks the next server replay check but cannot recall a send already past its final read. Exposure/cancel and withdrawal fixtures remain unrun. This does not constitute overall DEX completion.

JupiterNativeAccount now supplies deterministic preparation-key WSOL create/initialize/sync/close instructions and rejects changed derived-account identity; source fixture covers generation/key separation and funding overflow. Actual quote/planning/settlement integration remains unfinished. Required remaining sequence: Jupiter full provider lifecycle and local protocol proof; Ethereum executor provider lifecycle and existing real protocol fixture integration; Tinyman local protocol proof/readiness; action/domain and old API cutover; SDK/browser/UI plus fee policy parity; independent final review and integrated runtime sweep. No implementation item is deferred.

Jupiter private compilation now binds the same canonical Build hash and validated RPC role snapshot, compiles explicit compute policy, exact ATA setup, input-denominated platform fee, validated route and dedicated native create/sync/close lifecycle into one message, then verifies full message privileges/bytes against that exact instruction plan. This remains unwired until public quote/preparation validates balances, rent/cost/simulation and saved immutable recovery evidence. Root accepted exact DependencyUnavailable-to-Unavailable quote mapping by source reread.

## Jupiter actual adapter quote wiring

SolanaProvider.DexQuote now drives the real Build parser and finalized RPC account proof, validates exact user balances/decimals and configured immutable programs/guard, compiles guarded ATA/native/fee/route instructions, reads exact message fee and simulates without custody or submission. JupiterProviderQuoteTests supplies official-shape Build and actual binary account fixtures through the production HTTP/RPC adapter; covers compiled v0 message, missing output ATA disclosure, program/frozen/guard mutation and fee-cap refusal. These HTTP/RPC fixtures are unrun and do not claim real protocol settlement. Dedicated native source/public quote cases and complete durable prepare/sign/observe/client cutover remain required. Root independently accepted the additive opcode4 guard source only; C independently accepted full-message/WSOL pure helpers, requested fixture expansions now authored.

C independently accepted the bounded Jupiter quote/compiler/RPC-fixture source snapshot, including native public quote and occupied temporary account refusal; execution/journal/finality remain unaccepted. Root accepted shared SignDurableAsync canonical legacy/v0 conversion, with packet-before-custody and original context mapping retained. New additive SDK api/dex.ts and client lifecycle methods mirror current quote/admission/prepare/sign/expose/upload/submit/refresh/cancel/withdraw/cleanup routes. Fixtures cover response-loss exact replay, full-width amounts, unavailable quotes, separate egress stages and encoded identities. SDK tests/typecheck remain unrun; browser provider signing adapters, UI and legacy cutover are still required.

## Jupiter immutable preparation checkpoint

C independently accepted the frozen-route and quote-pin source slice: original encoded output/slippage produce the exact original floor, including rounding and uint64 boundary fixtures. Fresh market output is discarded; pool/direction/program/guard/Rent/native-address pins and prerequisite consent remain fixed. Runtime is unrun.

New `SolanaProvider.DexPreparation.cs` persists a canonical plan, reconstructs account roles and full compiled bytes from saved raw evidence, validates owner/scope before custody, and validates real signatures over exact bytes. `DexState` now shares pure validation between current RPC snapshots and saved plans. New preparation fixtures cover native identity retention and refusal of newly undisclosed account effects. This new preparation slice awaits independent review; no structural module registration or completed capability claim.

The external-signature history proposal is in `round3-dex-solana-exposure-amendment.md`; observer API changes await its review. Remaining mandatory work remains Jupiter submission/final settlement/history/accounting, Ethereum provider integration, actual protocol fixtures, action/legacy cutover, and browser/UI parity.

The additive SDK DEX command/types/export slice received Root bounded source acceptance, runtime pending. Preparation review identified a pre-custody fee/expiry gap; signing now checks finalized height and exact saved-message fee before custody, and submission repeats the reservation guard before replay. Focused delayed-expiry/increased-fee fixtures are authored and unrun. The submission fixture signs with an actual Solnet account and asserts response-loss retry sends identical bytes; expiry triggers observation without another send. This new preparation/submission delta awaits reviewer reread.

C independently reread and accepted the Jupiter pre-custody fee/expiry corrections, explicit required-evidence guards, exact operation/stage/generation Memo commitment, real-signature replay and finalized submission checks. Runtime remains unrun. C also accepted the shared finality extraction with thin WTT forwarding wrappers; all prior WTT proof checks are retained. New JupiterSettlementEvidence source is in authoring and not yet wired or reviewed; it is not a completion claim.

## Jupiter finalized observation source review packet

New `DexObservation` uses the shared finality proof before reporting exact settlement or finalized failure. New `JupiterSettlementEvidence` received C bounded source acceptance before wiring: exact gross input and treasury fee, recipient floor, owner SOL conservation, actual retained ATA top-up and dedicated native account closure. New `DexHistory` is now authored and awaits independent review: positive owner-index discovery never proves absence; an eight-block pass follows exact finalized parent hashes through the complete original-context interval, persists a bounded cursor, and only complete expired ancestry produces absence. Missing blocks, invalid parents/hashes/body arrays retain unresolved reservations. Authored fixtures cover multiple passes, null/pruned blocks and corrupt ancestry; no tests executed.

`IDexTransactionModule` observation methods now accept optional prior provider observations after CancellationToken. Only the coordinator reads these from the persisted stage. Algorand ignores this new continuation argument; no public cursor input was added. Domain mock signatures were updated. This observer/coordinator delta is awaiting independent review and does not advertise a completed DEX module.

C independently accepted the bounded finalized observer/history source after reread: actual saved/recovered signatures, exact shared finality, address history as discovery only, and complete descending finalized ancestry. The acceptance assumes coordinator-owned prior cursors and finalized preparation context; real protocol execution remains pending.

## Jupiter cleanup integration packet

Root accepted `round3-jupiter-cleanup-amendment.md`. Additive Rust guard opcode 5 and `JupiterZeroHoldingGuard` enforce exact zero ordinary/native balance, including unsynchronized SOL and strict SPL option tags. `JupiterCleanupWire` binds separate owner consent, original disclosed ATA, fixed preparation identity and owner-only close. `DexCleanup` rereads current owner/holding and immutable guard, checks exact fee/simulation, persists canonical evidence, and reconstructs the full message through the existing signing/exposure/submission path. Cleanup custody uses the new consent tenant rather than requiring the completed original workflow. `JupiterCleanupEvidence` checks actual zero token removal and exact owner lamport return; its receipt does not itself credit original rent. These new paths and fixtures await independent source review and remain unrun.

The remaining required implementation order is: complete Ethereum provider quote/allowance/nonce/executor/receipt recovery; author actual Tinyman/Jupiter/Ethereum protocol adapter fixtures; migrate frozen action and legacy swap callers; complete browser, ordinary UI, cleanup and fee-policy UI parity. Follow with separate review/pruning and the final integrated execution gate. These are implementation work, not external deferrals.

Root accepted the bounded opcode 5 Rust and C# guard source. C then independently accepted cleanup wire/evidence/planning and `DexRentRelease`/`JupiterCloseHistory`: complete parent-linked blocks retain the earliest successful exact SPL close after the proven allocation, including inner-instruction order; allocation-transaction close remains explicitly ambiguous. Credits use the original attributed amount rather than current account balance. New provider fixtures exercise real signature reconstruction, retained allocation, separate zero-economic closure, three history passes, repeated lost-progress page, missing block refusal and earlier same-block close/recreate attribution. Fixtures and actual guard/program execution remain unrun; the new fixture source awaits review.

C subsequently accepted the provider cleanup fixture source after correcting the
signature-only block response to include the same external close/recreate
transaction as the full block. No fixture or protocol execution has occurred.

## Ethereum reservation and quote checkpoint

Root accepted the unsigned allocator/explicit owner descriptor design. Source
now separates reservation from custody while preserving the old signed wrapper;
the reported canceled-signed replay gap is corrected before cached wire return
and after a lost signing CAS. Authored fixtures cover canceled states and delayed
custody cancellation. Root reread of those corrections remains pending.

C accepted the pure 19-word executor tuple, EIP-712 digest/sign helper and strict
independent policy-key lookup, plus the shared pinned-block code/call/uint
extractions. `DexState`/`DexQuote` use actual finalized RPC evidence, original
token decimals and explicit gas ceilings. An explicit bounded execution lifetime
is retained in the quote separately from admission expiry. Quote and new
preparation fixtures are authored, unrun and awaiting independent reread.

The allowance store atomically serializes physical genesis/chain, canonical
owner, token and executor. Only a saved confirmed receipt binding that exact
resource and zero remaining allowance can release it; canceled never-exposed
operations have a separate atomic reclamation path. Root requested canonical
identity and exact proof hardening; both are now authored with negative fixtures.
The new POCO still needs final schema generation.

`DexPreparation` reserves original unsigned bytes only after actual allowance
prerequisites, then reconstructs the complete private permit/call/fee identity on
replay. `DexSigning` supplies fresh caller custody context and validates canonical
signed type-2 bytes. These are not yet a complete provider module: actual submit,
hashless exposed nonce-winner observation, finalized settlement, generation
recovery and allowance cleanup remain in progress. No legacy caller was cut over
and no readiness flag was enabled by this checkpoint.

## Round 3 resume (Claude Code lane C, 2026-10-16)

Source-only; no build, test, lint, schema or protocol execution. Record of items:
`round3-lane-C-resume.md`.

Ethereum module completed as source: exact-saved-wire submission fenced by the
allocator row, finalized nonce and executor deadline (never marks the shared row
`SubmissionAuthorized`; the generic worker skips owner-descriptor rows);
finalized settlement through the closed `EthereumDexReceipt` grammar with
`AllowanceAfter`/`AllowanceRelease` evidence; bounded persisted finalized
nonce-winner search producing alternate-signature `FinalizedCandidate`,
exposed-plan candidate adoption or plan-bound `ExpiredAbsent`; stage lineage with
live-allowance first-stage selection inside the consented prerequisites; and
`RevokeCurrentAllowance` cleanup (`approve(executor, 0)` at a new nonce, separate
cleanup cap, consent tenant). Allowance resources are released by the provider
from the saved confirmed stage (`IDexRetainedResourceModule`) and self-heal on
contention in the allowance store.

Cleanup consent is now two mutually exclusive semantics with their own flags;
Tinyman/Jupiter planners require the close semantic. All three providers expose
`DexCapability` and `DescribeDexNetworkAsync`; `GET /api/dex/capabilities`
lists declared modules only. Structural `IDexTransactionModule` declarations stay
off until the independent review. The Swap action node is a recoverable handler
over `SwapActionResolver`; the legacy `/api/swap`, `SwapManager`, `IDexAdapter`
(including the Tinyman instructions-as-success builder), legacy request/response
models, validators and their mock tests are retired. SDK, swap dashboard, wallet
fund dialog and test page now use the durable lifecycle; the SDK workflow
`SwapConfig` mirrors the frozen quote request and the SDK gained governed fee
schedule read/update for the swap denomination.

New flow fixtures: Ethereum settlement/winner/pruned/exposed/submission/lineage/
cleanup/recovery-skip provider fixtures over a scripted finalized chain;
allowance-store self-heal (real Surreal); coordinator retained-resource release;
action resolver projection/terminal/drift/unenabled; cleanup-contract table;
capability discovery; SDK capability/revocation/fee-schedule; and an actual
pinned Uniswap V3 + executor .NET protocol fixture (`EthereumDexProtocolTests`
with `Fixtures/Ethereum/dex-chain.mjs`). None has run.
