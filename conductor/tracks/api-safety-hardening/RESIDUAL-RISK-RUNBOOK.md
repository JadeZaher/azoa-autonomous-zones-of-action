# API Safety Hardening — Residual Risk & Operations Runbook

**Audience:** Operators/SREs running the OASIS cross-chain bridge API pre-launch.  
**Scope:** Stuck/failed bridge transactions, idempotency record interpretation, reconciliation.  
**Last updated:** 2026-05-16 (api-safety-hardening track, Wave 1–3 consolidated)

---

## 1. What is now SAFE (exactly-once guarantees delivered)

### Bridge Redeem (Wormhole Mode)
**Mechanism:** `CrossChainBridgeService.RedeemWithVAAAsync` (`Services/CrossChainBridgeService.cs:140–303`)

**Safety guarantees:**
1. **Idempotency claim** (step 1, line 164): `IIdempotencyStore.TryClaimAsync` atomically claims the redeem. First caller to insert an `IdempotencyRecord` with the deterministic key wins; duplicates lose and replay the cached result.
2. **Atomic state transition** (step 2, line 186–207): `ExecuteUpdateAsync` with `WHERE status=VAAReady` predicate. Only the claim winner advances to `Redeeming`. Conditional UPDATE asserts exactly one row affected; if not, the bridge was already advanced by a concurrent request.
3. **VAA replay ledger** (step 3, line 210–245): Before any on-chain redeem, insert a `ConsumedVaaRecord` keyed by the VAA digest. UNIQUE constraint on `Digest` (`Models/ConsumedVaaRecord.cs:26`, migration `IX_ConsumedVaas_Digest`) blocks any duplicate digest from landing. A replayed VAA re-inserts the same digest → unique constraint violation → caught and rejected.
4. **On-chain call guarded** (step 4, line 259): Only the claim winner and replay-ledger winner calls `WormholeAdapter.RedeemTransferAsync`. Concurrent calls are barred by steps 1–3.
5. **Terminal write** (step 5, line 278–296): After successful on-chain redeem, `Redeeming→Completed` is atomic (WHERE status=Redeeming). If it fails (e.g., crash mid-flight), the on-chain mint is still recorded and reconciliation re-derives the true status.

**Proven by tests:**
- `ConcurrentDoubleRedeem_ResultsInExactlyOneMint` (unit test, SQLite): two concurrent redeem requests against the same bridge collide on the idempotency key; second request sees `InProgress` and is rejected. First request's mint lands once.
- `ReplayedVaa_IsRejected_NoSecondMint` (unit test, SQLite): a redeem that completes successfully marks the VAA digest consumed. A second redeem of the same VAA fails on the unique constraint of `ConsumedVaas.Digest`, never reaches the on-chain call.

### Server-Side Broadcast Idempotency
**Mechanism:** `BlockchainOperationManager.ExecuteAsync` + `AlgorandFaucet.DispenseAsync` (`Managers/BlockchainOperationManager.cs`, `Core/AlgorandFaucet.cs:33–79`)

**Safety:**
- Idempotency key is deterministic and content-addressed: `OperationIdGenerator.Generate(chain, operationType, walletAddress, params)` (`Core/OperationIdGenerator.cs`). No `DateTime.Ticks` — identical params always yield identical keys.
- `AlgorandFaucet.DispenseAsync` (the only server-side broadcaster in current codebase) accepts an idempotency key and passes it to `IIdempotencyStore.TryClaimAsync`. A retried topup dispenses once.
- `BaseBlockchainProvider.ExecuteWithRetryAsync` (`Core/Blockchain/Base/BaseBlockchainProvider.cs:173–232`) has a `RetrySafety.Broadcast` mode that retries ONLY on provable pre-send failures (SocketException with specific inner types). Post-send ambiguous failures (timeout, null status, 5xx, 429) are rethrown unretried for reconciliation to handle.

### VAA Verification (Fail-Closed)
**Mechanism:** `WormholeAdapter.VerifyVAAAsync` (`Services/WormholeAdapter.cs:136–156`)

**Safety:**
- Enforces VAA structure, version, ascending-unique guardian indices, Byzantine quorum.
- **secp256k1 ecrecover IS implemented (2026-05-17)**: `Secp256k1VaaSignatureVerifier` (SEC1 §4.1.6 recovery on secp256k1 via vetted Bouncy Castle 2.6.2; reuses the existing managed `Keccak256`) is registered in DI. With `RequireFullSignatureVerification=true` (default), each Guardian signature is cryptographically recovered and matched against the config-driven Guardian set. The fail-closed property is preserved: if the verifier is NOT registered (or the Guardian set is empty/unconfigured), no VAA can be trusted. Crypto path proven by unit tests; live-network validation + real testnet/mainnet Guardian sets remain an ops/config gate (see §2 / §4).

### Reconciliation (Chain-Source-of-Truth Re-derivation)
**Mechanism:** `Services/Reconciliation/ReconciliationService.cs` + `ReconciliationHostedService` (hosted background sweep)

**Safety:**
- Re-derives bridge/operation status from on-chain confirmations only. Never broadcasts or reverses automatically.
- Bridge non-terminal states (`Initiated`, `Locked`, `AwaitingVAA`, `VAAReady`, `Redeeming`) are scanned periodically (default 300s, configurable via `Reconciliation:IntervalSeconds` in appsettings.json).
- For each non-terminal record: probes the on-chain tx hash (`LockTxHash` for lock phase, `RedemptionTxHash`/`MintTxHash` for redeem phase).
- **Verdict logic** (line 178–241):
  - **Confirmed:** positive chain signal (`confirmed=true` on Algorand, `success=true` on Solana) → atomically advance status. Example: `Redeeming` + confirmed redemption tx → `Completed`.
  - **FailedOnChain:** explicit on-chain negative (`success=false`) → atomic transition to `Failed`.
  - **Unknown:** not-found tx, RPC error, or ambiguous result → do nothing (never auto-fail a not-found tx; may be mempool delay).
- **Hard-stuck flagging** (line 272–287): If a record is non-terminal for longer than `BridgeHardStuckAfterSeconds` (default 3600s / 1 hour) and still unresolvable, reconciliation logs `MANUAL INTERVENTION REQUIRED` (ERROR level) with all context (bridge ID, status, source/target chains, tx hashes). The record is NOT auto-failed; operator must investigate.

