---
type: review-evidence
---

# Round 1: optimism and completion-claim assessment

Snapshot: 2026-10-15, shared working tree. Read-only assessment of the full user goal:
close all existing platform stubs, Identomat KYC/KYB first, then Stripe, with only
unavoidable external work in the conductor deferral ledger. No build/test execution
was performed. On-ramp recovery and KYC pruning were still in flight at this snapshot.
The assessor authored earlier Stripe and AML work; this document does **not** approve
that work. Those patches still require another reviewer and the final integrated sweep.

## Claims that are not justified yet

1. **“KYC/KYB is set up” is not established by adapters and mocked outcomes.** The
   owner expressly authorized mocked workflow verification, and the deferred ledger
   honestly records absent Identomat workflow configuration IDs. The real dashboard
   workflows, company callback registration, live result shape and deployed secret
   availability remain unproven. Do not fabricate IDs or relabel fixture coverage as
   provider acceptance. Local adapter correctness and recovery can still be finished.
2. **“Stripe works” is stronger than the evidence.** The read-only development account
   request proves authentication/capabilities, and the CLI signing secret enables local
   webhook forwarding. Neither proves a successful checkout, issuance, bank payout,
   delayed callback, or compensation. The new bank lifecycle and cumulative reversal
   changes are authored, with only partial static review so far. No test sweep ran.
3. **Historical checked boxes do not establish chain-backed credentials.** The older
   credential track checks explicit recipient and issuer binding, but its mint seam
   writes a local ownership row. It does not issue the asset it claims to issue.
4. **Fail-closed is a safety property, not implementation completion.** Veriff, generic
   hosted KYC, bridge observation, Solana metadata/account closure and direct swap
   surfaces still refuse their advertised operations. Their size is not an external
   blocker. These must remain delivery packages rather than vague future work.
5. **One empty marker scan is not an inventory.** The most material NFT defect below
   has no TODO/NotImplemented marker. Audit success responses and state transitions
   against the external effect they purport to represent.

## Confirmed hidden semantic stubs and practical closure paths

### P0: UserNFT and credential issuance report chain effects after local CRUD

Evidence:

- `Services/Users/UserNFTService.cs:95-121`: mint invents a GUID token id when absent,
  stamps MintedDate/IsActive, and calls only `INftStore.UpsertUserNFTAsync`.
- `Services/Users/UserNFTService.cs:149-157`: transfer changes CurrentOwner and
  LastTransferDate, then returns “User NFT transferred”; no provider call exists.
- `Services/Users/UserNFTService.cs:169`: burn deletes the ownership row only.
- `Services/Users/UserNFTService.cs:351-357`: “verify ownership” compares local UserId.
  Transfer does not change that UserId, so even the local authorization projection
  continues to recognize the former holder after a reported transfer.
- `Controllers/UserNFTController.cs:21-89` exposes these live authenticated routes.
  `Controllers/CredentialController.cs` issues through the same service seam.
- `conductor/tracks/credential-third-party-issuance_20260906/plan.md:61-76` checks
  recipient/issuer improvements, not proof of an actual mint. Its assertions cannot
  support an end-to-end credential-complete claim.

Practical route: reuse `Managers/NftManager.cs:87-139` and the existing
`IBlockchainOperationManager` pipeline, rather than introducing another signer.
That manager already applies governance/value readiness, creates a durable Mint
operation and invokes operation execution. `Interfaces/IBlockchainProvider.cs`
has real MintAsync/BurnAsync/TransferAsync surfaces. Add durable operation-to-
ownership correlation and update local ownership only from confirmed chain evidence;
unknown/pending operations stay pending, never “minted.” Credential issuance needs
the issuer authority/recipient opt-in/soulbound semantics retained through this path.
Transfer must reconcile both address and subject ownership/binding permissions.
Burn must preserve the audit trail and reconcile only after chain confirmation.
Provider tests must prove transaction submission, not merely row contents.

### P1: Tinyman API transaction builder returns instructional text as success

`Services/Dex/TinymanDexAdapter.cs:163-180` returns success with no SwapTransaction,
sets TokenIn to QuoteId and TokenOut to WalletAddress, and tells the caller to build
its own transaction group. `Managers/SwapManager.cs:113` returns that result directly.
This is a semantic stub even though a quote implementation and SDK adapter exist.

Practical route: `sdk/azoa-wallet/src/dex/tinyman.ts:324-441` already invokes the
official SDK's `Swap.v2.generateTxns`, encodes the group and rejects an empty group.
Use that implementation as a contract/parity reference, not as proof the .NET route
works. Build the actual Algorand payment/asset-transfer plus fixed-input app-call
group server-side from authenticated cached quote facts, fresh pool state and
suggested parameters; preserve slippage bounds, opt-in needs and signer ownership.
The root captured official swap instructions in
`.omc/research/tinyman-swap-contract-discovery-2026-10-15.md`. Validate every member
of the group and expose actual bytes with a defined response contract. Direct
provider Swap/Exchange wrappers should follow that implementation, not merely stop
returning errors. SDK `any` shims also need contract verification against installed
Tinyman dependency versions; existing calls are useful evidence, not authority.

### P1: Failed bank payouts still lack a completed money-return path

`Services/Fiat/OffRamp/BankPayoutProcessor.cs` deliberately parks failed/canceled
bank payouts for reconciliation. That prevents unsafe unit restitution but does not
finish the cash-out recovery journey. Its newly added Reversed preservation guard
avoids a delayed failure downgrading reversal state; that is narrower than recovery.

