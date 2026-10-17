---
type: design-amendment
---

# Ethereum prepared NFT and restitution implementation

Status: design accepted after independent adversarial review; implementation and source review in progress.

## Scope and identity

Complete the existing Ethereum provider family (skill path3), retaining immutable
Ethereum/Devnet|Testnet|Mainnet instances and protocol identity eip155:<chainId>.
Server custody preparation/submission is server-only; SDK consumes the same public
NFT lifecycle API, never receives signed wire or private signing context. Existing
ERC20/native browser transfer capability remains separate. Bridge capabilities stay
absent. New NFT contract configuration defaults empty outside explicit development
setup. No successful deployment or testnet evidence is claimed.

## Durable nonce ownership across every sender egress

Introduce provider-owned IEthereumPreparedTransactionStore and Surreal persistence.
A preparation row is keyed by protocol identity + recovered sender + stable
PreparationKey, with immutable call fingerprint, decimal uint256 nonce, signed wire,
transaction hash and lifecycle status. A unique index on protocol/sender/nonce
prevents replacement collisions across processes; nonce sort is fixed-width decimal
so ordinary typed reads can select the highest reservation. Candidate nonce is
max(node pending count, highest durable reservation+1). Concurrent duplicate nonce
inserts retry; duplicate preparation identities compare the immutable call instead
of allocating another nonce. No unsigned/signed nonce is silently released.

NFT preparation keys already include lifecycle/stage/generation. Value preparation
keys will include exact restitution key and durable generation, incremented only
when the previous preparation has a final no-effect verdict. Missing preparation
keys refuse before allocating. Existing BuildSignSubmit must use this SAME allocator
and durable preparation path, not its current in-process-only high water mark.
Ordinary direct calls get a durable owner operation identity at admission; stored
prepared calls are recoverable after a caller crash. Sign failures retain the owned
nonce and immutable intent so retry can finish the same preparation. A provider-owned
recovery sweep resumes admitted preparations and submits only already-authorized
stored bytes; it never reconstructs a different economic call at an occupied nonce.
Prepared wire alone does not authorize egress. NFT/value callers must first win
their own lifecycle submission transition, then the module submission path durably
marks SubmissionAuthorized before network egress. The provider worker only replays
that state. Owner recovery cannot directly resume NFT/value preparations; it routes
through the owning lifecycle. Nonce cancellation remains available for abandoned
preparations without authorizing their original economic effect.

Receipt confirmation requires exact hash/from/to/nonce/value/input/chain identity,
canonical block hash and finalized block coverage. Missing receipts remain unknown.
A revert proven final permits a new generation. A forgotten/underpriced transaction
cannot be treated as absent merely from pending-count changes.

Nonce recovery is required functionality. An authenticated owner route lists safe
preparation views and permits resume or cancellation with a reason and explicit
maximum gas authorization. The durable owner user/tenant and original SigningContext
are checked against the caller's current signing authority. A node operator can
inspect and request reconciliation but cannot impersonate a user signer. Platform
signer cancellation requires the existing explicit node-operator authority. The
worker may finish an already-authorized recovery but cannot create authorization.
Cancellation prepares a zero-value self-send at the exact reserved nonce, persists
its immutable bytes before send and records it alongside the original candidate.
It uses an explicitly authorized replacement fee cap and rejects an insufficient cap
without broadcasting. Only canonical finalized cancellation proves the old effect
cannot execute; a racing original receipt wins if it finalized first. An unsigned
hole can be resumed from its authorized intent or canceled the same way. Underpriced
calls can be canceled with a newly authorized cap; retries reuse one cancellation
candidate until a further explicit replacement is authorized. Both originals and
replacements remain observed, so no pending attempt is discarded from reconciliation.

Activation requires quiescing every old API/worker process before enabling the new
allocator. The setup command records a per-chain signer checkpoint only after old
instances are stopped, compares latest/pending nonce and operator-supplied known
pending hashes, and refuses unknown pending nonce ranges. A fresh local signer can
activate at a clean latest=pending checkpoint. Existing ambiguous pending work needs
authoritative import or final settlement, never an assumed empty queue. Every new
egress entrypoint requires that checkpoint; no rolling mixed-allocator deployment is
supported. This is enforced by readiness and documented in the executable dev setup.

## Atomic NFT effect and original-actor native fee

A reviewed AZOA ERC721 contract is required; arbitrary ERC721 contracts cannot
safely be assumed to implement fee forwarding, credential authority or revocation.
Add source, pinned compiler/build recipe, ABI and deployed-code hash artifact.
Provider readiness requires configured contract, expected runtime hash and authority
matching chain state. Deployment remains explicit; the adapter does not deploy as a
side effect of an NFT request.

