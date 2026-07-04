using Microsoft.Extensions.Options;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Bridge;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Services.Reconciliation;

/// <summary>
/// Re-derives true bridge/operation status from on-chain confirmations.
///
/// <para><b>Chain-truth source.</b> The only confirmation-lookup capability on
/// <see cref="IBlockchainProvider"/> is
/// <c>GetTransactionStatusAsync(txHash, ct)</c> →
/// <c>AZOAResult&lt;Dictionary&lt;string,object&gt;&gt;</c>. The dictionary
/// shape is provider-inconsistent (Algorand emits <c>confirmed</c>/
/// <c>confirmedRound</c>; Solana emits <c>success</c>). There is NO provider
/// method that distinguishes "definitively dropped/failed" from "not yet
/// found": a not-found tx surfaces as <c>AZOAResult.IsError == true</c> on
/// both providers, which is ambiguous (mempool vs. dropped vs. never
/// broadcast). This is the documented residual gap — see
/// <see cref="ClassifyTx"/>. We therefore only ever:
/// <list type="bullet">
/// <item>ADVANCE on a positive confirmation signal,</item>
/// <item>FAIL only on an explicit on-chain negative signal,</item>
/// <item>otherwise LEAVE the record untouched and (past the hard-stuck
/// threshold) flag it "MANUAL INTERVENTION REQUIRED" — never guess.</item>
/// </list>
/// </para>
///
/// Scoped lifetime. The hosted sweep resolves this inside a per-tick DI scope.
/// </summary>
public sealed class ReconciliationService : IReconciliationService
{
    private readonly IBridgeStore _bridgeStore;
    private readonly IBlockchainProviderFactory _chainFactory;
    private readonly IIdempotencyStore _idempotency;
    private readonly ILogger<ReconciliationService> _logger;
    private readonly ReconciliationOptions _options;

    private static readonly BridgeStatus[] NonTerminalBridge =
    {
        BridgeStatus.Initiated,
        BridgeStatus.Locked,
        BridgeStatus.AwaitingVAA,
        BridgeStatus.VAAReady,
        BridgeStatus.Redeeming,
        // Reversal-in-flight: still swept so a stuck reversal is flagged for
        // MANUAL INTERVENTION (never auto-advanced/auto-reversed — see below).
        BridgeStatus.Reversing,
    };

    private static readonly string[] NonTerminalOperation =
    {
        OperationStatus.Pending,
        OperationStatus.AwaitingSignature,
    };

    public ReconciliationService(
        IBridgeStore bridgeStore,
        IBlockchainProviderFactory chainFactory,
        IIdempotencyStore idempotency,
        ILogger<ReconciliationService> logger,
        IOptions<ReconciliationOptions> options)
    {
        _bridgeStore = bridgeStore;
        _chainFactory = chainFactory;
        _idempotency = idempotency;
        _logger = logger;
        _options = options.Value;
    }

    // ───────────────────────── Bridge reconciliation ─────────────────────────

    public async Task<ReconciliationReport> ReconcileBridgeAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var staleBefore = now.AddSeconds(-Math.Max(0, _options.BridgeStaleAfterSeconds));
        var batch = Math.Clamp(_options.BatchSize, 1, 1000);

        // Snapshot candidate ids only — every write below is a standalone
        // conditional UPDATE, so we never mutate tracked entities.
        var candidates = await _bridgeStore.GetNonTerminalBridgeIdsAsync(
            NonTerminalBridge, staleBefore, batch, ct);

        var report = ReconciliationReport.Empty with { Scanned = candidates.Count };