### Input Validation
**Coverage:** 33 FluentValidation validators (e.g., `BridgeInitiateRequestValidator`, `SwapQuoteRequestValidator`, `WalletTransferRequestValidator`) registered in DI (`Program.cs:34`).

### Rate Limiting
**Mechanism:** ASP.NET built-in rate limiter (no external NuGet), configured in `appsettings.json:RateLimiting`.
- **Global policy:** 120 requests/60s per partition key (IP, API key, avatar ID — most specific wins).
- **Financial policy** (`[EnableRateLimiting("financial")]`): 10 requests/60s on swap-execute, wallet-topup, bridge-initiate/redeem/reverse.
- Rejection: 429 Too Many Requests + `Retry-After` header.
- Partitioning: `apikey:{sha256(X-Api-Key)}` (hashed so the secret never lands in state) → `avatar:{sub}` → `ip:{remoteIp}`.

### InMemoryStorageProvider Removed from Production
**Status:** Deleted from DI registration (`Program.cs`). `EfStorageProvider` is now the sole `IOASISStorageProvider`. InMemoryStorageProvider class still exists for integration tests to register explicitly; never used in production.

---

## 2. RESIDUAL RISKS (each: risk → impact → mitigation/monitoring)

### Risk: Crash Between On-Chain Success and `Redeeming→Completed` Write

**Scenario:** `RedeemTransferAsync` succeeds (mint lands on target chain) but the process crashes before the `Redeeming→Completed` UPDATE executes (line 278–296, `CrossChainBridgeService.cs`).

**Impact:** Bridge row stuck in `Redeeming` state. The mint has executed once on-chain (no double-mint: VAA digest is consumed and cannot be replayed). Client does not receive the success response.

**Mitigation & operator action:**
1. Reconciliation will automatically detect this: background sweep (or manual trigger via `/api/bridge/{id}/reconcile` if implemented) probes the redemption tx hash.
2. On positive confirmation, reconciliation atomically advances `Redeeming→Completed` and logs INFO level "bridge tx {id} {From} -> {To}".
3. Operator does NOT need to act unless hard-stuck threshold is exceeded. If it is:
   - Query the database directly:
     ```sql
     SELECT Id, Status, RedemptionTxHash, MintTxHash, TargetChain FROM BridgeTransactions
     WHERE Status = 'Redeeming' AND CreatedAt < now() - interval '1 hour';
     ```
   - For each row, verify the redemption/mint tx hash with the target chain RPC:
     ```bash
     # Algorand example (indexer)
     curl "https://testnet-idx.algonode.cloud/transactions/{txHash}"
     
     # Solana example
     curl -X POST https://api.devnet.solana.com -H "Content-Type: application/json" \
       -d '{"jsonrpc":"2.0","id":1,"method":"getTransaction","params":["<txHash>"]}'
     ```
   - If on-chain status is confirmed: manually update the row (safe, idempotent):
     ```sql
     UPDATE BridgeTransactions SET Status = 'Completed', CompletedAt = now()
     WHERE Id = '<id>' AND Status = 'Redeeming';
     ```
   - If on-chain status is FAILED: mark the bridge `Failed` instead (no double-mint to worry about):
     ```sql
     UPDATE BridgeTransactions SET Status = 'Failed', ErrorMessage = '...', CompletedAt = now()
     WHERE Id = '<id>' AND Status = 'Redeeming';
     ```

---

### Risk: Idempotency Record Stuck `InProgress` (Process Death Mid-Flight)

**Scenario:** Process crashes after `IIdempotencyStore.TryClaimAsync` succeeds (claim won, record inserted) but before `CompleteAsync`/`FailAsync` is called.

**Impact:** Idempotency record remains `InProgress` indefinitely. A retry of the same logical operation sees the record in `InProgress` state and is rejected with "already in progress — request rejected" (e.g., line 179, `RedeemWithVAAAsync`).

**No value risk:** The on-chain effect (mint, lock, etc.) is already recorded in the `ConsumedVaas` ledger or bridge state, so a retry does NOT duplicate it. The IN-PROCESS state is merely blocking; it does NOT cause a double-spend.

**Mitigation & operator action:**
1. Investigate whether the original request actually reached the on-chain call and succeeded:
   - Check bridge/operation DB state: is the bridge `Completed`/`Failed` or is it in a later terminal state? If yes, the `InProgress` record is a false positive.
   - If bridge state is non-terminal, check the on-chain tx hash (if recorded) via RPC.
2. If the on-chain effect succeeded but the record is stuck `InProgress`:
   - Manually mark it `Completed` (safe; only affects replay logic):
     ```sql
     UPDATE IdempotencyRecords SET State = 1 -- 1 = Completed
     WHERE Key = '<idempotency_key>' AND State = 0; -- 0 = InProgress
     ```
   - Then a retry will see the `Completed` state and replay the cached `ResultPayload`.
3. If the on-chain effect was never observed:
   - Check if the error was pre-send (safe to retry) or post-send (ambiguous; needs reconciliation).
   - Update the record to `Failed` if you are confident the original call did not land:
     ```sql
     UPDATE IdempotencyRecords SET State = 2, Error = 'operator-resolved: original never broadcast'
     WHERE Key = '<idempotency_key>';
     ```
   - Then a retry will see the `Failed` state and surface the error.

---

### Risk: Reconciliation Provider Gap (Cannot Distinguish "Dropped" from "Pending")

**Scenario:** `IBlockchainProvider.GetTransactionStatusAsync` returns an error (tx not found) on both Algorand and Solana. This is ambiguous: the tx could be in mempool (not yet observed), dropped, or never broadcast.

**Impact:** Reconciliation cannot auto-fail genuinely-dropped transactions. A `Redeeming` bridge with a non-existent redemption tx cannot be safely progressed without manual confirmation.

**Residual gap documented:** See `ReconciliationService.cs:484–495` (ClassifyTx doc-comment): "there is no provider capability that cleanly distinguishes 'dropped/failed' from 'not yet observed'".

**Mitigation:**
- **Short-term (pre-launch):** Operator manually confirms chain truth and updates the bridge row:
  ```sql
  -- If redemption is genuinely dropped (not in mempool or chain):
  UPDATE BridgeTransactions SET Status = 'Failed', 
    ErrorMessage = 'operator-confirmed: redemption tx never landed'
  WHERE Id = '<id>' AND Status = 'Redeeming';
  
  -- Then manually retry the redeem (with same VAA, same idempotency key if needed).
  ```
