---
type: review-evidence
---

# Round 1 adversarial assessment

Snapshot: 2026-10-15, main bb512cf, one worktree; 112 tracked/untracked status entries at inspection. Source-only review, no build/test execution. Shared workspace remains changing: on-ramp and review fixes are in flight. Authored KYC safety changes are not independently approved by this assessment; another lane must evaluate them.

## Verdict

The full goal is not achieved. There are material semantic stubs that produce success without the promised external effect. Historical completed-track prose is not evidence. Missing Identomat workflow IDs legitimately prevent live validation but do not excuse application authorization, persistence, recovery, or mock-contract omissions.

## Ranked findings and work packages

### P0 — NFT issuance provenance can authorize platform-signed clawback

Evidence: Services/Users/UserNFTService.cs:57-120 only writes a row. Caller TokenId is accepted verbatim, otherwise a GUID is fabricated. CredentialController.cs:76-113 checks manager role and nft:mint, then calls this same row writer, stamping caller as issuer and recipient wallet as current owner. SurrealNftStore.cs:434-462 resolves a current credential by chain/token with LIMIT 1, without issuance receipt provenance. CredentialManager.cs:133-190 trusts recorded issuer and invokes platform-signed clawback to that issuer's wallet. AlgorandProvider.cs:1611-1685 proves platform clawback authority, holder balance, and recipient opt-in, but does not prove the calling issuer originated this asset.

Attack preconditions: caller has manager/mint authorization and value access; victim holds an existing ASA whose clawback is this platform; attacker destination is opted in; injected row is selected as current. This is an authority-confusion risk, not merely misleading UI. No exploit was executed. Existing duplicate ownership rows and query selection require explicit regression coverage.

Package: real chain-backed mint/issue with durable operation/receipt linkage, immutable issuer provenance and unique chain/network/contract/token current ownership; chain transfer and burn with truthful pending/confirmed outcomes. Arbitrary imported token references must never mint issuer authority. Existing unproven rows cannot authorize platform signing.

Acceptance: mocked real provider receipts and independent adversarial tests show supplied existing token cannot create issuer authority; zero broadcast yields no minted status; pending confirmation remains pending; confirmed receipt persists exact token and issuer; replay cannot mint twice; mint/receipt persistence interruption reconciles; unsupported chains return explicit capability failure. Existing provider modules should be reused rather than inventing a parallel chain abstraction.

### P1 — UserNFT authorization and ownership answers are not authoritative

Evidence: UserNFTService.cs:144-180 transfers CurrentOwner in DB only and leaves UserId unchanged, so the original user retains transfer/delete authority; BurnUserNFTAsync deletes the DB row only. VerifyUserNFTOwnershipAsync at :351 trusts DB UserId. Composite endpoints UserNFTController.cs:176-190 do not enforce owner unlike ordinary read endpoints. VerifyHolonAccessAsync/:361 and VerifyWalletAccessAsync/:377 accept a supplied NFT ID with no caller identity and grant permission based on dictionary key presence, regardless of value. Composite projections deliberately emit blank holon names/wallet addresses/chain at :429 onward.

Package: caller-aware composite/access checks, chain-verified current ownership policy, truthful projections, and cross-account regression tests. Coordinate with NFT issuance owner to avoid competing ownership semantics.

Acceptance: unrelated caller cannot enumerate composite wallet/holon bindings or borrow another NFT's permission; explicitly denied permission does not pass by key presence; transferred ownership updates authorization atomically after chain confirmation; stale DB row cannot establish current chain ownership.

### P1 — Wallet authentication is incomplete for advertised chains

Evidence: Services/Signing/Ed25519SignatureVerifier.cs:47-54 only supports Algorand; Ethereum/Solana throw. WalletKeyService.cs:38-43 generates all three chains. WalletAuthManager.cs:106 and corresponding verify path trim raw address but do not establish chain-specific canonical identity. This is an implementation gap, not an unavoidable external deferral.

Package: chain-owned signature verification through generic dispatcher; EIP-191 personal_sign exact UTF-8 bytes and Solana raw Ed25519; canonical Ethereum address keys across challenge/create/verify/link/login and uniqueness constraints, preserving Solana case.

