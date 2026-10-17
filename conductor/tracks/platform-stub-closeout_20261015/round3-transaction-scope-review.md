---
type: review-checklist
status: active
owner: root
---

# Transaction-scope review checklist (owner concern, 2026-10-16)

Owner concern: many new mutations, especially in the DEX work. This checklist is a mandatory
dimension of every round-3 independent review and of the round-4 assessments.

## The concurrency model the stores rely on

SurrealDB 3.x on RocksDB runs optimistic transactions. Concurrent writes to the same record
conflict (retryable `Transaction conflict`, see `Core/Surreal/AGENTS.md` §transient-conflict).
Plain reads are not conflict-checked, so an invariant that depends on a row the transaction
only *reads* can be broken by a concurrent writer (write skew). The house rules:

1. **Read, decide, re-assert.** Every fact read before the batch and used in a decision is
   pinned inside one `BEGIN … COMMIT` by a version/state predicate. A failed predicate
   `THROW`s, which rolls back the whole batch.
2. **Promote dependent reads to writes.** A row the invariant depends on but does not change
   gets a no-op conditional write (`UPDATE … SET updated_at = updated_at WHERE <predicate>`),
   so a concurrent writer to that row conflicts instead of slipping past.
3. **Deterministic IDs are locks.** Where exclusivity is per resource, the record ID derives
   from the resource identity, so concurrent creates collide on the key.
4. **Retry only the engine conflict; resolve losers by re-reading the winner.** The retried
   batch must fail its own predicates cleanly once the winner has committed.
5. **Chain effects are outside every DB transaction.** A broadcast cannot roll back. Persist
   the unsigned plan before custody, signed bytes before first send, exposure before
   disclosure, and settle only from finalized chain evidence.

## Verified example: Ethereum DEX allowance claim

`Providers/Stores/Surreal/SurrealEthereumDexAllowanceStore.cs` applies rules 1–4. Its
`ClaimAsync` reads `swap_stage` egress (`authorized_at`/`external_exposed_at`) with a plain
`SELECT` inside the batch (line 57). That is safe only because:

- the batch no-op-locks the previous holder's `swap_operation` row with `state = 'Cancelled'`
  (lines 52–55), and
- the only two egress writes, `SurrealSwapOperationStore.Exposure.cs:73` and
  `SurrealSwapOperationStore.Stages.cs:134`, both commit through `CommitStageAsync`, which
  compare-and-swaps that same `swap_operation` row from a non-cancelled state.

So the operation row is the serialization point. *Correction (lane C review, 2026-10-16):* a
cancelled operation **can** later gain egress — `BeginCleanupAsync` (`SurrealSwapOperationStore.Cleanup.cs:41`)
moves `Cancelled` → `CleanupReady` → `Preparing` → an authorized revoke. It stays safe because
that transition also writes the operation row through `CommitStageAsync`, conflicting with the
claim's no-op lock, and the holder check (`EthereumProvider.DexCleanup.cs:39`) runs after the
operation has left `Cancelled`. The real invariant is: **every transition out of `Cancelled`
and every egress write updates the operation row.** **This dependency is implicit**: a future
egress write or `Cancelled` exit that bypasses the operation row would silently reopen a
write-skew hole. Required: document it in
`Providers/Stores/Surreal/AGENTS.md`, and gate it mechanically (below).

## Reviewer checklist (per changed store / fence)

- [ ] Every multi-record invariant commits in one `BEGIN … COMMIT` with `THROW` guards.
- [ ] Every pre-read fact used in a decision or a `SET` value is pinned in-batch (version or exact value).
- [ ] Every plain `SELECT` inside a batch names its serialization row (a no-op lock or a terminal-state
      argument). If neither applies, it is a defect.
- [ ] Retry wrappers sit only around genuinely contended batches, and the loser path re-reads the winner.
- [ ] No DB transaction spans a chain call; each chain step has a durable checkpoint before and after.
- [ ] Captured results use named transaction variables, not positional result offsets where avoidable
      (`GetValues(guards.Count + 5)` in `CommitStageAsync` breaks silently if a statement is added).

## Round-4 work this produces

- **Mechanical gate** (architecture test over store sources): every `"BEGIN"` batch ends in
  `"COMMIT"` with a `THROW` guard; every `Set(… AuthorizedAt|ExternalExposedAt …)` lives in a
  method routed through `CommitStageAsync`; every in-batch `SELECT` carries a
  `// lock: <row>` annotation.
- **Interleaving flow tests** against real SurrealDB, through the public store seam:
  allowance claim vs. old-operation authorize/cancel; exposure vs. withdrawal; Hold source
  consumption vs. revocation. Assert exactly one winner and the resulting rows.
- **SurrealForge candidate:** a typed transaction builder (guards, captured mutations, typed
  conflict codes, named results) to retire hand-composed `BEGIN`/`COMMIT` strings and result
  offsets. Three separate hand-built batch shapes exist in the DEX stores alone.

## Known compile defects for the integrated sweep

- `sdk/azoa-wallet/src/api/client.ts:2278` uses `FiatPayoutView`, and `src/api/index.ts:16` /
  `src/index.ts:96` re-export it, but no SDK file declares it. Present in checkpoint `3d08ace`.
- Frontend imports the **built** SDK (`azoa-sdk`): rebuild `sdk/azoa-wallet` (`npm run build`)
  before the frontend typecheck, or new exports such as `NftLifecycleView` appear missing.
