---
type: assessment
title: Round 2 optimism and completion-claim assessment
status: Read-only assessment; implementation planning required
---

Snapshot: 2026-10-15, after the Round 1 source-review loop. No build, tests, schema
generation, lint or runtime integration ran in this assessment. The assessor authored
parts of KYC/AML, Stripe, NFT clients and Solana; this report is not approval of those
changes. Separate reviews and the integrated acceptance sweep remain necessary.

## Completion claims that the evidence does not support

- **All stubs closed:** false at this snapshot. The server Tinyman builder, wallet
  authentication on Ethereum/Solana, additional hosted KYC adapters, standalone
  chain APIs and Wormhole observation still have concrete missing implementations.
- **NFTs now converge automatically:** false. Durable admission, actual prepared
  transactions and browser refresh are authored; the core handoff explicitly records
  no submitted-operation observation worker and no durable action pending integration.
- **KYC/KYB set up:** local adapters and recovery are authored, but actual Identomat
  workflow IDs/dashboard registration are absent by the owner's confirmed choice.
  Mocked workflow acceptance must remain clearly separate from provider acceptance.
- **Stripe operational:** read-only DEV credentials/capabilities and a local CLI
  webhook secret prove access. They do not prove checkout, issuance, connected-account
  payout, delayed events, recovery or restitution. Those scenarios still need runtime
  evidence; new local recovery code is not a reason to defer that work.
- **Clean/pushed main:** currently false. Read-only git inspection reports `main`,
  301 changed/untracked status lines and local HEAD 60 commits ahead of the locally
  known `upstream/main` (zero behind). No fresh remote fetch was performed here, so
  that count is local-ref evidence only. Consolidation is not equivalent to a clean,
  verified, pushed result. The remote is `upstream`, not `origin`.

## Prioritized implementation packages and useful existing work

### P0: Dapp deployment still fabricates successful external deployment

Root supplied this finding; this assessor independently read the implementation.
`Managers/ActionGraphBundleManager.cs:135-157` only saves DeploymentConfig, inventing
`TxHash = 0x + Guid`. GeneratedCode is serialized configuration, not compiled chain
code. `DappCompositionManager.cs:340-361` ignores targetOverride, delegates to that
method and sets the series to Deployed with a date. The action deploy handler reports
success from the same seam. Removing the random hash while retaining Deployed would
preserve the semantic defect.

Reuse composition validation, DappManifest, action graph publication/execution,
bundle ownership checks and chain provider deployment capabilities. Define a concrete
target deployment artifact and activation contract: platform-hosted graph activation
needs a durable versioned runtime registration and resolvable deployment identity;
chain targets need actual compiled artifacts, signer authorization, prepared bytes
and confirmed contract/program identities. Honor and validate targetOverride before
admission. Persist Pending/Failed/Confirmed deployment evidence and propagate it
through SDK, UI and action results. Verify response loss/replay, changed manifests,
target mismatch and actual reachable/executable deployment, not merely stored JSON.
Do not relabel a generated manifest as deployment or scope this product feature away.

### P0: Pending economic effects must converge without false saga success

`action-lifecycle-review.md` records the concrete Running replay path returning
`StepResult.Ok`, which SagaProcessor treats as completion. A crash after claim can
therefore orphan the node. NFT handlers now call the real lifecycle but cannot treat
recipient acceptance/finality as an ordinary failure or successful final effect.
`round1-nft-core-handoff.md` and the absence of a worker consumer of the lifecycle
store's pending list confirm browser-closed convergence is still missing.

Reuse the existing immutable lifecycle ID, exact prepared wire, generation history,
participant discovery and saga parking primitives. Add a durable pending node result
that records the original lifecycle/request identity and advances successors only on
the exact terminal effect. Add a separate observe/rebroadcast/finalize worker that
cannot prepare new signatures or impersonate recipient/issuer acceptance. Calling the
current interactive refresh method blindly is unsafe because it can reach another
preparation stage. Verify claim/admission/response-loss crash windows, overlap,
late acceptance beyond retry budgets and no successor before final confirmation.