- **Future (post-launch enhancement):** Implement a tri-state provider method:
  ```csharp
  Task<(TxConfirmationState state, object? details)> GetTransactionConfirmationAsync(string txHash, CancellationToken ct);
  // returns: Confirmed | Dropped | Pending
  ```
  Then reconciliation can auto-fail `Pending` → `Unknown` transitions after a timeout, and auto-advance confirmed ones.

---

### Risk: VAA Signature Verification — Implemented; Live-Network Validation Pending (ops/config)

**Status (2026-05-17):** secp256k1 ecrecover IS now implemented and registered.
`Services/Wormhole/Secp256k1VaaSignatureVerifier.cs` performs standard SEC1
§4.1.6 ECDSA public-key recovery on curve secp256k1 over the 32-byte canonical
VAA digest (`keccak256(keccak256(body))`, consumed as-is — NOT re-hashed),
derives the Ethereum-style Guardian address (last 20 bytes of
`keccak256(uncompressedPubKey[1..65])`, reusing the existing managed
`Keccak256`), and constant-time-compares it to the expected Guardian for
`(guardianSetIndex, GuardianIndex)` from config. Curve/point/modular math is the
vetted Bouncy Castle 2.6.2 library (`BouncyCastle.Cryptography`, official
`LegionOfTheBouncyCastle`, pure-managed, no native binaries; bound via the
`BCCrypto2` extern alias so it never collides with the legacy 1.8.8 BC pulled
transitively by the Algorand2/Solana SDKs). Per-signature recovery + membership
only; signature count / Byzantine quorum / ascending-unique indices remain the
adapter's responsibility (unchanged). Fail-closed: any malformed r/s (zero or
≥ curve order n), bad recovery id, out-of-range/unconfigured Guardian index,
empty placeholder set, or thrown exception ⇒ `false` (never a value mistaken
for "valid"). Registered in `Program.cs` as
`AddScoped<IVaaSignatureVerifier, Secp256k1VaaSignatureVerifier>()`.

**Proven by tests (unit, self-contained, no live network):** 17 tests in
`tests/.../Services/Secp256k1VaaSignatureVerifierTests.cs` generate a real
secp256k1 keypair, build a VAA in the exact `ParseVAA` wire format, compute the
canonical double-keccak digest the same way the adapter does, sign with the
correct recovery id, and assert: valid sig + correct Guardian ⇒ true & adapter
`VerifyVAAAsync` succeeds; tampered digest/body ⇒ false; signer not in set ⇒
false; wrong recovery id ⇒ false; malformed r (=0) / s (≥ n) ⇒ false;
below-quorum set of valid sigs ⇒ adapter still rejects (quorum owned by
adapter); round-trip determinism; and the appsettings devnet Guardian address
is independently re-derived from the documented Wormhole devnet private key.
All pre-existing `WormholeAdapter` fail-closed tests stay green (the
no-verifier ⇒ fail-closed path is unchanged and still tested).

**What remains (ops/config gate, NOT a code gap):**
1. **Fill the real mainnet/testnet Guardian sets.** `appsettings.json`
   `Blockchain:Wormhole:GuardianSets` ships with **NO** testnet/mainnet entries
   (the prior empty placeholder was removed) — an absent set fails closed (no
   VAA can verify against it). They MUST be populated + independently verified
   from the official Wormhole Guardian set per the procedure +
   sign-off checklist in **`GUARDIAN-SET-SETUP.md`** (this track) before the
   corresponding network is enabled for value flow. Only the devnet ("tilt")
   set is populated (in `appsettings.Development.json`). Code sign-off gate:
   `scripts/passoff.ps1`.
2. **Validate against the live Wormhole Guardian network** on testnet/devnet:
   fetch a real signed VAA and confirm the implemented recovery accepts the
   genuine Guardian signatures end-to-end. Unit tests prove the crypto path with
   a synthetic Guardian set; they cannot prove agreement with the live Guardian
   network — that is an ops validation step.

**Mitigation / posture:**
- The fail-closed escape hatch still exists: `RequireFullSignatureVerification`
  default stays `true`; setting it `false` (devnet/testnet only, NEVER where
  real value moves) skips crypto entirely. With the verifier now registered and
  the default `true`, genuine verification runs.
- Do NOT enable Wormhole value flow on testnet/mainnet until items 1–2 above are
  completed and signed off by ops.

---

### Risk: EF Migration Baseline — `BridgeTransactions` Table Pre-Existence

**Scenario:** The migration `20260516224425_AddIdempotencyAndBridgeSafetyConstraints` includes `CreateTable("BridgeTransactions")` (line 14–47). This is because `BridgeTransactions` was **never created by any prior migration**; it only materialized via `EnsureCreated()` in old test harnesses.

**Impact:**
- **Fresh/greenfield database:** `Database.Migrate()` on startup creates `BridgeTransactions`, `ConsumedVaas`, `IdempotencyRecords`, and all indexes correctly.
- **Pre-existing database with physical `BridgeTransactions` table** (e.g., from a prior `EnsureCreated()` call): migration fails with "relation 'BridgeTransactions' already exists" (PostgreSQL error 42P07).

**Operator remediation:**

**Case 1: Pre-launch environment where you can drop the table:**
```sql
-- Backup first!
DROP TABLE IF EXISTS "BridgeTransactions" CASCADE;
-- Then re-run migrations
dotnet ef database update
```

**Case 2: Running environment where you cannot drop (has real data):**
Split the migration or wrap the create in a conditional:
```csharp
// In migration Up():
migrationBuilder.Sql(@"
  CREATE TABLE IF NOT EXISTS ""BridgeTransactions"" (
    -- columns...
  );
");
```
Then re-apply with `dotnet ef database update`.

**Best practice:** On a greenfield deployment, ensure your initial database is empty before running migrations. The program (`Program.cs:22`) calls `db.Database.Migrate()` on startup — ensure there are no pre-existing tables from a prior `EnsureCreated()` call.

---

### Risk: Rate Limiter State is In-Memory Per-Instance

**Scenario:** Rate limiter uses ASP.NET's built-in fixed-window limiter with in-memory state. Each process instance maintains its own counters.

**Impact:** Horizontal scale-out (multiple API replicas) will not have shared metering. Each instance gets its own quota. A client hitting 10 requests/60s against instance 1 and another 10 against instance 2 both succeed (combined 20 requests, above the global 10-request limit).