The contract accepts an EIP712 authorization for one exact NFT effect. Its typed
message binds operation identity, action, token/from/to/metadata/soulbound identity,
fee payer, native amount, treasury, quote revision, deadline, chain ID and contract.
The immutable provider intent is extended through the core author with logical
operation ID, logical operation kind, stage, generation, actor user/address/context,
issuer user/address, and the explicit effect authorization address/context. Stage
signer, effect authority and outer gas/fee payer are separate named roles. Ordinary
transfer/burn authority is the current owner. Credential creation/delivery/revocation
requires platform credential authority AND an issuer-user permit; issuer address is
persisted at creation and checked at revoke. The original issuer actor context is
captured by the service before switching stage signing to the platform context.
No provider derives issuer identity from the platform signer. The original actor
submits the outer payable call when a fee is charged; the contract verifies the
separate authority permit(s). Non-fee stages use their stage signer as outer payer.
The quote including explicit zero is present on final stages, with a separate marker
for intermediary stages; absence never represents an authorized zero-fee final call.

The contract validates authorization and current NFT authority, marks the unique
logical effect consumed, executes the ERC721 change, and forwards exactly msg.value
to the quoted treasury in one reverting transaction. A failed fee forwarding or NFT
effect reverts both. Replay cannot charge twice. The final stage alone carries the
fee; internal accept/return stages have none. A mint token ID may be deterministically
reserved but is not reported minted until exact confirmed Transfer evidence and
ownerOf/URI/credential authority reads agree. Credentials retain immutable issuer and
nontransferability; revocation checks that issuer, never holder identity.

Every execution, including ordinary NFTs, also requires a distinct platform policy
permit over the same payload. This attests the persisted server-selected quote;
an owner permit alone cannot forge a zero amount, revision or treasury. The policy
signing context preserves the original actor's tenant consent boundary.

Use pinned OpenZeppelin ERC721/ECDSA/EIP712/ReentrancyGuard implementations. All
external transferFrom/safeTransferFrom overloads, approve/setApprovalForAll and burn
are disabled in favor of the checked execute entrypoint; standard read interfaces
remain available. Internal _update enforces soulbound lifecycle transitions even if
future entrypoints are introduced. execute rejects unexpected stage/kind combinations
and checks owner/issuer/platform authority for every transition. The permit binds
logical operation ID/kind, stage/generation, explicit final-effect flag and immutable
fee quote, in addition to chain/contract/domain and exact NFT parameters. Consumed
logical effects and per-stage permits are tracked separately. An intermediary permit
cannot perform a final effect or skip its fee. Cancellation consumes no NFT permit.
Treasury forwarding and receiver callbacks execute under reentrancy protection;
failure reverts both fee and NFT state, including replay markers.

Development ships an executable local-node deployment/setup script, pinned compiler
and dependency lockfile, deterministic ABI/runtime artifact generation, and a fixture
that deploys the exact artifact. Setup verifies chain ID, deployed runtime hash,
credential authority and allocator activation checkpoint before writing a development
overlay. Provider readiness repeats those checks before preparation. Tests deploy to
an isolated local EVM without external funds; source compilation and deployment are
part of the final integrated sweep, not claimed successful in advance.

## Required adversarial evidence

- Concurrent different operations and crash/restart cannot reuse one sender nonce;
  same preparation retries return the same signed transaction.
- Crash before/after sign and before/after submission has a durable recovery owner;
  no transaction becomes permanently orphaned merely from a lost response.
- Wrong chain/sender/target/calldata/value/receipt block, missing/zero receipt status,
  stale nonfinal receipt and nonce replacement all fail closed.
- ERC721 mint/transfer/burn/credential revoke plus nonzero native fee are atomic;
  original payer bound, quote revision immutable, no fee on no-send and no double fee.
- Contract signer/issuer/owner/metadata/soulbound mismatch and reused authorization
  cannot change ownership or charge a fee.
- Unsigned nonce holes, underpriced pending calls, racing original/cancellation,
  wrong recovery caller and old/new allocator coexistence fail safely or recover
  through the explicit authenticated route; cancellation never authorizes NFT effect.
- Direct ERC721 entrypoint bypass, intermediary-as-final permit, cross-chain/contract
  replay, treasury reentrancy and receiver callbacks cannot bypass authority or fees.
- Exact signed-wire/ABI vectors, typed Surreal concurrent nonce claims, .NET/SDK
  contract parity. All runtime gates run in the final integrated sweep.

## Review questions to settle before source

The EIP712 authorization/fee contract is a concrete new on-chain surface and requires
independent review of permission/replay semantics before implementation. The recovery
sweep must not broadcast a newly reconstructed call after an ambiguous submission;
its durable row retains signed bytes and original authorization. Contract deployments
and configured live keys are external activation prerequisites, not grounds to omit
local implementation or contract tests.