### P1: Wallet login parity is still missing despite transaction signing parity

`Services/Signing/Ed25519SignatureVerifier.cs:65-72` dispatches only Algorand and
throws for other chains. `wallet-auth-review-input.md` also identifies exact-address
lookup and challenge-store defects. New Ethereum/Solana transaction signers do not
close this authentication seam.

Reuse existing vetted curve libraries and the captured official EIP-191 contract;
implement Ethereum personal-message recovery and Solana raw-byte Ed25519 verification
over the server challenge with an explicit browser/SDK encoding contract. Canonicalize
Ethereum identity throughout challenge/link/login; preserve Solana case. Reuse the
public WalletAuthChallenge POCO and typed atomic single-use consume, and enforce
concurrent wallet-binding uniqueness. Verify actual signatures, wrong message/chain,
malformed encodings, equivalent Ethereum spellings, Solana case distinctions,
expired/reused/concurrent nonces and concurrent linking. No external blocker exists.

### P1: Tinyman remains a successful response with no executable transaction

`Services/Dex/TinymanDexAdapter.cs:163-183` ignores the cached quote payload, puts
QuoteId/WalletAddress into TokenIn/TokenOut and returns instructional prose without
SwapTransaction. This is still a semantic stub. `JupiterDexAdapter.cs:126-175`
already returns provider transaction bytes; that is useful response-contract reuse,
not proof of Tinyman. The SDK Tinyman builder and captured official fixed-input
contract provide implementation references.

Build actual authenticated quote-bound Algorand groups with current pool state,
fees, slippage, asset IDs, signer/account references and expiry. Validate every group
member and close server/SDK response parity. Then connect advertised provider swap
entry points and audit Ethereum DEX availability against an actual configured adapter
(Services/Dex currently contains only Jupiter and Tinyman). Pool absence remains
`Unavailable: true`; missing builders must not be disguised as market conditions.
Verify decoded transaction semantics and real local/test-network protocol execution.

### P1: Standalone chain APIs remain refusals after NFT module completion

`SolanaProvider.cs:570-586` refuses program deployment/calls; lines 629-686 refuse
metadata create/update, missing ATA creation and close. `AlgorandProvider.cs:741-755`
refuses AVM deployment/calls. New Solana NFT instructions create metadata and ATAs
inside their lifecycle; this does not implement these standalone public contracts.

Reuse Solana metadata/PDA codec, ATA/token instructions, custody and exact-wire
recovery; add explicit fee payer/signing authority/rent destination to deficient
contracts. For mutable metadata, validate update authority and immutability rather
than attempting to mutate the new immutable NFTs. Use typed AVM deploy/call and
Solana instruction/program-deploy inputs instead of guessing an ABI from generic
dictionaries. Preserve SDK/.NET capability parity and durable submitted/confirmed
states. Missing interface parameters are an implementation change, not an external
deferral. Ethereum's `ConfiguredChainId()` still reads one chain-level metadata pin;
add/review per-network intended identity so a correct wire on the wrong configured
chain cannot pass initial admission. The durable genesis check alone cannot choose
the operator's intended network.

### P1: Wormhole verifier success does not establish a bridge route

`Services/Bridge/WormholeProtocol.cs:53-64` refuses observation on every supported
route. Solana declares no source/target legs; Algorand has source-only support.
Therefore Guardian signature fixtures cannot establish end-to-end bridge usability.

Reuse the existing VAA verifier, replay digest, bridge records and chain-specific
receipt readers. Implement real source publication plus finalized emitter/sequence
extraction bound to the admitted transfer, then exact payload-bound redemption and
authoritative target settlement/replay prevention. A normal vault transfer does not
publish a Wormhole message. Plan routes explicitly with actual configured programs,
wrapped-asset authority and decimal normalization. Public contracts/funding can limit
live acceptance, but observation codecs, durable orchestration and local protocol
tests remain implementable. SDK `supportsBridging=true` describes builder support;
do not expose it as proof a server settlement route is ready.