        foreach (var id in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Per-record reports carry Scanned == 0, so Combine preserves
                // the batch scan count set above.
                report = report.Combine(await ReconcileOneBridgeAsync(id, now, ct));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Reconciliation: unexpected error reconciling bridge tx {BridgeId}", id);
                report = report with { Errors = report.Errors + 1 };
            }
        }

        // G6: surface Failed bridges that hold locked funds with no mint — terminal status
        // makes them invisible to the non-terminal sweep above; alert so operators can act.
        report = report.Combine(await CheckLockedFundsAtRiskAsync(ct));

        return report;
    }

    public async Task<ReconciliationReport> ReconcileBridgeTransactionAsync(
        string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Bridge transaction id is required", nameof(id));

        var exists = await _bridgeStore.ExistsByIdAsync(id, ct);

        if (!exists)
        {
            _logger.LogWarning(
                "Reconciliation: bridge tx {BridgeId} not found (manual trigger)", id);
            return ReconciliationReport.Empty;
        }

        var rep = await ReconcileOneBridgeAsync(id, DateTime.UtcNow, ct);
        return rep with { Scanned = 1 };
    }

    /// <summary>
    /// Reconcile exactly one bridge transaction. Re-reads (no-tracking) inside
    /// so a manual trigger or a long sweep always sees current status. All
    /// writes are conditional updates with the expected current status in the
    /// predicate; exactly-one-row is asserted.
    /// </summary>
    private async Task<ReconciliationReport> ReconcileOneBridgeAsync(
        string id, DateTime now, CancellationToken ct)
    {
        var tx = await _bridgeStore.GetBridgeAsync(id, ct);

        if (tx is null) return ReconciliationReport.Empty; // vanished — nothing to do

        if (!NonTerminalBridge.Contains(tx.Status))
        {
            // Task 6: row already reached a TERMINAL state (raced with foreground). If an
            // IdempotencyKey is stamped, settle it now so a crash between the terminal-transition
            // and the idempotency-complete call (audit Window 6) doesn't leak InProgress forever.
            var isSuccessTerminal = tx.Status == BridgeStatus.Completed || tx.Status == BridgeStatus.Refunded;
            if (!string.IsNullOrWhiteSpace(tx.IdempotencyKey))
                await SettleBridgeIdempotencyAsync(
                    tx, settleCompleted: isSuccessTerminal,
                    payloadOrReason: $"sweep: row already terminal ({tx.Status}) — settling orphaned idempotency record",
                    ct);
            return ReconciliationReport.Empty;
        }

        var ageSeconds = (now - tx.CreatedAt).TotalSeconds;
        var hardStuck = ageSeconds >= Math.Max(0, _options.BridgeHardStuckAfterSeconds);

        // Pick the on-chain tx hash that, if confirmed, proves the *current*
        // phase advanced. Orphan-lock recovery (defense-in-depth half of plan
        // task 6): Initiated/Locked/AwaitingVAA with a LockTxHash → verify the
        // lock landed; we advance the lifecycle flag or flag for manual review,
        // but NEVER auto-reverse funds here.
        // MEDIUM-3 guard (now EXPLICIT provenance): a Reversing row is a
        // reversal in flight — CrossChainBridgeService.ReverseBridgeAsync moves
        // Completed→Reversing, then terminal Refunded/Failed. It is NOT a redeem
        // awaiting confirmation; advancing it would mask an in-flight
        // refund/burn. Reconciliation MUST NOT auto-advance, auto-reverse, or
        // otherwise mutate it (same conservative rule as the rest of the
        // service) — the operator resolves the reversal outcome. The state is
        // now an explicit BridgeStatus, so there is no CompletedAt/IdempotencyKey
        // inference: a Reversing row IS reversal-in-flight, by construction.
        if (tx.Status == BridgeStatus.Reversing)
        {
            _logger.LogError(
                "Reconciliation: MANUAL INTERVENTION REQUIRED — bridge tx {BridgeId} is " +
                "in the explicit Reversing state (reversal-in-flight: IdempotencyKey={IdemKey}). " +
                "This is an in-flight refund/burn, NOT a redeem awaiting confirmation. " +
                "Reconciliation will NOT advance it (that would mask the reversal) and " +
                "will NOT mutate it — operator must resolve the reversal outcome.",
                tx.Id, tx.IdempotencyKey);
            return ReconciliationReport.Empty with { StuckFlagged = 1 };
        }

        var (txHash, chainName, advanceTo, phase) = SelectBridgeProbe(tx);

        if (string.IsNullOrWhiteSpace(txHash))
        {
            // No on-chain handle for the current phase (e.g. Initiated before a
            // lock hash exists, or AwaitingVAA where progress is off-chain).
            // Cannot derive truth — flag only if hard-stuck.
            return MaybeFlagStuckBridge(tx, ageSeconds, hardStuck,
                $"no on-chain tx hash for phase {phase}");
        }

        var provider = TryResolveProvider(chainName, tx.Network);
        if (provider is null)
        {
            _logger.LogWarning(
                "Reconciliation: no provider for chain '{Chain}' (bridge tx {BridgeId}, phase {Phase})",
                chainName, tx.Id, phase);
            return MaybeFlagStuckBridge(tx, ageSeconds, hardStuck,
                $"no blockchain provider for '{chainName}'");
        }

        var verdict = await ProbeChainAsync(provider, txHash!, ct);

        switch (verdict)
        {
            case ChainVerdict.Confirmed:
            {
                // G1 fix: Locked row with a confirmed lock tx cannot self-transition
                // Locked→Locked. The lock landed but the downstream mint/VAA step
                // never ran — foreground or operator must re-drive; sweep observes only.
                if (tx.Status == BridgeStatus.Locked && advanceTo == BridgeStatus.Locked)
                {
                    _logger.LogWarning(
                        "Reconciliation: lock confirmed on-chain for bridge tx {BridgeId} " +
                        "(LockTx={LockTx}, chain={Chain}) but downstream mint/VAA step never ran " +
                        "— awaiting operator/foreground re-drive. Will not self-transition Locked→Locked.",
                        tx.Id, txHash, chainName);
                    return ReconciliationReport.Empty with { StuckFlagged = 1 };
                }

                // Atomic, conditional advance. WHERE Id == x AND Status ==
                // <expected current>. If a concurrent live request already
                // moved it, affected == 0 and we simply no-op (idempotent).
                var expected = tx.Status;
                var alsoSet = advanceTo == BridgeStatus.Completed
                    ? new BridgeStatusMutation { SetCompletedAtUtcNow = true }
                    : new BridgeStatusMutation { ClearCompletedAt = true };

                int affected = await _bridgeStore.TryTransitionBridgeStatusAsync(
                    tx.Id, expected, advanceTo, alsoSet, ct);

                if (affected == 1)
                {
                    _logger.LogInformation(
                        "Reconciliation: bridge tx {BridgeId} {From} -> {To} " +
                        "(chain-confirmed {Phase} tx {TxHash} on {Chain})",
                        tx.Id, expected, advanceTo, phase, txHash, chainName);

                    // HIGH-3: only a chain-confirmed TERMINAL state (Completed)
                    // may settle the owning idempotency record. Advancing to a
                    // non-terminal phase (e.g. Initiated→Locked) leaves the
                    // op in flight — the record must stay InProgress.
                    if (advanceTo == BridgeStatus.Completed)
                        await SettleBridgeIdempotencyAsync(
                            tx, settleCompleted: true,
                            payloadOrReason: txHash!, ct);

                    return ReconciliationReport.Empty with { Advanced = 1 };
                }

                _logger.LogInformation(
                    "Reconciliation: bridge tx {BridgeId} not in expected status {Expected} " +
                    "(concurrently advanced) — no-op", tx.Id, expected);
                return ReconciliationReport.Empty;
            }

            case ChainVerdict.FailedOnChain:
            {
                var expected = tx.Status;
                var msg = $"Reconciliation: {phase} tx {txHash} reported FAILED on-chain ({chainName}).";
                int affected = await _bridgeStore.TryTransitionBridgeStatusAsync(
                    tx.Id, expected, BridgeStatus.Failed,
                    new BridgeStatusMutation
                    {
                        ErrorMessage = msg,
                        SetCompletedAtUtcNow = true,
                    }, ct);

                if (affected == 1)
                {
                    _logger.LogWarning(
                        "Reconciliation: bridge tx {BridgeId} {From} -> Failed ({Reason})",
                        tx.Id, expected, msg);

                    // HIGH-3: chain-confirmed terminal failure settles the
                    // owning idempotency record as Failed so the poisoned
                    // InProgress key is released.
                    await SettleBridgeIdempotencyAsync(
                        tx, settleCompleted: false, payloadOrReason: msg, ct);

                    return ReconciliationReport.Empty with { Failed = 1 };
                }

                _logger.LogInformation(
                    "Reconciliation: bridge tx {BridgeId} not in expected status {Expected} " +
                    "when marking Failed (concurrently advanced) — no-op", tx.Id, expected);
                return ReconciliationReport.Empty;
            }

            default: // ChainVerdict.Unknown — never guess
                return MaybeFlagStuckBridge(tx, ageSeconds, hardStuck,
                    $"chain status for {phase} tx {txHash} on {chainName} is indeterminate");
        }
    }

    /// <summary>
    /// Decide which on-chain tx hash proves the current phase, and what status
    /// a positive confirmation advances to. Never auto-reverses funds.
    /// </summary>
    private static (string? txHash, string chain, BridgeStatus advanceTo, string phase)
        SelectBridgeProbe(BridgeTransactionResult tx) => tx.Status switch
    {
        // Outbound lock landed → lifecycle can move to Locked. Orphan-lock
        // recovery: a stale Initiated/Locked/AwaitingVAA carrying a LockTxHash
        // is verified here; we only advance the flag or (if hard-stuck and
        // indeterminate) flag for manual reversal — never reverse on-chain.
        BridgeStatus.Initiated or BridgeStatus.Locked or BridgeStatus.AwaitingVAA
            => (tx.LockTxHash, tx.SourceChain, BridgeStatus.Locked, "lock"),

        // Redeem/mint on the target chain confirmed → Completed.
        BridgeStatus.Redeeming
            => (tx.RedemptionTxHash ?? tx.MintTxHash, tx.TargetChain,
               BridgeStatus.Completed, "redeem"),

        // VAAReady: a mint/redeem hash may already exist if a crash happened
        // after broadcast but before the Completed write.
        BridgeStatus.VAAReady
            => (tx.RedemptionTxHash ?? tx.MintTxHash, tx.TargetChain,
               BridgeStatus.Completed, "redeem"),

        // Terminal (Completed/Failed/Refunded) or Reversing. Reversing is
        // intercepted by the explicit early return in ReconcileOneBridgeAsync
        // and never reaches here; this default is the defensive backstop —
        // null txHash ⇒ flag-only via MaybeFlagStuckBridge, never a mutation
        // or a mis-mapped advance.
        _ => (null, tx.TargetChain, tx.Status, "unknown"),
    };

    private ReconciliationReport MaybeFlagStuckBridge(
        BridgeTransactionResult tx, double ageSeconds, bool hardStuck, string reason)
    {
        if (hardStuck)
        {
            _logger.LogError(
                "Reconciliation: MANUAL INTERVENTION REQUIRED — bridge tx {BridgeId} " +
                "stuck in {Status} for {AgeSeconds:F0}s ({Reason}). " +
                "Source={SourceChain} Target={TargetChain} LockTx={LockTx} " +
                "MintTx={MintTx} RedeemTx={RedeemTx}. " +
                "Reconciliation will NOT auto-fail or auto-reverse — operator must resolve.",
                tx.Id, tx.Status, ageSeconds, reason,
                tx.SourceChain, tx.TargetChain, tx.LockTxHash, tx.MintTxHash,
                tx.RedemptionTxHash);
            return ReconciliationReport.Empty with { StuckFlagged = 1 };
        }

        _logger.LogDebug(
            "Reconciliation: bridge tx {BridgeId} in {Status} for {AgeSeconds:F0}s, " +
            "not yet hard-stuck ({Reason}) — leaving untouched",
            tx.Id, tx.Status, ageSeconds, reason);
        return ReconciliationReport.Empty;
    }

    /// <summary>
    /// G6: count Failed bridges with locked funds and no mint; log a warning per sweep tick
    /// if any exist so operators can act. The sweep never mutates these rows.
    /// </summary>
    private async Task<ReconciliationReport> CheckLockedFundsAtRiskAsync(CancellationToken ct)
    {
        try
        {
            var batchSize = Math.Clamp(_options.BatchSize, 1, 1000);
            var ids = await _bridgeStore.GetFailedBridgesWithLockedFundsAsync(batchSize, ct);
            if (ids.Count == 0) return ReconciliationReport.Empty;

            var sample = string.Join(", ", ids.Take(5));
            _logger.LogWarning(
                "Reconciliation: {Count} failed bridge(s) hold locked funds with no mint — " +
                "manual intervention required to recover source-chain funds. " +
                "Sample ids: [{Sample}]",
                ids.Count, sample);

            return ReconciliationReport.Empty with { LockedFundsAtRisk = ids.Count };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reconciliation: failed to check locked-funds-at-risk");
            return ReconciliationReport.Empty;
        }
    }

    // ─────────────────────── Operation reconciliation ────────────────────────

    public async Task<ReconciliationReport> ReconcileOperationsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var staleBefore = now.AddSeconds(-Math.Max(0, _options.OperationStaleAfterSeconds));
        var batch = Math.Clamp(_options.BatchSize, 1, 1000);

        var candidateIds = await _bridgeStore.GetNonTerminalOperationIdsAsync(
            NonTerminalOperation, staleBefore, batch, ct);

        var report = ReconciliationReport.Empty with { Scanned = candidateIds.Count };

        foreach (var id in candidateIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Per-record reports carry Scanned == 0, so Combine preserves
                // the batch scan count set above.
                report = report.Combine(await ReconcileOneOperationAsync(id, now, ct));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Reconciliation: unexpected error reconciling operation {OperationId}", id);
                report = report with { Errors = report.Errors + 1 };
            }
        }

        return report;
    }

    private async Task<ReconciliationReport> ReconcileOneOperationAsync(
        Guid id, DateTime now, CancellationToken ct)
    {
        var op = await _bridgeStore.GetOperationAsync(id, ct);

        if (op is null || !NonTerminalOperation.Contains(op.Status))
            return ReconciliationReport.Empty;

        var ageSeconds = (now - op.CreatedDate).TotalSeconds;
        var hardStuck = ageSeconds >= Math.Max(0, _options.OperationHardStuckAfterSeconds);

        // Same provider-resolution convention as BlockchainOperationManager:
        // Parameters["ChainType"] (else default provider), Parameters["ChainNetwork"].
        op.Parameters.TryGetValue("TxHash", out var txHash);

        if (string.IsNullOrWhiteSpace(txHash))
        {
            // No broadcast tx hash recorded → nothing was put on-chain (or the
            // broadcast never reached the persistence point). Cannot derive
            // truth; NEVER re-broadcast. Flag only if hard-stuck.
            return MaybeFlagStuckOperation(op, ageSeconds, hardStuck,
                "no TxHash in operation Parameters (nothing observable on-chain)");
        }

        var chainType = op.Parameters.TryGetValue("ChainType", out var ct0) && !string.IsNullOrWhiteSpace(ct0)
            ? ct0
            : null;

        var provider = chainType is not null
            ? TryResolveProvider(chainType)
            : SafeDefaultProvider();

        if (provider is null)
        {
            _logger.LogWarning(
                "Reconciliation: no provider for operation {OperationId} (chain '{Chain}')",
                op.Id, chainType ?? "<default>");
            return MaybeFlagStuckOperation(op, ageSeconds, hardStuck,
                "no blockchain provider resolvable");
        }

        var verdict = await ProbeChainAsync(provider, txHash!, ct);

        switch (verdict)
        {
            case ChainVerdict.Confirmed:
            {
                var expected = op.Status;
                int affected = await _bridgeStore.TryTransitionOperationStatusAsync(
                    op.Id, expected, OperationStatus.Completed, DateTime.UtcNow, ct);

                if (affected == 1)
                {
                    _logger.LogInformation(
                        "Reconciliation: operation {OperationId} {From} -> Completed " +
                        "(chain-confirmed tx {TxHash})", op.Id, expected, txHash);

                    // HIGH-3: chain-confirmed terminal success settles the
                    // owning idempotency record (Completed).
                    await SettleOperationIdempotencyAsync(
                        op, settleCompleted: true, payloadOrReason: txHash!, ct);

                    return ReconciliationReport.Empty with { Advanced = 1 };
                }

                _logger.LogInformation(
                    "Reconciliation: operation {OperationId} not in expected status {Expected} " +
                    "(concurrently advanced) — no-op", op.Id, expected);
                return ReconciliationReport.Empty;
            }

            case ChainVerdict.FailedOnChain:
            {
                var expected = op.Status;
                int affected = await _bridgeStore.TryTransitionOperationStatusAsync(
                    op.Id, expected, OperationStatus.Failed, DateTime.UtcNow, ct);

                if (affected == 1)
                {
                    _logger.LogWarning(
                        "Reconciliation: operation {OperationId} {From} -> Failed " +
                        "(tx {TxHash} reported failed on-chain)", op.Id, expected, txHash);

                    // HIGH-3: chain-confirmed terminal failure settles the
                    // owning idempotency record (Failed).
                    await SettleOperationIdempotencyAsync(
                        op, settleCompleted: false,
                        payloadOrReason:
                            $"Reconciliation: operation tx {txHash} reported FAILED on-chain.",
                        ct);

                    return ReconciliationReport.Empty with { Failed = 1 };
                }

                _logger.LogInformation(
                    "Reconciliation: operation {OperationId} not in expected status {Expected} " +
                    "when marking Failed (concurrently advanced) — no-op", op.Id, expected);
                return ReconciliationReport.Empty;
            }

            default:
                return MaybeFlagStuckOperation(op, ageSeconds, hardStuck,
                    $"chain status for tx {txHash} is indeterminate");
        }
    }

    private ReconciliationReport MaybeFlagStuckOperation(
        BlockchainOperation op, double ageSeconds, bool hardStuck, string reason)
    {
        if (hardStuck)
        {
            _logger.LogError(
                "Reconciliation: MANUAL INTERVENTION REQUIRED — operation {OperationId} " +
                "({OpType}) stuck in {Status} for {AgeSeconds:F0}s ({Reason}). " +
                "Reconciliation observes only; it will NOT re-broadcast — operator must resolve.",
                op.Id, op.OperationType, op.Status, ageSeconds, reason);
            return ReconciliationReport.Empty with { StuckFlagged = 1 };
        }

        _logger.LogDebug(
            "Reconciliation: operation {OperationId} in {Status} for {AgeSeconds:F0}s, " +
            "not yet hard-stuck ({Reason}) — leaving untouched",
            op.Id, op.Status, ageSeconds, reason);
        return ReconciliationReport.Empty;
    }

    // ──────────────────── HIGH-3: idempotency settlement ─────────────────────
    //
    // When a process dies AFTER the on-chain effect but BEFORE the owning
    // service called CompleteAsync/FailAsync, the IdempotencyRecord stays
    // InProgress forever. ReplayFromRecord then returns "still in progress" for
    // every duplicate, permanently poisoning the key. Once reconciliation has
    // proven (from chain truth) that the owning bridge/op reached a
    // chain-CONFIRMED terminal state, it is now safe to settle that record.
    //
    // Conservative rules (mirror the rest of the service):
    //   • Only ever called from a branch that already advanced/failed the
    //     owning entity to a chain-confirmed TERMINAL state — never on
    //     ambiguous chain truth.
    //   • Resolve the key WITHOUT guessing. Bridge rows carry the key in their
    //     IdempotencyKey column (stamped by CrossChainBridgeService). Operation
    //     rows do NOT persist their derived key; the manager derives it from
    //     content via OperationIdGenerator and does not store it, so it is NOT
    //     reconstructable here without guessing — when unresolvable, log a
    //     flagged manual-intervention item and never fabricate a key.
    //   • Idempotent: GetAsync first; skip if the record is already terminal or
    //     absent; tolerate the InvalidOperationException contract of the store.
    //
    // No ReconciliationReport field is added (the report record lives in
    // IReconciliationService.cs, outside this change's ownership): settlements
    // fold into the existing Advanced/Failed counters and are explicitly logged.

    private async Task SettleBridgeIdempotencyAsync(
        BridgeTransactionResult tx, bool settleCompleted, string payloadOrReason,
        CancellationToken ct)
    {
        var key = tx.IdempotencyKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            // No key persisted on the row (legacy/back-compat row). Cannot
            // settle without guessing — flag for manual intervention only.
            _logger.LogWarning(
                "Reconciliation: bridge tx {BridgeId} reached a chain-confirmed terminal " +
                "state but carries NO IdempotencyKey — cannot settle the idempotency " +
                "record without guessing. MANUAL INTERVENTION: verify no orphaned " +
                "InProgress idempotency record poisons retries for this bridge.",
                tx.Id);
            return;
        }

        await SettleIdempotencyRecordAsync(
            key!, settleCompleted, payloadOrReason,
            ownerDescription: $"bridge tx {tx.Id}", ct);
    }

    private async Task SettleOperationIdempotencyAsync(
        BlockchainOperation op, bool settleCompleted, string payloadOrReason,
        CancellationToken ct)
    {
        // The operation row does NOT persist its idempotency key
        // (BlockchainOperationManager.DeriveIdempotencyKey produces it from
        // content via OperationIdGenerator and never stores it). The only
        // non-guessing source is an explicit key on the operation Parameters
        // (e.g. a caller-supplied "IdempotencyKey"). If absent, do NOT
        // reconstruct it — flag for manual intervention.
        if (!op.Parameters.TryGetValue("IdempotencyKey", out var key)
            || string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning(
                "Reconciliation: operation {OperationId} ({OpType}) reached a " +
                "chain-confirmed terminal state but its idempotency key is not " +
                "persisted/resolvable (the manager derives it from content and does " +
                "not store it). NOT fabricating a key. MANUAL INTERVENTION: verify no " +
                "orphaned InProgress idempotency record poisons retries for this op.",
                op.Id, op.OperationType);
            return;
        }

        await SettleIdempotencyRecordAsync(
            key, settleCompleted, payloadOrReason,
            ownerDescription: $"operation {op.Id}", ct);
    }

    /// <summary>
    /// Settle one resolved idempotency key, idempotently. GetAsync first; skip
    /// when the record is absent or already terminal. The store's
    /// Complete/FailAsync throw <see cref="InvalidOperationException"/> if the
    /// key vanished between the check and the write (no claim) — tolerated and
    /// downgraded to a log so a reconciliation pass never errors on a benign
    /// race. Never throws out of reconciliation.
    /// </summary>
    private async Task SettleIdempotencyRecordAsync(
        string key, bool settleCompleted, string payloadOrReason,
        string ownerDescription, CancellationToken ct)
    {
        try
        {
            var record = await _idempotency.GetAsync(key, ct);
            if (record is null)
            {
                _logger.LogDebug(
                    "Reconciliation: no idempotency record for key '{Key}' ({Owner}) — " +
                    "nothing to settle.", key, ownerDescription);
                return;
            }

            if (record.State != IdempotencyState.InProgress)
            {
                // Already settled (Completed/Failed) — idempotent no-op. This
                // is the normal case on a re-run.
                _logger.LogDebug(
                    "Reconciliation: idempotency record '{Key}' ({Owner}) already " +
                    "terminal ({State}) — skipping (idempotent).",
                    key, ownerDescription, record.State);
                return;
            }

            if (settleCompleted)
            {
                await _idempotency.CompleteAsync(key, payloadOrReason, ct);
                _logger.LogInformation(
                    "Reconciliation: settled orphaned idempotency record '{Key}' " +
                    "({Owner}) InProgress -> Completed (chain-confirmed; payload={Payload}).",
                    key, ownerDescription, payloadOrReason);
            }
            else
            {
                await _idempotency.FailAsync(key, payloadOrReason, ct);
                _logger.LogWarning(
                    "Reconciliation: settled orphaned idempotency record '{Key}' " +
                    "({Owner}) InProgress -> Failed (chain-confirmed terminal failure: {Reason}).",
                    key, ownerDescription, payloadOrReason);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex)
        {
            // The store throws this when no claim exists for the key — a benign
            // race (the key was cleaned up / never persisted). Reconciliation
            // must not error on settlement; the entity status write already
            // succeeded conditionally.
            _logger.LogWarning(ex,
                "Reconciliation: idempotency settle for key '{Key}' ({Owner}) found " +
                "no claim (benign race) — entity status already reconciled; skipping.",
                key, ownerDescription);
        }
        catch (Exception ex)
        {
            // Never let an idempotency-settlement failure abort the sweep or
            // undo the (already-committed, conditional) entity status write.
            _logger.LogError(ex,
                "Reconciliation: failed to settle idempotency record '{Key}' ({Owner}) — " +
                "the owning entity status was reconciled but the idempotency key may " +
                "remain InProgress. MANUAL INTERVENTION may be required.",
                key, ownerDescription);
        }
    }

    // ───────────────────────── Chain truth probing ──────────────────────────

    private enum ChainVerdict { Confirmed, FailedOnChain, Unknown }

    /// <summary>
    /// Single point that talks to the chain. Pure observation: no mutation, no
    /// re-broadcast. Any exception/timeout is downgraded to
    /// <see cref="ChainVerdict.Unknown"/> so a flaky RPC never causes a wrong
    /// status write.
    /// </summary>
    private async Task<ChainVerdict> ProbeChainAsync(
        IBlockchainProvider provider, string txHash, CancellationToken ct)
    {
        try
        {
            var result = await provider.GetTransactionStatusAsync(txHash, ct);
            return ClassifyTx(result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Reconciliation: chain probe threw for tx {TxHash} on {Chain} — treating as indeterminate",
                txHash, provider.ChainType);
            return ChainVerdict.Unknown;
        }
    }

    /// <summary>
    /// Map the provider-inconsistent status dictionary to a verdict.
    ///
    /// <para><b>RESIDUAL GAP (documented, not invented):</b> there is no
    /// provider capability that cleanly distinguishes "dropped/failed" from
    /// "not yet observed". A not-found tx surfaces as
    /// <c>AZOAResult.IsError == true</c> on both Algorand and Solana, so it is
    /// treated as <see cref="ChainVerdict.Unknown"/> (never auto-failed). A
    /// dedicated provider method (e.g. <c>GetTransactionConfirmationAsync</c>
    /// returning an explicit Confirmed/Dropped/Pending tri-state) would let
    /// reconciliation also auto-fail genuinely-dropped txs; until then those
    /// remain operator-resolved via the hard-stuck flag.</para>
    /// </summary>
    private static ChainVerdict ClassifyTx(AZOAResult<Dictionary<string, object>> result)
    {
        // IsError ⇒ tx not found / RPC error / not-yet-mined. Ambiguous by
        // construction. NEVER treat as failure.
        if (result.IsError || result.Result is null)
            return ChainVerdict.Unknown;

        var d = result.Result;

        // Positive confirmation signals (provider-specific keys).
        if (ChainTxClassifier.ReadBool(d, "confirmed") == true) return ChainVerdict.Confirmed; // Algorand
        if (ChainTxClassifier.ReadBool(d, "success") == true) return ChainVerdict.Confirmed;   // Solana

        // Explicit on-chain negative: the tx exists on-chain but failed.
        if (ChainTxClassifier.ReadBool(d, "success") == false) return ChainVerdict.FailedOnChain; // Solana revert
        if (ChainTxClassifier.ReadBool(d, "confirmed") == false) return ChainVerdict.Unknown;     // Algorand: 0 rounds = not yet, NOT failed

        // A non-error result with no recognizable signal: do not guess.
        return ChainVerdict.Unknown;
    }

    // ───────────────────────── Provider resolution ──────────────────────────

    /// <summary>
    /// Resolves the chain provider using the bridge row's persisted network when available,
    /// falling back to Devnet when absent (rows written before the network field existed).
    /// </summary>
    private IBlockchainProvider? TryResolveProvider(string? chain, ChainNetwork? network = null)
    {
        if (string.IsNullOrWhiteSpace(chain)) return SafeDefaultProvider();
        try
        {
            // G5: prefer the network stamped on the row; fall back to Devnet for pre-field rows.
            var resolvedNetwork = network ?? ChainNetwork.Devnet;
            return _chainFactory.GetProvider(chain, resolvedNetwork);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Reconciliation: provider resolution failed for chain '{Chain}'", chain);
            return null;
        }
    }

    private IBlockchainProvider? SafeDefaultProvider()
    {
        try { return _chainFactory.GetDefaultProvider(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconciliation: default provider resolution failed");
            return null;
        }
    }
}
