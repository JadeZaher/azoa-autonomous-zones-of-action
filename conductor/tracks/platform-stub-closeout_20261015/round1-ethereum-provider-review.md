---
type: Review
title: Round 1 Ethereum provider source review
status: Bounded source findings resolved; runtime verification pending
---

The root reviewer inspected the shared preparation allocator, activation checkpoint, authorized egress, cancellation recovery, NFT adapter and prepared value adapter independently of their author. The durable store owns unique protocol/sender/nonce reservations, immutable call fingerprints and versioned recovery state. Submission requires a distinct persisted authorization state; the background worker does not submit merely prepared NFT/value wire.

Finalized observation checks the exact hash, sender, nonce, chain, call target, value and calldata, then binds the receipt to a canonical block covered by finalized height. NFT effects additionally require the contract's exact lifecycle/stage/token event and owner/fee payload. ERC20 restitution requires the exact transfer event. Cancellation candidates are persisted before submission and only finalized nonce consumption proves the original effect impossible.

Open finding: NFT observation maps every unknown original to `Unseen`, including a durable `Canceling` preparation. The owning lifecycle then attempts original submission, which rejects that recovery state. Pending cancellation must remain a non-egress pending outcome. The value adapter and caller need the same handling. Concurrent cancellation can also invalidate a worker's row between selection and submission; an expected race must not abort reconciliation for every later row. The author has been asked to correct these paths with focused regression coverage.

Correction reread: owned preparation observation now checks all saved cancellation candidates and returns `Pending` while cancellation lacks finalized evidence. NFT maps it to a non-egress pending state; restitution only resubmits on an error-free `Unknown` observation. Submission verifies saved wire identity and treats a current cancelling/terminal state as an egress no-op, retaining strict rejection for unadmitted candidates. These bounded source findings are resolved; regression execution remains pending.

Root-authored pipeline fixture changes were separately reviewed by the Ethereum author. The strict fake store mirrors clone isolation, nonce uniqueness, CAS and immutable-field preservation; the RPC fixture decodes actual submitted wire and supplies canonical finalized evidence. One checksum-case message assertion was corrected following that independent review. No build or test has executed for these changes.

The contract has its own review record. Full source handoff, remaining review fixes, schema generation and the integrated runtime gates remain required before acceptance.

## Independent adversarial addendum

The NFT/core author independently reread `EthereumProvider.Preparation.cs`, `EthereumProvider.Recovery.cs`, `EthereumProvider.Nft.cs`, `EthereumProvider.ValueTransfer.cs`, `SurrealEthereumPreparationStore.cs`, recovery controllers/worker, and `EthereumPreparedRecoveryTests.cs`. No source was changed and no tests were run in this review.

A concrete recovery gap remains in `Services/Ethereum/EthereumPreparationRecoveryWorker.cs`: it enumerates `GetAllEnabledProviders()`. The factory implementation in `Providers/Blockchain/BlockchainProviderFactory.cs` yields exactly one preferred network per chain, selecting Mainnet before Testnet before Devnet. With multiple Ethereum networks enabled, authorized durable preparations on the lower-priority network never receive unattended replay after a caller crash. The author was asked to enumerate every enabled Ethereum network (or a durable network inventory), retain the saved network identity, and isolate network failures. Acceptance requires a regression with two enabled Ethereum networks and another where the first network fails while the second still reconciles.

The inspected preparation path preserves immutable call fingerprints, owned nonce reservations and signed bytes across CAS/restart; egress requires a separately durable submission authorization. Cancellation candidates persist before broadcast and only exact finalized nonce consumption establishes no original effect. Pending cancellation remains non-egress. NFT and value observation bind the admitted call and canonical finalized receipt before accepting semantic event evidence. No additional blocking defect was found in those bounded paths. Runtime acceptance still requires the integrated tests and actual local contract/RPC evidence; the authored fixture itself is not proof of successful execution.

Correction reread for the independent addendum: `EthereumPreparationRecoveryWorker.SweepAsync` now enumerates configured enabled Ethereum networks directly, resolves each exact network provider, and catches/logs failures per network while rethrowing cancellation. The existing store query remains scoped to the saved network. `EthereumPreparationRecoveryWorkerTests.Every_enabled_network_is_replayed_even_when_first_network_fails` covers both enabled networks, disabled Mainnet, and a first-network outage without blocking the second. I reread the changed worker, configuration-manager contract, and test source; the concrete finding is resolved. No tests were executed. The bounded independent source pass is complete, pending integrated runtime verification.

Follow-up assessment input, not a resolved finding: root identified that `EthereumProvider.ConfiguredChainId` still uses chain-level metadata across networks. Its configuration/identity consequences belong in the next full-goal assessment; this worker fix does not establish correct per-network chain-id configuration.
