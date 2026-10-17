# Services/Blockchain

## §client-signed operations (D-3, round 4)

A user-driven Transfer whose source is the caller's own **External** wallet is not signed by custody (custody never holds
that key). `BlockchainOperationManager.ExecuteTransferAsync` asks the chain's `IClientSignedTransferModule` for a plan:
the exact unsigned bytes (Algorand msgpack txn, Ethereum EIP-1559 unsigned RLP, Solana legacy message) plus the facts the
chain will judge them by (Algorand predicted txid + last-valid round, Solana last-valid block height, Ethereum nonce). The
row moves to `AwaitingSignature` with the plan in `Parameters["ClientSigningPlan"]` and the manager's derived idempotency
key in `Parameters["ExecutionIdempotencyKey"]`; the claim stays `InProgress`. A provider that merely *says* a client must
sign, with no plan, fails closed (nothing to verify a later signature against). Tenant-driven ops never take this path.

`ClientSignedOperationService` owns the rest:

- **Submit** (`POST /api/blockchain/operations/{id}/submit-signed`): owner + acting tenant pinned to the row, signing scope
  `transfer:sign`. The module accepts only the plan's exact bytes signed by the plan signer (Algorand: re-encode the saved
  txn under the submitted signature and require byte equality, since the SDK cannot round-trip every axfer; Ethereum:
  `EthereumSignedEnvelope.Verify` — exact unsigned RLP + recovered signer; Solana: message equality + fee-payer
  signature). The signed bytes are persisted by a status CAS (`AwaitingSignature|NeedsOperator → PendingConfirmation`)
  **before** the first broadcast. Re-submitting identical bytes re-broadcasts; different bytes are a conflict (two valid
  signatures of one Solana message are two transactions).
- **Reconcile** (sweep + receipt-triggered): `ReconciliationService` delegates any row carrying a plan. Terminal outcomes
  come only from chain proof: `Confirmed` → `Completed`; `FailedOnChain`, `Expired` (validity window passed and the chain
  proves the plan never landed), or `NonceConsumed` by an attributed different transaction (submitted or not; an
  unattributed consumer parks as `NeedsOperator`, since it might be the plan itself) → `Failed` with
  `ClientSigningOutcome` = `FailedOnChain|Abandoned|Superseded`. Every terminal state settles both the derived key and
  any parent `IdempotencyKey`, so a retry is never answered "still in progress" forever.
- **Stuck rows** (`Reconciliation:ClientSignatureTtlSeconds`, default 1800): the chain is re-observed first, because the
  signer may have broadcast the bytes themselves (Algorand by predicted txid; Solana by scanning the signer's finalized
  signatures since the plan's context slot). Algorand/Solana plans past their window close as `Abandoned`. An Ethereum
  signature never expires, so an unsubmitted plan parks as `NeedsOperator` (also when the nonce is consumed but the
  consumer cannot be attributed — it might be the plan itself).
- **Operator** (`GET /api/operator/blockchain-operations/stuck`, `POST …/{id}/resolve`): the operator may supply the
  consuming transaction id; the service re-observes and applies only what the chain proves, with an audit in
  `Parameters["OperatorResolution"]`. A submitted row is always judged by its own bytes.

`NeedsOperator` is a new `operation_log.status` member (`ClientSigningStatus`); `OperationStatus` (Models, L1) does not
list it yet. Store: `SurrealOperationLogClientSigningStore` (status CAS; `// lock:` the row itself).
