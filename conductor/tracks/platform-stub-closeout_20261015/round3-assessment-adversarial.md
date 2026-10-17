---
type: Assessment
title: Round 3 adversarial platform closeout assessment
status: Implementation remains required; runtime acceptance not yet performed
---

# Verdict

The platform cannot yet satisfy the requested no-stubs closeout. Rounds 1 and 2
have substantial real identity, fiat, NFT, native deployment, wallet authority,
and durable workflow implementations. Independent source reviews are useful
progress, but none substitutes for the deliberately deferred integrated runtime
sweep. This assessment is read-only against the shared working tree on 2026-10-15;
it does not approve the author's earlier packages or claim test execution.

The next implementation plan must cover the concrete gaps below. Safe refusal is
necessary while a capability is incomplete, but does not complete that capability.
No item below qualifies as an external deferral merely because it spans chains,
requires a protocol fixture, or needs a durable recovery model.

## Ranked findings

### P1: fungible creation currently reports a transaction hash as a created asset

`Providers/Blockchain/Algorand/AlgorandProvider.cs:1830` returns a successful string
containing the transaction ID with a PendingConfirmation marker when ASA creation
has broadcast but has no confirmed asset index. `Managers/FungibleTokenManager.cs:224`
checks only error/nonempty result; at line 236 it assigns that string to `AssetId`,
then registers the reserve asset and completes its idempotency record around
lines 247 and 277. `Services/Actions/Handlers/FungibleTokenCreateNodeHandler.cs:76`
can link that supposed asset to a Holon and return success. This is a concrete
false-success defect, not merely a missing action recovery wrapper.

Required: a durable provider-owned prepared ASA-create operation with exact wire
identity before send, explicit pending/finalized/no-effect outcomes, authoritative
transaction-to-created-asset proof, and idempotent reserve/Holon projection only
after that proof. A lost response must not remint or leave an unrecoverable generic
InProgress claim. The direct API and action adapter must share the lifecycle.
Author pending-with-hash, crash-before/after-send, duplicate invocation, finalized
asset-index mismatch, reserve registration repair, and Holon-link retry tests.

### P1: DEX transaction construction and action settlement remain incomplete

`Services/Dex/TinymanDexAdapter.cs:161` returns instructions for the client to build
a transaction, with no transaction group; line 172 even substitutes the quote ID
for TokenIn. This is an implemented response-shaped stub. Jupiter returns unsigned
wire at `Services/Dex/JupiterDexAdapter.cs:126`, while
`Services/Actions/Handlers/SwapNodeHandler.cs:42` treats transaction construction
as successful workflow execution. Successors can run without a swap occurring.
`Program.cs:1367` and `:1369` register only Jupiter and Tinyman; the planned Ethereum
DEX adapter remains absent. Provider Swap/Exchange entry points are separate
capability refusals, so surface parity must be explicit.

`Managers/SwapManager.cs:87` intentionally ignores the supplied idempotency key.
Its memory cache at lines 124-155 binds only chain name and raw quote payload,
not a durable network/actor/wallet/asset/amount/expiry admission. A quote cannot
safely become an automatic durable action merely by broadcasting the returned
bytes or retaining the old success mapping.

Required: reviewed concrete protocol implementations, immutable quote-bound
transaction construction, exact signer/destination/slippage/fee/network checks,
explicit quote expiry, and durable admitted send/observe semantics for actions.
Keep client-side unsigned construction distinct from settled execution. Pin the
current official Jupiter endpoint/schema before adapting it; the existing v2 URL
and request shape are not fresh verified evidence. Preserve HTTP 200 Unavailable
for legitimate absent pools/unreachable environments. Verify manipulated upstream
wire, changed quote fields/network, expired quotes, lost responses, partial groups,
finality, and local real protocol settlement. Ethereum must not remain an absent
adapter disguised as configuration work.

### P1: Wormhole has neither complete source publication nor real target settlement

`Services/Bridge/WormholeProtocol.cs:53-65` always refuses source message observation.
`Providers/Blockchain/Algorand/AlgorandProvider.cs:789` moves ASA units into a vault
but does not publish a Wormhole token-bridge message. Reading an emitter sequence
alone therefore cannot finish this route. Only Algorand implements a real source
leg; Ethereum and Solana implement neither source nor target, and the only target
implementation found is the explicitly simulated provider.

