---
type: implementation-plan
---

# Round 1 plan: identity, payment recovery, and false chain-success paths

Status: adversarial review corrections incorporated; execution wave ready. Review evidence: [round1-plan-review.md](round1-plan-review.md). Its eight findings are addressed below; reviewer confirmation is recorded separately from implementation approval.

Reviewer confirmed correction sufficiency for execution on 2026-10-15. Dispatch: Lane A `stripe_explore`; Lane B `stub_inventory`; Lane C `identomat_explore`. This is plan acceptance, not approval of authored implementation. Round 1 implementation and independent review/fix passes are in progress.

## Evidence and priority

The completed parallel assessments are preserved in [round1-assessment-adversarial.md](round1-assessment-adversarial.md) and [round1-assessment-optimism.md](round1-assessment-optimism.md). Neither is runtime verification or approval of its author's code. The adversarial assessment confirmed the credential authorization risk with explicit preconditions; no exploit was executed. The independent identity review is [round1-identity-review.md](round1-identity-review.md).

Identomat remains first priority, then Stripe. A newly confirmed UserNFT semantic stub also requires immediate correction: local ownership CRUD is reported as mint/transfer/burn, and credential issuance accepts an unproven asset identifier that subsequently participates in platform clawback authorization. Historical track checkmarks do not establish actual issuance or issuer provenance.

## Parallel implementation wave

### Lane A: identity completion and verified-subject recovery

- Complete independent review of KYC, KYB, AML lineage, hosted admission recovery, and shared callback routing. Resolve findings across author boundaries.
- Verify that failed monitor registration remains retryable, lost leases cannot approve or bind identities, and stale attempt evidence cannot clear a current attempt.
- Close the recovery journey for a real approval missing linked AML evidence through authoritative subject reverification. Do not bypass lineage or enable manual simulation for value access.
- Review the operator attach/absence decision against concurrent callback delivery and late vendor creation. Preserve the audit trail and explicitly distinguish human attestation from provider proof.
- Add a durable local creation-ownership/cancellation fence. Test absence release while the creator is suspended before send, during HTTP, after vendor acceptance, and before attachment. An attestation cannot cancel a live vendor request: retain quarantine and exclude automatic retry while the outcome is ambiguous. Fresh in-flight CLAIMED status must not immediately imply NeedsOperator.
- Fix immutable screening-store builder mutations in `UpdatePollStateAsync` and `RecordMonitorFollowUpAsync`; independent review found missing reassignment in the AML author's handoff. Inspect all touched typed mutations for the same defect.
- Fix callback-before-submission persistence: an authoritative callback arriving between hosted creation and KYC attempt creation must remain retryable, not terminalize the session on `Success(null)`. Distinguish superseded attempts from not-yet-attached attempts and cover operator attachment in the same window.
- Author tests for the real typed Surreal transactions, optional-field handling, immutable builder return values, and stale-owner outcomes. Schema generation and execution remain in the final integrated sweep.

Acceptance: no stale attempt can produce a current approval or clearance; each transient failure has a durable retry/reconciliation path; invalid subject/tenant/provider/config evidence fails closed; public status reflects durable state. Only confirmed dashboard configuration and undocumented automatic vendor reconciliation remain external.

### Lane B: Stripe recovery end to end

- Finish the in-flight on-ramp refund/resume, inbox replay, operator routes and SDK/frontend parity.
- Add durable structured proof for allocations that failed before any external effect, with an atomic transition from that proof to the exact refund reservation. Never infer safety from an error string or replace a generic Failed claim.
- Keep ambiguous or effect-started allocations excluded from refunds until authoritative reconciliation resolves them. A refund reservation must win against concurrent issuance under the identical allocation key.
- Implement failed/canceled bank-payout recovery: authoritative connected-account payout read, idempotent reversal/reconciliation of the original Connect transfer, then unit restitution only after the platform's funds are proven returned.
- Preserve cumulative reversal accounting, account/environment/intent/amount/currency binding, and stable idempotency after response loss. Include operator audit and durable retry windows.
- Bind an atomic restitution reservation to the original account, environment, transfer, payout attempt and unit quantity. Cumulative restored units cannot exceed proven returned funding net of prior restitution. Cover partial reversals, duplicates, reordered payout/reversal events, paid-after-failure observations, and previous unit returns.
- Before development lifecycle execution, establish account permissions, available test balance and an authorized test-payment fixture. If this setup prevents acceptance testing, record exactly which external condition blocks it; contract fixtures never become a claim of provider acceptance.

Acceptance: charged-but-uncredited payments and failed bank payouts have usable recovery; no concurrent branch can both issue and refund, or both pay cash and return units. DEV credentials only. Provider fixtures, real Surreal tests, and a bounded Stripe development lifecycle are required; no live-key transactions.

### Lane C: real NFT lifecycle and credential authority

