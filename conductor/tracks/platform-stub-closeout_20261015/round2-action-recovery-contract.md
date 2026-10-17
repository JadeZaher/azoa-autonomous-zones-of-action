---
type: Contract Proposal
title: Action handler recovery matrix and pending-operation envelope
status: Shared envelope accepted; local receipt and governance designs require separate review
---

## Shared envelope

`ActionExternalOperationReference` contains serialized `Version=1`, `Domain` (server registry key), `RequestKey` (stable run/node identity), nullable `OperationId` (deterministic or attached once), `IntentHash` (SHA-256 frozen effective node input plus domain artifact identity), `ActorUserId`, and `ActingTenantId`. Domain/request/intent/actor/tenant are immutable. OperationId can move from absent to one verified domain identity, never be replaced. The node execution persists the reference and frozen effective input before handler admission or egress; a returned pending result is not the first place the request identity becomes durable.

Canonical input is UTF-8 JSON with ordinal-sorted object keys recursively, original array order, decoded strings serialized with the platform JSON encoder, and number tokens preserved exactly; duplicate object keys are rejected. The exact canonical payload is persisted with lowercase SHA-256, and recovery hashes those saved bytes. Numeric spellings intentionally remain distinct inputs. The input includes resolved config, node type, actor/tenant, domain request and frozen artifact identity. Unknown envelope versions/domains, mismatching hashes, changed actor/tenant, or changed input fail closed. Recovery never reconstructs input from a mutable latest graph. Domain observation and rebroadcast of previously admitted exact wire do not create new consent; any fresh local write revalidates the current actor/tenant capability through existing authority services, without inventing a principal. Legacy Running rows without this envelope or a provable receipt become evidence-based NeedsOperator records and cannot be automatically reset.

`ActionNodeHandlerResult` gains an optional pending envelope, with domain state and output for display. Pending is distinct from error and success. Registry recovery returns Pending, Succeeded, Failed, Cancelled, or Ambiguous; only domain-authoritative terminal proof can drive terminal node state. The resolver receives persisted reference/frozen input and resolves registered server code, never a caller URL or a fabricated signing context. Expected waiting parks a saga with a bounded polling timer without consuming the ordinary failure budget. Pure computations may replay; mutation recovery must satisfy the matrix below. Missing node records are a persistence fault, never a completed step.

Lane C agrees Domain=`deployment`, deterministic actor/tenant/request operation identity, and domain intent containing immutable target/network/artifact/authority/budgets. Its resolver owns deployment status/proof. Lane B owns DTO, registry, node persistence and orchestration. Platform artifact invocation needs a frozen action snapshot entry point into the existing interpreter with the actual caller/tenant and normal consent/quota checks; it must not load a mutable latest definition or inherit deployer signing power.

## Handler matrix

Inventory is the registered `Services/Actions/Handlers/*NodeHandler.cs` surface. The entries below describe current boundaries and required recovery work, not a claim that every row is already idempotent. All stable request identities additionally bind authenticated tenant and effective config/input digest.