`Services/WormholeAdapter.cs:359-364` transports VAA bytes as TokenUri, fabricates
`seq:<sequence>` as source token ID, and passes amount 1 to a generic wrapped-mint
interface. This compatibility-shaped handoff is not a real Wormhole token-bridge
redemption contract, despite the hardened guardian verifier preceding it.

Required: provider modules for actual source publication and canonical message
observation, typed verified transfer payload/asset/amount/recipient/network facts,
actual target token-bridge redemption and wrapped-asset authority, and durable
burn/release return semantics. Preserve VAA quorum, replay consumption and exact
once invariants. Reject wrong emitter, token, amount, target and recipient;
exercise source-confirmed/response-lost, proof delay, target-send ambiguity,
duplicate redemption, finality/reorg policy and reverse-leg recovery on real local
contracts/programs with pinned artifacts. Do not reuse a plain reserve transfer
as evidence of a Wormhole publication or generic Mint as redemption.

### P1: remaining registered action handlers still lack their reviewed recovery lane

The registry at `Services/Actions/ActionNodeHandlerRegistry.cs:15-20` wraps local,
NFT and closed-catalog reads; governance/deployment declare their own contracts.
The following registered handlers remain outside the completed reviewed lanes:
Hold, HoldRefund, Release, ReleaseIntoHold, FungibleTokenCreate, Emit,
ActionGraphBundleGenerate, BlockchainExecute, and the planned Bridge/Swap/Back
protocol dependencies. Their underlying managers may have individual idempotency
guards; that does not establish frozen action admission, recovery references,
truthful successor conditions, or projection completion.

A concrete authority defect exists in
`Services/Actions/Handlers/BlockchainExecuteNodeHandler.cs:26-29`: it calls
`IBlockchainOperationManager.GetAsync(id)` without the acting caller, then marks
any nonerror operation result successful, including a pending/failed operation.
Do not put this handler into the pure-read catalog without caller ownership and
explicit operation-state mapping. Fungible has the separate defect above. Emit
has an external webhook/outbox effect and cannot be called a pure read. Bundle
Generate writes a durable artifact and requires receipt-based recovery.

Required: finish the registered-handler matrix with actual domain reference,
immutable key, authority fence, observation method and terminal condition for
each row. Author crash after effect/before action receipt tests for each exceptional
handler; resolve read-only BlockchainExecute authority and status explicitly.
Remove the CredentialRevoke activation gate in
`Services/Actions/NotEnabledActionNodeTypes.cs:8` only after its already-authored
source/authority/recovery review is formally accepted. Leaving a permanent review
gate is not implementation closure. Do not redo the already-reviewed read,
local-mutation, NFT or governance machinery merely to fill this matrix.

### P1: workflow compensation parks effects without a usable domain resolution

`Services/Actions/Workflow/ActionCompensateStepHandler.cs:24-30` parks any completed
or failed non-read effect at `operator:compensation:<run>`. It performs no reversal
or evidence-backed acceptance of retained effects. The existing operator action
recovery API retries an original node's admitted operation; it does not resolve
this per-run compensation gate. A forever-parked workflow is not a completed
compensation implementation.

Required: a reviewed per-domain compensation policy and durable effect receipts.
Some effects are reversible, others must be explicitly retained with evidence and
an authorized resolution. Never reinterpret compensation as an automatic reverse
payment or allow an operator absence assertion to cancel possibly-sent value.
Expose a usable audited journey and terminal semantics. Test mixed read/mutation/
economic runs, partial success, duplicated compensation, changed ownership,
pending original effect, consent requirements, and crash during reversal.

### P1: partial Solana deployment has no executable cleanup journey

`Services/Deployments/DeploymentService.Recovery.cs:71-79` refuses cancellation once
any stage has confirmed and asks callers to explicitly recover created resources.
`Providers/Blockchain/Solana/SolanaNativeLoader.cs:56` contains CloseBuffer encoding,
but no call to it was found in the native preparation/operation flow. A confirmed
CreateBuffer followed by a rejected or abandoned later stage locks rent without
an exposed durable cleanup operation. Current deployment UI offers continuation
or cancellation, and cancellation cannot perform this recovery.

Required: typed cleanup admission for the exact original buffer/program resources,
authority and rent destination, proof that cleanup does not destroy a live deployed
program, immutable wire and finality receipts, and usable SDK/UI controls. Preserve
original deployment history and fees/rent accounting. Test crash after buffer
creation/upload, failed deploy/upgrade, pending original transaction, wrong owner,
wrong refund recipient, duplicate cleanup and races with successful deployment.
No cleanup is required for an unknown pending effect until its outcome is proven.