Practical route: audited operator recovery must refetch the connected-account payout,
validate account/intent/amount/currency/environment and establish terminal failure.
Then reverse/reconcile the original Connect transfer with a durable idempotency key,
prove the platform recovered the intended funds, and only then perform idempotent
unit restitution. Ambiguous reversal or bank payout states must preserve claims and
remain replayable. Never return units merely because a bank payout says failed.
The parent placed this next implementation package on hold until reviewed planning.

### P1: Remaining advertised provider capabilities require real implementation

- `Services/Bridge/WormholeProtocol.cs:53-63` explicitly refuses all supported-route
  source-message observation. Read authentic emitter/sequence from each supported
  chain's transaction evidence; pair this with the target provider settlement
  lifecycle. A guardian VAA verifier alone does not create a usable bridge route.
- `Providers/Blockchain/Solana/SolanaProvider.cs:660-717` retains metadata create/
  update and token-account-close refusals; `Providers/Blockchain/Solana/AGENTS.md:136`
  also records refusal to create a missing ATA. Add real instruction builders with
  signer/fee-payer context, rent/authority checks and submitted/confirmed semantics.
- `Providers/Kyc/VeriffKycProviderService.cs` and
  `Providers/Kyc/GenericHostedKycProviderService.cs:30-59` remain unavailable
  scaffolds. Veriff has an identifiable official contract (root captured it).
  Generic hosted requires a precisely defined AZOA integration contract or a named
  provider contract; mapping arbitrary JSON “approved” fields is not a valid closure.
  Both real adapters need the same verified-subject AML lineage and atomic outcome
  fences as Identomat. Missing vendor credentials restrict live acceptance testing,
  not implementation against the documented protocol.
- `Services/Actions/AGENTS.md:451-492` explains why governance and credential nodes
  are disabled. Some reasons are now stale after historical patches, but disabling
  cannot simply be removed from a checklist. Reassess vote-of-record tally authority,
  issuer/asset authority and the newly confirmed issuance defect before enabling.

## Challenge to the current deferrals

**IDENTOMAT-WORKFLOWS:** justified external remainder because the owner confirmed
the workflows do not yet exist and explicitly chose mocked verification. Close all
local configuration validation, routing and fixture/replay tests first. Exact
external closure remains dashboard workflows plus real callback/result acceptance.

**IDENTOMAT-RECOVERY:** absence of documented provider idempotency or an authoritative
lookup does justify deferring *automatic* absent-versus-created resolution. It does
not justify leaving admissions indefinitely unrecoverable. The new
`Controllers/HostedSessionReconciliationController.cs` is present; independently
review attach/absence authority, subject correlation and lease/CAS behavior before
calling the operator path implemented. A human assertion of absence is an audited
risk decision, not provider proof. Test a callback arriving during release/attach.

**No other code package should be moved to deferred.md merely for size.** Chain
funding/deployed contracts/vendor credentials can block live acceptance, but local
instruction generation, durable state, signing boundaries, reconciliation and
contract tests remain authorized work. Do not claim those external conditions are
missing without inspecting the available configuration without printing secrets.

Manual KYC is explicitly simulation-only and both value gates already exclude
`KycProvider.MANUAL`; requiring AML lineage did not newly remove a supported manual
real-value approval path. Real approvals missing lineage do need a reviewed,
verified-subject recovery/reverification journey; do not bypass lineage to make old
fixtures or manually inserted approval rows pass.

## Bounded delivery packages for plan review

1. **Identomat completion:** independently review new KYC/KYB/AML/shared-session
   changes; fix reviewed defects; close missing-lineage recovery for real attempts;
   keep workflow-only external remainder explicit. Avoid simultaneous edits to
   shared stores/Program/schema generation.
2. **Stripe operational closure:** finish current on-ramp recovery/inbox replay,
   then off-ramp bank-failure recovery; common durable ambiguity/idempotency rules;
   DEV-only integration plan, no live transactions.
3. **NFT/credential chain semantics:** durable mint/transfer/burn plus reconciliation,
   then authoritative ownership/permissions and credential node enablement review.
   This can proceed in parallel with 1 and 2 on its own managers/service files.
4. **DEX closure:** real Tinyman group builder and SDK parity first; route provider
   swaps and Ethereum adapter through reviewed chain-specific execution contracts.
5. **Chain capabilities:** Solana metadata/ATA/close and chain-specific contract
   call/deploy inputs; use the required chain-provider skill when implementing.
6. **Bridge and governance:** source-message observation/target settlement and
   vote-of-record audit in distinct lanes; enable only after independent review.
7. **Additional KYC and final parity:** documented Veriff/generic hosted adapters,
   frontend/SDK/API status parity, stale docs, generated schemas, and all regression
   fixtures. Review all new adapters' AML lineage and subject verification.

These are proposed packages, not approved implementation dispatches. Their order
preserves Identomat then Stripe priority while permitting independent NFT/provider
work. Four assessment rounds should revisit the resulting tree, not rerun the same
marker scan and declare progress from checked boxes.

## Verification truth at this snapshot

- Authored patches and added tests are **unverified** until the integrated sweep.
- `git diff --check` is whitespace evidence only, not compilation or correctness.
- Existing historic test counts do not cover new POCO fields/typed query translation.
- Regenerate schemas/flowcharts only after all model changes settle; real SurrealDB
  execution must cover new optional lineage/index fields and transactions.
- Round-end independent reviewers must examine another author's code. This assessor
  cannot approve its own Stripe or AML batch through an “optimism assessment.”
- Revisit in-flight on-ramp and KYC recovery claims after their authors hand off.