**Mitigation:**
- **Single-instance pre-launch:** No issue. Fixed-window per-instance is sufficient.
- **Multi-instance deployment:** Implement a distributed rate-limit store (e.g., Redis fixed-window counter keyed by partition key + window timestamp). See .omc/notepad.md § WAVE 3 for current status.

---

### Risk: Swap-Execute Accepts Idempotency-Key But Does Not Consume It

**Scenario:** `SwapController.GetSwapTransaction` reads the `Idempotency-Key` header (task 10 threading) but does not pass it to `ISwapManager.GetSwapTransactionAsync` for use.

**Impact:** None. Swap-execute returns an unsigned transaction; the client signs and broadcasts it. There is no server-side irreversible effect to dedupe. The header is accepted (for forward-compat and audit) but intentionally unused.

**Note:** This is correct behavior for a client-signed swap. If a future server-side submit endpoint is added, wire the key at that time.

---

### Risk: Integration Test Harness Uses Destructive Teardown on Persistent DB

**Scenario:** `OASIS.WebAPI.IntegrationTests` was built for ephemeral EF-InMemory DBs. It now points at the persistent Postgres (`tests/run-tests.ps1` auto-spins `oasis-postgres` container) but still calls `EnsureDeleted()` in teardown and runs tests in parallel.

**Impact:** Tests race a shared database. A parallel test teardown may delete data while another test is reading, causing spurious failures.

**Mitigation (deferred follow-up):**
- Disable xUnit parallelization (set `MaxParallelThreads = 1` in xunit.runner.json or runner config).
- Remove destructive `EnsureDeleted()` teardown; migrate the schema once on startup and use data isolation (e.g., unique test IDs or per-test transactions with rollback).
- Status: This is a known deferred task; the **unit test suite (493/493 tests, all passing)** is the authoritative gate. Integration harness refactor is Wave 4+ scope.

---

### Risk: No Distributed API-Key Metering / Billing

**Scenario:** Current rate limiter partitions by API key (hashed) but does NOT persist usage counters. No billing/quota system.

**Impact:** Operators cannot accurately meter API usage over longer windows (daily/monthly). Usage is partition-based (burst limiting only).

**Mitigation:** Residual. Future enhancement: add usage counters (`UsageLog` table keyed on API key + day/hour) populated by a background job. This is outside the Tier-0 pre-launch scope.

---

## 3. OPS RUNBOOK — Stuck/Failed Bridge Transaction

### List Non-Terminal Bridge Transactions

**Query non-terminal records (last 24 hours, showing age):**
```sql
SELECT 
  Id, AvatarId, SourceChain, TargetChain, Status,
  now() - CreatedAt as age,
  LockTxHash, RedemptionTxHash, MintTxHash, ErrorMessage
FROM BridgeTransactions
WHERE Status IN ('Initiated', 'Locked', 'AwaitingVAA', 'VAAReady', 'Redeeming')
  AND CreatedAt > now() - interval '24 hours'
ORDER BY CreatedAt DESC;
```

**Query hard-stuck records (non-terminal and older than 1 hour):**
```sql
SELECT 
  Id, Status, Mode,
  now() - CreatedAt as age_seconds,
  SourceChain, TargetChain,
  LockTxHash, RedemptionTxHash, MintTxHash,
  ErrorMessage
FROM BridgeTransactions
WHERE Status IN ('Initiated', 'Locked', 'AwaitingVAA', 'VAAReady', 'Redeeming')
  AND CreatedAt < now() - interval '1 hour'
ORDER BY CreatedAt ASC;
```

### Inspect Idempotency & VAA Records

**For a stuck bridge transaction:**
```sql
-- Idempotency record
SELECT * FROM IdempotencyRecords
WHERE Key LIKE 'bridge-%' || '<bridge_id>'
ORDER BY CreatedAt DESC LIMIT 5;

-- Consumed VAA record (if Wormhole mode)
SELECT * FROM ConsumedVaas
WHERE BridgeTransactionId = '<bridge_id>';
```

**Interpret states:**
- `IdempotencyRecords.State = 0` (InProgress): claim was won, effect may be in-flight or stalled. Check bridge Status and on-chain tx hash.
- `IdempotencyRecords.State = 1` (Completed): effect succeeded; ResultPayload holds the cached result (tx hash for redeem).
- `IdempotencyRecords.State = 2` (Failed): effect failed; Error holds the reason.
- `ConsumedVaas.Digest` present: VAA was consumed; a second redeem of this VAA is impossible (replay-protected).

---

### Decision Tree: Is the Bridge Safe to Advance?

```
1. Check on-chain status:
   ├─ Bridge Status = Redeeming
   │  └─ Query target chain for RedemptionTxHash / MintTxHash
   │     ├─ TX CONFIRMED on-chain
   │     │  └─ SAFE TO MARK COMPLETED
   │     │     UPDATE BridgeTransactions SET Status = 'Completed', CompletedAt = now()
   │     │     WHERE Id = '<id>' AND Status = 'Redeeming';
   │     ├─ TX NOT FOUND (not in chain, not in mempool)
   │     │  └─ MANUAL REVIEW REQUIRED
   │     │     - Check reconciliation logs for "MANUAL INTERVENTION REQUIRED"
   │     │     - If >= 1 hour old, operator must decide:
   │     │       a) Retry the redeem (reconciliation will re-probe; safe, idempotent)
   │     │       b) Mark bridge Failed (safe; no double-mint via ConsumedVaas)
   │     └─ TX ERROR / RPC TIMEOUT
   │        └─ WAIT & RECHECK
   │           Reconciliation sweep will re-probe periodically.
   │
   ├─ Bridge Status = Locked or AwaitingVAA (trusted mode)
   │  └─ Query source chain for LockTxHash
   │     ├─ TX CONFIRMED on-chain
   │     │  └─ SAFE TO ADVANCE
   │     │     UPDATE BridgeTransactions SET Status = 'AwaitingVAA' (or VAAReady if mint was broadcast)
   │     │     WHERE Id = '<id>' AND Status = 'Locked';
   │     └─ TX NOT FOUND
   │        └─ MANUAL REVIEW REQUIRED (see above)
   │
   └─ Bridge Status = Initiated or VAAReady
      └─ NO SAFE DIRECT ADVANCE
         These states indicate no irreversible on-chain call has been made yet.
         Manual action: Inspect error logs; retry initiate or fetch-VAA.
```