### P1: Governance gates have actionable reasons; stale reasons hide progress

`Services/Actions/NotEnabledActionNodeTypes.cs` still blocks governance and revoke.
Its current explanation correctly says the tally is now claim-ledger based and
identifies the remaining electorate ownership and close-authority defects. Meanwhile
`Services/Actions/AGENTS.md:481` still says tally reads raw holons. Do not rebuild a
solved tally or lift gates based on obsolete docs.

Reuse `GovernanceClaimLedgerTally`, vote-of-record guards and atomic claims. Bind
proposal electorate to authorized tenant authority, implement an auditable close
policy and protect in-flight votes at the same durable boundary. Review credential
revocation gate removal against new issuer-backed lifecycle and the pending-action
package, then update all publish/dispatch/catalog gates together. Tests must prove
cross-tenant rejection, vote/close races, no forged-ballot tally and issuer-only
revocation. Gate removal alone is not completion.

### P1: Additional KYC adapters and operator recovery journeys need closure

`VeriffKycProviderService.cs` refuses every operation. GenericHosted always reports
unavailable even with complete configuration (`GenericHostedKycProviderService.cs:27-60`).
Captured official Veriff authentication/webhook contracts support real implementation;
reuse shared admission, attempt/lease fencing, callback correlation and subject-bound
AML evidence. Do not weaken Identomat-derived invariants to make another provider
appear approved. Define and version an explicit AZOA hosted-provider protocol for
generic integration, with authenticated callbacks, authoritative fetch, verified
subject evidence and retry semantics. Arbitrary JSON status mapping is not a protocol.

Operator hosted reconciliation exists at `api/operator/hosted-sessions` and is
Identomat-specific. Targeted SDK/frontend searches found no corresponding recovery
methods or operator board; business verification currently tells the user to contact
an operator. Add a usable authenticated SDK/UI path preserving reviewed evidence,
proven-unsent release versus sent quarantine and authoritative attachment. Audit
Stripe inbox replay parity as well: payout/on-ramp recovery SDK/UI exists, but an
API-only replay endpoint is not an operator journey. New adapter recovery must dispatch
through provider interfaces instead of hard-coded Identomat dependencies.

## Deferral audit and acceptance boundaries

The current deferred ledger contains only IDENTOMAT-WORKFLOWS and
IDENTOMAT-RECOVERY. Both are narrowly defensible: owner-confirmed absent dashboard
workflows, and undocumented automatic lookup/idempotency after ambiguous begin.
The correction is essential: only proven-unsent admission releases; a human absence
assertion cannot make a possibly sent request safe to repeat. Operator attachment,
quarantine, late original-token completion and authorized per-attempt renewal are
local features to verify, not reasons to enlarge that external exception.

No implementation-size deferral is justified for the packages above. Credentials,
funding and deployment constrain external acceptance only after local code and
contract evidence are finished. Explicit manual KYC simulation is not a missing real
approval bypass. Mainnet faucet absence is not a stub. Unsupported semantics must
remain truthful until implemented, but labeling them unsupported cannot shrink the
original all-stubs goal.

Final acceptance must separate four evidence levels: authored code, independent
source review, executed fixture/persistence/protocol tests, and actual vendor DEV
journeys. Run the requested integrated sweep after all fixes, including generated
Surreal schemas, real CAS/unique/optional-field behavior, SDK/frontend gates, Solana
local-validator Metaplex execution, Ethereum local contract execution and Stripe DEV
webhook/payout/recovery drills. Then fix the resulting batch, obtain separate review,
commit/push the reviewed result and verify clean main against the actual remote.
Historic green counts, whitespace checks and mocked chain confirmations cannot
substitute for these acceptance steps.