| Handler(s) | Category and current admission boundary | Required stable identity / resume / successor condition |
| --- | --- | --- |
| Condition, ComposeOutputs, GateCheck | Pure computation or wait against persisted upstream/input | Frozen execution input; recompute/read current gate; succeed only actual condition/outcome. Gate wait remains parked. |
| Search, HolonQuery, HolonGet, HolonGetAncestors, HolonGetChildren, HolonGetDescendants, HolonGetPeers, HolonCompose | Read/projection | Frozen caller/tenant/input; repeat authorized read. HolonCompose is a read projection, not native deployment. |
| WalletQuery, WalletGet, WalletGetPortfolio, NftQuery, NftGet, NftGetMetadata, UserNFTGetComposite, LoanBalance | Read/projection | Repeat current authorized query; unresolved chain ownership denies access. Outputs snapshot at terminal execution. |
| BlockchainExecute | Existing code reads an operation by configured ID; it does not execute one | Domain `blockchain`, exact configured operation ID/actor/network/intent; pending until terminal operation proof, no success merely because Get succeeds. Verify ownership rather than exposing arbitrary operation. |
| HolonCreate | Local create currently generates new ID | Domain `local-mutation`, deterministic run/node result ID + immutable payload; atomic create/receipt or duplicate-aware lookup. No new random row on replay. |
| HolonClone | Multi-row local mutation currently creates fresh IDs | Deterministic clone tree IDs + source snapshot and mutation receipt; atomic bounded tree operation or durable staged receipts. Resume existing tree, never clone twice. |
| HolonUpdate, HolonDelete, HolonMoveSubtree, HolonPropagate, HolonInteract | Local mutation; some graph/multi-row or additive side effects | Immutable target/version/input; mutation and action receipt must share transaction or use an explicit versioned staged command. Do not treat apparent current state as proof of this command. Delete is terminal only with this command's receipt or pre-admission not-found result. |
| WalletCreate | Local custody provisioning plus persistence, currently random wallet | Deterministic request/provisioning identity; retain encrypted key/derived address and one durable wallet result. Reconcile incomplete provisioning without making a second wallet. |
| WalletUpdate, WalletDelete, WalletSetDefault | Local custody/account mutation | Versioned authorized mutation + durable request receipt; current ownership/consent checks and atomic default selection. Persist result before successor. |
| LoanOpen | Two Holon creates (tracker then disbursement); partial first write is possible | One stable loan/entry identity and atomic ledger command/receipt; resume partial command safely. A tracker alone is not completion. |
| LoanRepaymentRecord | Ledger create with source run/node marker and process-local write gate | Durable unique source identity plus ledger CAS/transaction; receipt and projection/loan-status repair. Process-local locks are not cross-instance admission. |
| GovernanceProposalCreate | Local proposal creation | Stable proposal ID, immutable electorate/authority/cutoff/quorum snapshot; receipt-backed creation. See separate governance contract review. |
| GovernanceVote | Person-uniqueness claim ledger plus cosmetic Holon receipt | Existing person/operation uniqueness identity; authoritative ledger claim at frozen proposal/cutoff. Repair cosmetic receipt without second ballot; exact eligible claim must exist before success. |
| GovernanceTally | Claim-ledger projection currently recomputable | Atomic immutable finalization identity/snapshot after authorized cutoff; replay returns same snapshot despite later membership/claim edits. |
| NftMint, Grant | Domain `nft`, existing actor/tenant/run-node Mint admission | Precompute request/lifecycle identity; resume restricted observation/project only, return Pending for participant/finality waits. Grant's post-mint Holon association also needs a durable repair receipt before successor. |
| NftTransfer, Transfer | Domain `nft`, existing Transfer admission | Exact source asset/owner/recipient/fee request; same lifecycle only, terminal confirmed transfer before successor. |
| NftBurn | Domain `nft`, existing Burn admission | Same lifecycle and creator-return/acceptance flow; no worker-created participant signature or fabricated burn. |
| CredentialRevoke | Domain `nft`, verified issuer Revoke admission | Same issuer/asset/reason/network/fee lifecycle; preserve original colon-form key if already admitted. Gate lifted only after independent authority/pending review. |
| Refund (NFT) | Domain `nft`, original confirmed Transfer-bound Refund admission | Same original source operation and current holder acceptance; confirmed reverse transfer only. |
| Hold | Domain `hold`, existing chain+stable HoldReference | Authoritative hold state and funding/recipient evidence; no second reservation/debit; terminal open/funded result as specified by hold service. |
| Release, Refund (hold) | Domain `hold`, original hold settlement/continuation | Existing immutable settlement/next-hold identity; authoritative terminal release/refund or durable continuation, never a second payout from reentry. |
| FungibleTokenCreate | Domain `allocation`/`blockchain`, existing run API-key plus run/node idempotency | Resolve exact allocation/operation, confirmed issuance plus post-create Holon link repair; no key substitution or retry after unknown effect. |
| Bridge, Back | Domain `bridge`, existing run/node bridge/reverse key | Durable bridge identity and authoritative source/proof/target status. Round 3 real Wormhole/provider target implementation dependency remains explicit; no gate lift or fake settled state in Round 2. |
| Swap | Currently quote-to-unsigned transaction construction, not settlement | Stable cached quote/network/wallet/input identity; terminal built artifact only if API explicitly promises a build. If workflow promises settled swap, domain `swap` waits on actual signed/submitted receipt. Tinyman/Ethereum execution is explicit Round 3 dependency. |
| ActionGraphBundleGenerate | Local artifact generation, changing manager contract in Lane C | Stable generation request/version/artifact hash; deterministic stored artifact and authorized bound graph. No random regeneration on replay. |
| ActionGraphBundleDeploy | Domain `deployment`, Lane C durable admission | Frozen artifact/target/network/budgets and deterministic request lookup; only runtime activation or chain-confirmed deployment receipt succeeds. |
| Emit | Pure output plus currently optional best-effort outbox enqueue | Stable tenant/run/node event ID; durable unique outbox enqueue before reporting promised emission. Success means durable enqueue, not third-party processing. Delivery retries must carry same event ID; arbitrary receiver exactly-once cannot be inferred from HTTP response loss. |

The matrix contains every handler file; `ChainOperationOutputs.cs` is a helper, not a registered handler. New enum/registration entries must fail contract validation until classified. The common node claim itself is not proof a local write or external send occurred.

## Crash and ambiguity rules

1. Persist frozen input, reference and phase `Admitted` before invocation. A suspended creator requires an ownership/lease token with a checked pre-egress/admission fence; elapsed time alone cannot authorize another irreversible effect.
2. Durable domains are resumed by authoritative request lookup, never a fresh key. A known domain result attaches once under CAS. Participant approval remains an authenticated interactive domain operation.
3. Local mutations need atomic receipt/effect or an explicit durable staged command; an engine-level lease does not make arbitrary manager calls idempotent. The work above will extend deficient local seams rather than enabling blanket replay.
4. Ambiguous legacy/external delivery obtains durable NeedsOperator status, reason and preserved evidence. Authenticated recent-operator endpoints list and inspect these records, request authoritative domain refresh, and attach a domain-verified result. A human success/absence assertion is not enough to settle or release a possibly-sent economic effect. For a webhook, operator retry means same event identity and documented at-least-once delivery, not a claim that the receiver processed once.
5. The handler's completed receipt and successor admission remain distinct durable stages; recovery re-drives only missing successor enqueue under its existing unique key. Running/Pending is never StepResult.Ok.

## NFT worker restriction

Expose a separate reconciler operation accepting only persisted lifecycle ID. Eligible states are Submitted/NeedsOperator (observe/rebroadcast identical previously admitted wire while valid) and Confirmed (atomic projection). A successful intermediary observation may record the next required participant stage but must return before preparing it. Finalized no-effect proof archives the generation and parks interactive preparation; the worker never signs/replaces. Paginate fairly with stable cursor/updated-time rotation and isolate failures per record/network. Awaiting acceptance/Preparing/Prepared are not broadcast authority.

## Review checkpoints

Freeze the shared envelope with root/Lane C before adapter edits. Independently review local mutation admission/receipt strategy and operator recovery before enabling broad replay. Independently review the concrete governance tenant/role/cutoff/ledger snapshot mapping before gate removal. Author deterministic overlap/crash fixtures; no test/build/schema execution during this authoring wave.