- Immediate prerequisite: block unproven issuer/ownership records from every platform-clawback entry point, including direct CredentialController revoke, action handlers and internal manager callers. The existing action gate alone does not protect the controller route. This guard is mitigation until real provenance is implemented and independently reviewed.
- Trace and reuse the existing operation manager, signing/custody boundary and concrete provider capabilities. Confirm their behavior instead of treating the manager names as proof.
- Replace UserNFT mint/transfer/burn local-success paths with durable chain operations and operation-to-ownership reconciliation. Pending or ambiguous operations must not project final ownership or success.
- Bind credential issuer authority to the actual confirmed issuance operation and asset, recipient wallet and required chain semantics. Caller-supplied TokenId is never proof that the caller issued an existing asset.
- Fix caller ownership/permission checks on composite/binding endpoints discovered by the assessment, including permission value rather than key-presence checks.
- Preserve owner audit after transfers/burns, and prevent the former holder from retaining access through stale UserId/bindings.
- Enforce uniqueness for current ownership by chain, network, contract and token; resolve duplicate/unproven rows explicitly without allowing a first-row lookup to select attacker-authored issuer authority. Fill composite projections from actual authorized records instead of blank placeholder values.
- Issuance proof includes chain, network, contract/mint address, asset/token ID and authoritative confirmed transaction. Verify the actual creator/signer, recipient, quantity and soulbound/clawback parameters. A request-supplied transaction hash, self-minted registration or fabricated duplicate row does not establish issuer authority. Preserve proof through restart and operation/ownership persistence interruption.
- Application access follows the authenticated caller's confirmed current ownership and explicit allowed permission value. No unrelated composite disclosure, borrowed NFT identifiers or present-but-denied permissions. Unresolved chain ownership cannot grant access; centralize the rule so routes and internal callers agree.
- Keep CredentialRevoke's action gate until a separate security review proves the issuance-to-revocation chain. A temporary fail-closed guard is mitigation, not completion of this package.

Acceptance: every reported chain effect is backed by confirmed transaction evidence; lost response/retry yields one operation; arbitrary asset registration cannot confer platform clawback authority; ownership/access follows chain truth. Implement supported concrete chains without simulated identifiers or silent fallback.

### NFT fee contract clarification during implementation

Review found that reusing fungible asset fee schedules would attempt to split a single indivisible NFT or silently reinterpret its fee currency. The NFT lifecycle will use an explicit, separately configured chain-native flat fee and treasury, bound to chain/network and snapshotted before signing. Existing fungible fee semantics stay intact. Current NFT operations are mint, gift transfer, burn, and credential revocation; they expose no sale consideration and do not advertise a percentage NFT sale fee. Nonzero NFT fees must settle atomically with the relevant NFT effect, once per logical operation rather than on every acceptance/delivery stage. This remains implementation work with independent review and nonzero-fee regression coverage, not a deferral or permission to disable configured fees.

Separate adversarial amendment review by `stripe_explore` required and received these corrections before nonzero-fee implementation: quote native atomic units, currency decimals, network, policy revision, treasury and explicit payer; use the original actor for the logical platform fee while distinguishing each signer's network costs. Show the quote before charged submission through quote/confirm or an explicit maximum-fee and expected-revision constraint. Persist the accepted quote so later policy changes cannot reprice a retry. Free is explicit zero, never an unavailable quote interpreted as zero. Cancellation or pre-send failure charges no platform fee. An uncertain submitted transaction must reconcile before any refund; retries cannot add another platform fee.

## Ownership, review, and verification

Dispatch disjoint file ownership after plan review. Central Program/config/schema wiring has one owner at a time. Authors must notify peers before modifying shared interfaces, models or SDK exports.

Review each lane in a different agent context, apply findings as a batch, and record unresolved findings as work rather than convenient deferrals. Round 1 closes only after these implementation and static review passes; it does not claim tests passed. The full test/build/typecheck/lint sweep stays at the end of the four-round workflow per the user's execution policy.

Round 4 reserves a complete remediation batch after the integrated sweep and an evidence-producing rerun of failed/affected gates. Known failures are never left merely to preserve the phrase "one sweep." Build the final model assembly and regenerate schemas/flowcharts before runtime/golden checks. Independently review generated differences and final remediation.

## Remaining rounds retain the full goal

The later rounds must reassess the new tree and cover wallet authentication parity, Tinyman/DEX execution and Ethereum adapter, Solana metadata/account lifecycle, Algorand/Solana contract operations, Wormhole observation/settlement, governance authority/finality, additional KYC adapters, and any newly discovered semantic stubs. Their scope will be reconciled from fresh parallel assessments, not automatically deferred. The fourth round includes final generated artifacts, integrated verification, development-provider acceptance evidence, main push and merged remote-branch cleanup.

Round 2 entry explicitly includes wallet-auth chain-owned EIP-191 and Solana Ed25519 verification, canonical Ethereum address identity across challenge/link/login/rate limiting and uniqueness, case-preserving Solana identities, and nonce replay/concurrent-consumption regressions. Round 3 entry explicitly includes remaining chain operations and bridge authoritative evidence. Each round inventories reachable success paths, not merely stub keywords.