---

### Explicit DO-NOT List

**NEVER:**
- Delete a `ConsumedVaas` digest to "retry" a redeem. The digest is a replay ledger; erasing it breaks the exactly-once guarantee.
- NULL an `IdempotencyKey` on a bridge transaction. The key links the bridge to its idempotency record; breaking that link orphans the dedup logic.
- Run a faucet dispense or bridge redeem manually without first checking the idempotency record. You will bypass the exactly-once claim and may double-spend.
- Manually call `WormholeAdapter.RedeemTransferAsync` or `TargetProvider.MintWrappedAsync` outside the service layer. These irreversible calls must be guarded by idempotency + VAA replay protection.
- Mark a bridge `Completed` without confirming the on-chain tx hash landed on the target chain. A false Completed state will block reversal requests.

---

### Trigger Reconciliation On-Demand

**Background sweep interval:**
- Configured in `appsettings.json`: `Reconciliation:IntervalSeconds` (default 300 = 5 minutes).
- Hosted service `ReconciliationHostedService` runs in the background, calling `ReconciliationService.ReconcileBridgeAsync` and `ReconcileOperationsAsync` on each tick.

**Manual trigger (if an API endpoint is exposed):**
```bash
# Trigger full bridge reconciliation sweep
curl -X POST https://your-api.com/api/reconciliation/bridges \
  -H "Authorization: Bearer <jwt>" \
  -H "Content-Type: application/json"

# Reconcile a specific bridge transaction (if endpoint exists)
curl -X POST https://your-api.com/api/bridge/<bridge_id>/reconcile \
  -H "Authorization: Bearer <jwt>"
```

**Check reconciliation logs:**
```bash
# Follow logs for reconciliation events
docker logs <api-container> -f | grep -i reconciliation

# Search for "MANUAL INTERVENTION REQUIRED" — critical flag
docker logs <api-container> -f | grep -i "manual intervention"
```

**Sample log (positive reconciliation):**
```
[INF] Reconciliation: bridge tx wh_bridge_abc123 VAAReady -> Completed 
      (chain-confirmed redeem tx 0x789def on Solana)
```

**Sample log (hard-stuck):**
```
[ERR] Reconciliation: MANUAL INTERVENTION REQUIRED — bridge tx bridge_xyz789
      stuck in Redeeming for 3605.2s (chain status for redeem tx 0x456abc on Solana 
      is indeterminate). Source=Algorand Target=Solana LockTx=... MintTx=... 
      RedeemTx=... Reconciliation will NOT auto-fail or auto-reverse — 
      operator must resolve.
```

---

### Example: Operator Workflow for Hard-Stuck Bridge

**Scenario:** Reconciliation has flagged bridge `wh_bridge_abc123` hard-stuck in `Redeeming` for >1 hour. The redemption tx hash is `0x789def` (Solana).

**Steps:**

1. **Query the database:**
   ```sql
   SELECT * FROM BridgeTransactions WHERE Id = 'wh_bridge_abc123';
   ```
   Output:
   ```
   Id: wh_bridge_abc123
   Status: Redeeming
   SourceChain: Algorand
   TargetChain: Solana
   RedemptionTxHash: 0x789def
   LockTxHash: (null, Wormhole mode)
   IdempotencyKey: bridge-redeem:wh_bridge_abc123:0a1b2c3d...
   ```

2. **Check Solana RPC for the redemption tx:**
   ```bash
   curl -X POST https://api.devnet.solana.com \
     -H "Content-Type: application/json" \
     -d '{
       "jsonrpc": "2.0",
       "id": 1,
       "method": "getTransaction",
       "params": ["0x789def"]
     }'
   ```
   Response possibilities:
   - **`"result": { "meta": {"status": {"Ok": null}}, ... }`** → TX CONFIRMED ✓
   - **`"result": null`** → NOT FOUND (ambiguous; may still be in mempool)
   - **`"error": { ... }`** → RPC error (try again later or use a different RPC)

3. **If TX CONFIRMED:**
   ```sql
   UPDATE BridgeTransactions 
   SET Status = 'Completed', CompletedAt = now()
   WHERE Id = 'wh_bridge_abc123' AND Status = 'Redeeming';
   ```
   Verify 1 row affected. Bridge is now safe and terminal.

4. **If TX NOT FOUND (ambiguous):**
   - Option A: Wait 5 more minutes and recheck (mempool delay).
   - Option B: Trigger a reconciliation re-probe manually or wait for the next sweep.
   - Option C: If you are confident the original redeem request never landed and want to retry:
     ```sql
     UPDATE IdempotencyRecords 
     SET State = 2, Error = 'operator-resolved: original tx never landed, safe to retry'
     WHERE Key = 'bridge-redeem:wh_bridge_abc123:0a1b2c3d...' AND State = 0;
     
     -- Then call POST /bridge/{id}/redeem again with the same Idempotency-Key header
     -- (or let it derive a new one; idempotency is content-addressed by VAA digest).
     ```

5. **If TX FAILED on-chain:**
   ```sql
   UPDATE BridgeTransactions 
   SET Status = 'Failed', ErrorMessage = 'operator-confirmed: redemption tx failed on Solana', 
       CompletedAt = now()
   WHERE Id = 'wh_bridge_abc123' AND Status = 'Redeeming';
   ```
   Mark as `Failed` and escalate to the support/finance team for manual refund/reversal.

---

### Trusted Mode (Lock + Mint) Specific Scenarios

**Scenario: Lock succeeded, mint failed (bridge in `Locked` status, hard-stuck).**

**Operator steps:**

1. **Confirm lock on-chain:**
   ```sql
   SELECT LockTxHash FROM BridgeTransactions WHERE Id = '<id>';
   -- Verify LockTxHash with source chain RPC (Algorand/Solana)
   ```
   If lock confirmed:

2. **Determine why mint failed:**
   - Check bridge `ErrorMessage` field: may contain the mint error.
   - If blank, either:
     a) Mint was never attempted (rare; check logs), or
     b) Mint was attempted, failed, but the error was not recorded.

3. **Manual remediation:**
   - Option A: Retry the mint call (manually call the BlockchainOperationManager or submit a mint transaction).
   - Option B: Call `ReverseBridgeAsync` to burn the locked asset on the source chain (if source chain supports it).
   - Option C: Manual refund (outside API; requires vault/operator private key).