### P2: a definitively rejected workflow start cannot yet renew its consent request

The new panel deliberately retains the original start key, consent and published
hash after failures. `Managers/ActionManager.cs:1553` can reject a stale published
hash before admission, but the API returns the same generic failure shape as other
outcomes. The panel cannot safely distinguish authoritative non-admission from a
lost response to an admitted request, so it cannot renew a stale request.

Required: a durable request-status/cancel-before-admission or equivalent typed
rejection disposition bound to the original key and immutable intent. It must
fence a delayed original request before the UI is allowed to clear it and obtain
new consent. Reuse the reviewed deployment request cancellation pattern where
appropriate. Do not clear a key based on an error-message substring, timeout or
client assumption. Test late first request, lost response, changed published
revision, exact replay, and account/action scope.

### P2: broad advertised capability closure needs a final evidence matrix

The source scan also finds intentional BaseBlockchainProvider default refusals,
unsupported algorithms/chains, explicit production-blocked simulation, test mocks,
and input placeholder labels. These are not all product stubs and should not be
mechanically removed. Conversely, removing TODO strings is not evidence of a
working capability. Produce a final route/provider/SDK/UI matrix for the advertised
three chains and active features, naming protocol limitations separately from
missing implementation. Retain an honest explicit Ethereum faucet restriction
instead of inventing a public faucet provider.

For identity, keep the real Identomat KYC/KYB adapters and the authorized mock HTTP
fixtures, tenant authority, exact attempts, authoritative callbacks and fail-closed
approval gates. For fiat, retain test/live mode isolation and all durable on/off-
ramp, refund, transfer reversal and inbox replay work. Reassess any new gap revealed
by execution rather than blanket approving those lanes because their source was
reviewed earlier.

## Required integrated acceptance after Round 4

No build, test, schema emission, typecheck, compiler, local-chain deployment or
payment effect was executed for this assessment. The final gate must include:

- Generated Surreal schemas and golden parity for every new POCO, real Surreal
  contention and mutation semantics, all .NET projects, and warning regression.
- Actual authorization pipeline cases, wallet canonical migration and single-use
  claims, KYC/KYB/AML evidence fencing, hosted exact-key replay/key rotation and
  callback-before-attachment failures, governance cutoff/evidence races.
- Actual local Ethereum/Algorand/Solana provider fixtures using durable Surreal
  stores, independently pinned local genesis, custody scopes, canonical wire,
  effect-at-transaction-position proof, finality and replay. Authored fixtures
  are not passing evidence. The newly pinned AVM Compose fixture is under
  independent source review and has not been started.
- Bounded real Stripe DEV verification with existing test credentials and signed
  webhook replay, concurrent refund/payout recovery and environment-switch tests.
  Never substitute LIVE credentials or public-chain funds for this gate.
- SDK build/tests, frontend typecheck/lint/tests/build/audit, active client flows,
  operator policy and original-request retention across lost responses/accounts.
- Batched remediation of observed failures, independent verification, then the
  required main commit/push and ancestry-proven remote branch cleanup. A large
  dirty shared main tree is not the final deliverable.

## Deferral boundary

The two recorded Identomat entries remain narrowly supported: the user confirmed
missing dashboard workflows and authorized mocked verification; the documented
vendor create API lacks an authoritative absent/created lookup or idempotency
contract, so ambiguous sent admissions require verified attachment rather than
blind recreation. Their real workflow acceptance remains external work. All ranked
items above are implementation or verification work and belong in the next plan,
not `deferred.md`. No new external blocker is asserted by this assessment.

## Evidence and review boundaries

Targeted `rg --hidden --no-ignore` / `rg -uuu` scans covered Services, Managers,
Providers, Controllers, Core, Interfaces and SDK/frontend surfaces. Budgeted scan
captures live in `.omc/research/round3-{stub,capability,other,success,client}-scan.log`.
Known Round 2 deployment admission authority and operator read-domain corrections
are present in the current tree and are not reopened merely from old findings.
The run-panel selection guard correction authored by root was independently
reread: attaching or mismatched selected run now refuses advance/signal/reconcile
before send, with disabled controls. Runtime evidence remains pending.