Acceptance: known official-compatible vectors, altered-domain/wrong-address/wrong-chain rejection, nonce replay and concurrent consumption, Ethereum case variants share one auth identity and rate bucket, Solana case variants do not collapse. Read chain-provider skill before implementation.

### P1 — New Identomat/Stripe code needs executable persistence evidence

Independent KYB/Stripe review found and reported: discarded RequestNotSentCode stranded definitely-unsent admissions; missing status recovery projection; unassigned immutable Set/Unset builder omitted KYB business evidence; broad duplicate catches masked unrelated statement errors. Authors have changed these, but no runtime verification exists yet. Stripe Reversed guards now preserve reversal under delayed paid/pending/failed/canceled triggers and version CAS protects overlap in source review.

Authored KYC/shared work remains unverified: real Surreal typed expressions, nullable Unset, response indexing across transactions, token fencing, concurrent renewal and record lock conflicts, identity/AML lineage, monitor-registration retry and operator admission recovery. The independent review caught Set(null), which the actual library explicitly rejects; static-looking fluent code is not evidence of working persistence.

Package: independent review then final integrated sweep with real SurrealDB and HTTP fixtures for real adapters, no production approval mock. Include source-generated schema regeneration and exact generated diff.

Acceptance: lease expiry + paused old handler cannot bind/approve; retired attempt cannot overwrite current identity/AML; current HIT followed by old clean result remains closed; monitor failure remains retryable; operator attach/absence decision is authorized/audited; KYB evidence survives real persistence and approved projection; null fields do not throw; callback-before-attachment/lost response and wrong account/environment cases are covered. Bank transfer funding cannot report bank Paid until authoritative payout paid; Reversed never downgraded; on-ramp in-flight batch reviewed separately.

### P2 — Recovery claims and external deferrals must stay precise

Identomat workflow IDs are confirmed absent, so live vendor verification is legitimately deferred. Official captured API documents no idempotent begin/unique correlation lookup; automated lost-create recovery can remain blocked on that contract. However operator endpoints, polling status, reattachment, durable recovery state, and tests are application work and cannot be deferred. CLAIMED alone currently represents both in-flight creation and ambiguous create; a polling NeedsOperator projection must avoid asserting proven ambiguity during the brief live request.

Review proposal: track live workflow setup and automated vendor lookup as separate external entries. Record exact owner/unblock/live drill. Do not imply mocks establish account-specific dashboard behavior. Stripe live setup/account capability should likewise be supported by observed configuration evidence, not presumed absent/present.

### P2 — General inventory and Git closure remain unfinished

The shared tree has substantial uncommitted new code; only one worktree is present and main contains the consolidation commit. This supports consolidation, not completion or preservation of all work in a commit. A final tracked/untracked inventory must include generated schemas, new controllers/services/tests, SDK and UI. Broad scan still finds explicit limitations, including tenant custodial bootstrap Algorand simulation only in WalletKeyService.cs:49-55; evaluate capability reachability before calling each a stub. Simulated bridge vault fallback is correctly restricted to simulated routes at CrossChainBridgeService.cs:1478-1495; do not remove valid simulation behavior merely to reduce keyword counts.

Acceptance: every reachable placeholder classified by concrete behavior and supported capability; no fake external effect; unresolved implementation gaps assigned to work packages, only externally blocked items in deferred.md. Final integrated build/unit/integration/frontend/SDK gates recorded with commands, versions and results; residual failures individually triaged, no old baseline waiver assumed. Separate reviewer signs off final diff; final commit/status evidence recorded per user's authorization.

## Proposed parallel order

1. Plan/review the P0 NFT provenance and P1 access package together; implementation owner handles shared store and service authority.
2. Independent wallet-auth owner implements chain modules and canonical identities after plan review.
3. Dedicated verification/recovery owner reviews current KYC/KYB/Stripe changes, schema and contract fixtures, including on-ramp once stable.
4. Integrate all authored changes, complete independent review, then run the single final sweep. Apply any discovered fixes as a batch rather than alternating single-test loops.

## Limits

This is not exhaustive proof of all platform code. No live provider call, exploit, test, build, schema generation, or deployment was performed. Exact chain authorization exploit impact is source-derived with stated prerequisites. Shared branches changed during review; author-reported fixes need final reread and test evidence. The source scan was budgeted and contains legitimate simulations/documentation hits; its keyword count cannot establish completion.