4. **Mark bridge state:**
   After successful mint retry, manually advance:
   ```sql
   UPDATE BridgeTransactions 
   SET Status = 'Completed', TargetTokenId = '<wrapped_token_id>', 
       MintTxHash = '<mint_tx_hash>', CompletedAt = now()
   WHERE Id = '<id>' AND Status = 'Locked';
   ```

---

### Reversal Workflow (Bridge Reversal / Refund)

**Scenario:** Client wants to reverse a completed bridge (burn wrapped asset, release original).

**API call:**
```bash
curl -X POST https://your-api.com/api/bridge/<bridge_id>/reverse \
  -H "Authorization: Bearer <jwt>" \
  -H "Content-Type: application/json" \
  -d '{"sourceRecipientAddress": "<refund_address>"}'
```

**Expected behavior:**
1. Bridge must be in `Completed` status.
2. Service atomically transitions `Completed → Reversing` (the EXPLICIT reversal-in-flight state — distinct from the forward redeem's `Redeeming`, so reversal provenance is unambiguous and never inferred from a `CompletedAt` timestamp or idempotency-key sniff).
3. Calls `TargetProvider.BurnWrappedAsync` to burn the wrapped token on the target chain, which releases the original on the source chain.
4. On success: `Reversing → Refunded` (terminal).
5. On failure: `Reversing → Failed` + `ErrorMessage = "MANUAL INTERVENTION REQUIRED: ..."`.

> Reconciliation sweeps `Reversing` rows (it is in `NonTerminalBridge`) but, by the explicit-state guard, NEVER advances or mutates one — a `Reversing` row is logged `MANUAL INTERVENTION REQUIRED` and left for the operator, because auto-advancing it would mask the in-flight refund/burn.

**If reversal fails (bridge in `Failed` state, `ErrorMessage` indicates manual intervention):**

```sql
SELECT Id, ErrorMessage FROM BridgeTransactions WHERE Status = 'Failed' AND ErrorMessage LIKE '%MANUAL INTERVENTION%';
```

**Manual reversal (requires vault/operator key on target chain):**
1. Burn wrapped token on target chain manually.
2. Release original asset to source chain manually (or wait for reconciliation if the burn succeeded but the state write failed).
3. Update bridge row:
   ```sql
   UPDATE BridgeTransactions 
   SET Status = 'Refunded', RedemptionTxHash = '<burn_tx_hash>', CompletedAt = now()
   WHERE Id = '<id>';
   ```

---

## 4. Pre-Launch Gating Checklist

| Task | Status | Owner | Notes |
|------|--------|-------|-------|
| ✓ Idempotency store (IIdempotencyStore impl) | DONE | Wave 1 | `Core/Idempotency/IdempotencyStore.cs` |
| ✓ Consumed-VAA ledger (ConsumedVaaRecord + unique digest) | DONE | Wave 1 | `Models/ConsumedVaaRecord.cs`, unique on Digest |
| ✓ Atomic bridge state transitions (WHERE Status=expected) | DONE | Wave 1 | `RedeemWithVAAAsync`, `ExecuteUpdateAsync` |
| ✓ Bridge integration tests (concurrent redeem, replay rejection) | DONE | Wave 1 | Unit tests passing (SQLite, real constraints) |
| ✓ Deterministic OperationIdGenerator (no DateTime.Ticks) | DONE | Wave 2 | `Core/OperationIdGenerator.cs` rewritten |
| ✓ Server-side broadcast idempotency (AlgorandFaucet) | DONE | Wave 2 | `AlgorandFaucet.DispenseAsync` keyed on idempotency key |
| ✓ Retry safety (RetrySafety.Broadcast mode) | DONE | Wave 2 | `ExecuteWithRetryAsync` guarded on pre-send failures |
| ✓ Reconciliation service + hosted sweep | DONE | Wave 3 | `ReconciliationService.cs`, background interval |
| ✓ FluentValidation for all financial models | DONE | Wave 3 | 33 validators, DI auto-registered |
| ✓ Rate limiting + per-API-key metering | DONE | Wave 3 | In-memory fixed-window; partitioned by API key hash |
| ✓ InMemoryStorageProvider removed from production DI | DONE | Wave 3 | `EfStorageProvider` sole IOASISStorageProvider |
| ✓ secp256k1 ecrecover in IVaaSignatureVerifier | DONE (code) | YOU | `Services/Wormhole/Secp256k1VaaSignatureVerifier.cs` — SEC1 §4.1.6 recovery on secp256k1 via vetted Bouncy Castle 2.6.2 (`BouncyCastle.Cryptography`, alias `BCCrypto2`); reuses existing managed `Keccak256`. Crypto path proven by 17 unit tests (synthetic Guardian set, real keypair, double-keccak digest). |
| ✓ Implement + register IVaaSignatureVerifier | DONE (code) | YOU | Registered `AddScoped<IVaaSignatureVerifier, Secp256k1VaaSignatureVerifier>()` in `Program.cs`. Guardian sets are config-driven (`Blockchain:Wormhole:GuardianSets`, per-network). `RequireFullSignatureVerification` default stays `true`; fail-closed-without-verifier path unchanged & still tested. **Pending ops gate (a):** populate + independently verify real mainnet/testnet Guardian sets per **`GUARDIAN-SET-SETUP.md`** (this track) + validate against the live Wormhole Guardian network (config/ops, not a code gap). Base `appsettings.json` ships NO testnet/mainnet set (absent ⇒ fail-closed; placeholder removed — do not re-add). **Code sign-off gate: `scripts/passoff.ps1`** (build 0 errors + full unit suite + safety-critical assertions; prints "OPS SIGN-OFF REQUIRED" for ops gates a–c). |
| **VERIFY** | **MANUAL** | **QA/Ops** | **EF migration baseline: no pre-existing BridgeTransactions table** |
| **FUTURE** | **DEFERRED** | **Wave 4+** | Tri-state provider method (Confirmed/Dropped/Pending) for reconciliation |
| **FUTURE** | **DEFERRED** | **Wave 4+** | Distributed rate-limit store (Redis) for multi-instance scale-out |
| **FUTURE** | **DEFERRED** | **Wave 4+** | Integration test harness refactor (no parallelization, no EnsureDeleted) |
| **FUTURE** | **DEFERRED** | **Wave 4+** | API-key usage metering / billing (persistence + reporting) |

---

### IMPLEMENTED: Wormhole VAA Signature Verification (secp256k1 ecrecover)

**Current state (2026-05-17):** `IVaaSignatureVerifier` is implemented
(`Services/Wormhole/Secp256k1VaaSignatureVerifier.cs`, SEC1 §4.1.6 recovery on
secp256k1 via vetted Bouncy Castle 2.6.2) and registered in `Program.cs`
(`AddScoped<IVaaSignatureVerifier, Secp256k1VaaSignatureVerifier>()`).
`WormholeAdapter.VerifyVAAAsync` now performs genuine per-signature
cryptographic verification against the config-driven Guardian set; the
no-verifier ⇒ fail-closed path is unchanged. The CRYPTO PATH is closed and
proven by unit tests (synthetic Guardian set + real keypair + canonical
double-keccak digest); it is no longer a code blocker.

**Remaining steps are ops/config, not code. The authoritative per-network
procedure + sign-off checklist is `GUARDIAN-SET-SETUP.md` (this track); the
CODE sign-off gate is `scripts/passoff.ps1`.**
1. **Fill real Guardian sets.** Populate `Blockchain:Wormhole:GuardianSets` for
   testnet/mainnet from the official Wormhole Guardian set, following the
   two-source verification procedure in `GUARDIAN-SET-SETUP.md`. The base
   `appsettings.json` ships **NO** testnet/mainnet set (the empty placeholder
   was removed — an absent set fails closed; do not re-add a placeholder or
   guessed addresses). Only the devnet ("tilt") set is populated
   (`appsettings.Development.json`, index `0` =
   `0xbeFA429d57cD18b7F8A4d91A2da9AB4AF05d0FBe`, independently re-derived from
   the documented devnet Guardian private key in a unit test).
2. **Live-network validation.** On devnet/testnet, fetch a real signed VAA from
   the live Guardian network and confirm end-to-end acceptance before enabling
   Wormhole value flow. Unit tests cannot substitute for this (synthetic set).
3. Only after 1–2 are signed off by ops, enable Wormhole mode where real value
   can move.

**Guardian-set config shape:** `Blockchain:Wormhole:GuardianSets` =
`{ "<setIndex>": [ "0x<20-byte-addr>", ... ] }` — the VAA header's
`GuardianSetIndex` selects the list; **list position == Guardian index**.
Network switching follows the existing config pattern: the base
`appsettings.json` holds mainnet/testnet (empty placeholders);
`appsettings.Development.json` overrides with the devnet set.

**For testnet without signature verification:** the escape hatch still exists —
temporarily set `Blockchain:Wormhole:RequireFullSignatureVerification=false`
(NOT for production / never where real value moves).

---

### VERIFY: EF Migration Baseline on Target Environment

**Before deploying to production:**
1. Ensure the target database is created fresh (empty) by your deployment pipeline.
2. OR: if the database is pre-existing, confirm there is NO `BridgeTransactions` table:
   ```sql
   SELECT EXISTS (
     SELECT 1 FROM information_schema.tables
     WHERE table_name = 'BridgeTransactions'
   );
   -- If result is 'true', stop. Drop or migrate the table as documented above.
   ```
3. Then run migrations:
   ```bash
   dotnet ef database update
   # OR within Program.cs, this runs automatically on startup:
   db.Database.Migrate();
   ```
4. Verify the new tables and indexes:
   ```sql
   SELECT table_name FROM information_schema.tables
   WHERE table_name IN ('BridgeTransactions', 'ConsumedVaas', 'IdempotencyRecords');
   
   SELECT indexname FROM pg_indexes
   WHERE tablename IN ('BridgeTransactions', 'ConsumedVaas', 'IdempotencyRecords');
   ```

---

## Summary for Operators

**Safe to launch if:**
- ✓ All unit/integration tests pass (564 tests, zero failures — see Addendum 2 test-count correction)
- ✓ `dotnet build` produces zero errors (17 pre-existing warnings acceptable; the approved Bouncy Castle reference adds none)
- ✓ EF migration baseline verified (no pre-existing `BridgeTransactions` table)
- ✓ VAA signature verification is implemented (DONE — `Secp256k1VaaSignatureVerifier`, registered, crypto path unit-proven) AND the ops/config gate is closed: real testnet/mainnet `Blockchain:Wormhole:GuardianSets` filled from the official Wormhole set + validated against the live Guardian network (or Wormhole mode disabled on devnet/testnet)
- ✓ Reconciliation background sweep is enabled (`Reconciliation:Enabled=true` in appsettings.json)
- ✓ Rate limiting is enabled (`RateLimiting:Enabled=true`)
- ✓ At least one SRE has read this runbook and confirmed understanding of the residual risks

**Operator monitoring during launch:**
- Watch reconciliation logs hourly for "MANUAL INTERVENTION REQUIRED" messages.
- Query hard-stuck bridges every 4 hours; none should exceed the hard-stuck threshold (1 hour) without escalation.
- Test a few bridge transactions end-to-end (initiate, lock/VAA, redeem) before allowing real users.
- Have a runbook printed/accessible for the on-call ops team (this document).

---

---

## Addendum — post code-review closure (2026-05-17)

A multi-agent code review of the full change set found 1 CRITICAL + 4 HIGH + 4
MEDIUM on the safety surface; all were fixed and a closure re-review returned
**APPROVE** (no remaining double-spend / replay / atomicity hole). Net changes
to the residual picture above:

- **VAA replay digest divergence (was CRITICAL, NOW CLOSED):** the bridge had a
  private `ComputeVaaDigest` hashing the base64 *string* while the canonical
  `WormholeAdapter.ComputeVaaDigest` hashes the *decoded bytes* — divergent
  ledger keys. Consolidated to the single canonical implementation; malformed/
  empty `VaaBytes` now deterministically rejected before any claim/transition/
  on-chain call. Cross-component agreement + re-encoded-base64 collision tests
  added.
- **Orphaned `InProgress` idempotency key (was a "residual" above, NOW
  MITIGATED):** `ReconciliationService` now settles a still-`InProgress`
  `IdempotencyRecord` to the true terminal state once the owning bridge/op is
  chain-confirmed terminal (GetAsync-first, idempotent, never fabricates a key).
  The "stuck forever after crash" item in §2 is now self-healing for the common
  case; manual settlement remains the fallback only when the key is
  unresolvable (logged as MANUAL INTERVENTION).
- **Idempotency settled against unverified bridge state (was HIGH, NOW
  CLOSED):** `CompleteAsync`/`FailAsync` are gated on the conditional-update
  affected-row count; 0-row paths re-read and settle to the true bridge state,
  logging MANUAL INTERVENTION on inconsistency.
- **IdempotencyStore robustness (was HIGH, NOW CLOSED):** claim now runs on a
  fresh per-call DI scope (never batched with caller-tracked entities); unique
  violations positively typed (Npgsql 23505 / SQLite 2067·1555); genuine
  non-unique DB errors rethrown, not masked as duplicates.
- **Address validators (was HIGH, NOW CLOSED):** relaxed from alnum-only to a
  permissive base58/base32/hex/bech32 charset + length bounds; amount/Guid/enum
  rules unchanged.
- **OperationIdGenerator (was MEDIUM, NOW CLOSED):** components escaped before
  join (no separator collision); full 256-bit digest.

**Fast-follow (LOW):** none outstanding — the two LOW items from the closure
review (stamp reverse-path `IdempotencyKey` on the row; bridge-level
malformed-VAA reject test) were also fixed. The pre-launch gating items in §4
(implement+register `IVaaSignatureVerifier`; resolve the migration baseline on
the target env; distributed rate-limit store; integration-harness refactor;
reconciliation tri-state provider method) remain as stated.

Verification at closure: production API builds 0 errors; **537/537 unit tests
green** incl. all exactly-once / replay / idempotency-primitive / reconciliation
safety tests.

---

---

## Addendum 2 — pre-launch / greenfield posture (2026-05-17)

**This system is not live: no customers, no production data.** Architecture may
change freely (no migration / dual-write / rollback / backward-compat
constraints). This supersedes the conservative deployment cautions in the body
above. Re-classification of §2 / §4:

**Collapse to NON-ISSUES pre-launch (do not treat as blockers):**
- **§2 / §4 EF migration baseline (`BridgeTransactions` pre-existence).** Moot —
  there is no data to preserve. Just reset: drop the dev DB (or recreate the
  `oasis` podman container) and `Database.Migrate()` from empty. Do **not** add
  `CREATE TABLE IF NOT EXISTS` / split-migration compat shims. (Note: there is
  now an additional stacked migration `20260518003457_AddSagaOutbox` — same
  reset-from-empty answer.)
- **§2 swap-execute idempotency-key unused** — already a non-issue by design.
- **§2 "DO-NOT drop the table / data-loss" cautions in §3** — apply only once
  real value flows; pre-launch the DB is disposable.

**Deferred + correctly tracked (not "resolved", but owned elsewhere):**
- **§2 integration-test harness** → owned by `surrealdb-migration` (rebuild
  against SurrealDB, not patched for Postgres — Postgres is being deleted).
- **Async retry / compensation / orphan-recovery as first-class** →
  `durable-saga-orchestration` (Phase-1 skeleton built; bridge adoption is
  Phase 2 — the bridge still runs the synchronous path today).

**Genuinely OPEN — real correctness/security, independent of live status:**
1. **`IVaaSignatureVerifier` (secp256k1 ecrecover) — IMPLEMENTED (2026-05-17);
   now an OPS/CONFIG gate, no longer a code blocker.** Implemented
   (`Services/Wormhole/Secp256k1VaaSignatureVerifier.cs`, SEC1 §4.1.6 recovery
   on secp256k1 via vetted Bouncy Castle 2.6.2, reusing the existing managed
   `Keccak256`) and registered in `Program.cs`. The crypto path is proven by
   17 self-contained unit tests (real keypair, synthetic config-driven Guardian
   set, canonical `keccak256(keccak256(body))` digest, tamper / wrong-recovery /
   malformed-r·s / not-in-set / below-quorum / determinism, plus an independent
   re-derivation of the devnet Guardian address from the documented devnet
   private key). **What remains is ops/config, not a code gap:** (a) fill the
   real testnet/mainnet `Blockchain:Wormhole:GuardianSets` (base
   `appsettings.json` ships NONE ⇒ fail-closed; the empty placeholder was
   removed) from the official Wormhole Guardian set, following the two-source
   verification procedure + sign-off checklist in **`GUARDIAN-SET-SETUP.md`**
   (this track); (b) validate end-to-end against the live Wormhole Guardian
   network on devnet/testnet before enabling Wormhole value flow. Until (a)+(b)
   are signed off, Wormhole value flow stays gated — but by configuration/ops,
   not by missing code. The CODE sign-off gate is `scripts/passoff.ps1`
   (build + full unit suite + safety-critical assertions; 564/564 green).
2. **Reconciliation tri-state provider gap** (dropped vs pending) — real
   correctness limitation; operator-manual until a tri-state provider method
   exists.
3. **Distributed rate-limit store** — only matters at multi-instance scale-out;
   single-instance is fine.

**Test-count correction (supersedes all body figures):** the body says `493`,
Addendum 1 says `537`; the prior authoritative figure was `547`. **As of
2026-05-17 the current authoritative figure is 564/564 unit tests green**
(547 + 17 new `Secp256k1VaaSignatureVerifierTests` covering the ecrecover
crypto path + adapter integration), production build 0 errors / 17 warnings
(unchanged 17-baseline; the approved Bouncy Castle reference adds no new
warnings). Treat `493`, `537`, and `547` in the text above as stale.

The closure-review defects (Addendum 1) remain resolved. The runbook's purpose
is to *carry* the genuinely-open items (1–3 above) forward — they are tracked,
not closed.

---

**Last updated:** 2026-05-18 (api-safety-hardening — guardian-set config
cleaned: empty testnet/mainnet placeholder REMOVED from `appsettings.json`
(absent ⇒ fail-closed; verified devnet set in `appsettings.Development.json`
intact). Added `GUARDIAN-SET-SETUP.md` (ops procedure + sign-off checklist for
gate a) and `scripts/passoff.ps1` (CODE sign-off gate). `IVaaSignatureVerifier`
secp256k1 ecrecover IMPLEMENTED + registered, crypto path unit-proven;
remaining real open items: live-Guardian-network validation + real
testnet/mainnet Guardian-set config [ops/config gate, not code],
reconciliation tri-state, distributed rate-limit. 564/564 unit green via
`scripts/passoff.ps1`; production build 0 errors / 17-baseline warnings.)
