# API Safety Hardening — Plan

## Tasks

### Bridge correctness
1. [ ] Add unique constraint/index on bridge source dedupe key: `LockTxHash` and the Wormhole `(EmitterChainId, EmitterAddress, Sequence)` tuple (`AZOADbContext` entity config)
2. [ ] Add a consumed-VAA ledger (unique on VAA digest); reject redeem if the VAA digest is already consumed
3. [ ] Make `RedeemWithVAAAsync` atomic: conditional state transition `UPDATE … SET status=Redeeming WHERE status=VAAReady`, assert exactly one row affected, persist **before** the on-chain call (`CrossChainBridgeService.cs:129-163`)
4. [ ] Add partial-failure compensation: on post-broadcast failure, mark `Failed`/`Refunded` with a real on-chain reversal path; fix `ReverseBridgeAsync` (`:189-205`) to actually reverse or clearly mark manual-intervention
5. [ ] Check `mintResult.IsError` in the trusted flow before stamping `Completed` (`:335,351`)
6. [ ] Reorder Wormhole initiate so the tracking row is persisted before/atomically with the on-chain lock (`:303-304`), or add a recovery sweep for orphan locks
7. [ ] Remove dead `BridgeStatus.Minted` or wire `Redeeming→Minted→Completed` correctly
8. [ ] Bridge integration tests: concurrent double-redeem → exactly one mint; duplicate initiate → one bridge row; replayed VAA rejected; crash-before-save recovery

### Irreversible-op idempotency
9. [ ] Replace `OperationIdGenerator` with a deterministic content/client-supplied idempotency key (remove `DateTime.UtcNow.Ticks`; `OperationIdGenerator.cs:19,32`)
10. [ ] Accept a client `Idempotency-Key` header on swap-execute / wallet-transfer / topup / bridge-initiate; persist and check before any irreversible effect
11. [ ] Add an "already executed" lookup in `BlockchainOperationManager.ExecuteAsync` keyed on the idempotency key (no fresh-GUID re-execution)
12. [ ] Dedupe `AlgorandFaucet.DispenseAsync` (`AlgorandFaucet.cs:33-79`) on idempotency key so a retried `POST /topup` dispenses once
13. [ ] Ensure `ExecuteWithRetryAsync` (`BaseBlockchainProvider.cs:173-232`) cannot re-broadcast an accepted tx (idempotency-key guard around broadcast)

### Chain reconciliation
14. [ ] Add a reconciliation service: for non-terminal ops/bridge tx, query chain confirmations and re-derive true status (not the local flag)
15. [ ] Background/triggered sweep for stuck `Redeeming`/`AwaitingVAA`/`AwaitingSignature` records
16. [ ] Reconciliation tests: kill mid-op → recovery converges to chain truth

### Validation, rate limiting, provider safety
17. [ ] Add FluentValidation validators for all swap/wallet/quest/bridge request models (~22 missing)
18. [ ] Add ASP.NET rate limiting middleware + per-API-key usage metering
19. [ ] Remove `InMemoryStorageProvider` from production DI (`Program.cs:164-169`); keep as a test double only

### Verification
20. [ ] `dotnet build` — zero warnings
21. [ ] All unit + integration tests passing (incl. new safety tests)
22. [ ] Document the residual-risk surface and required ops runbook for stuck/failed bridge tx
