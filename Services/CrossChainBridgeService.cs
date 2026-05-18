using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Core.Blockchain.Wormhole;
using OASIS.WebAPI.Data;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Idempotency;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Services;

/// <summary>
/// Hybrid cross-chain bridge orchestrator.
///
/// Trusted mode: OASIS server coordinates lock→mint (fast, custodial).
/// Wormhole mode: Guardian network produces VAAs for trustless proof verification.
///
/// Bridge transactions are persisted via EF Core to the PostgreSQL database.
/// Service is Scoped (tied to request DbContext).
/// </summary>
public class CrossChainBridgeService : ICrossChainBridgeService
{
    private readonly IBlockchainProviderFactory _factory;
    private readonly IWormholeAdapter _wormhole;
    private readonly WormholeConfig _wormholeConfig;
    private readonly OASISDbContext _db;
    private readonly IIdempotencyStore _idempotency;
    private readonly ILogger<CrossChainBridgeService> _logger;

    public CrossChainBridgeService(
        IBlockchainProviderFactory factory,
        IWormholeAdapter wormhole,
        IOptions<WormholeConfig> wormholeConfig,
        OASISDbContext db,
        IIdempotencyStore idempotency,
        ILogger<CrossChainBridgeService> logger)
    {
        _factory = factory;
        _wormhole = wormhole;
        _wormholeConfig = wormholeConfig.Value;
        _db = db;
        _idempotency = idempotency;
        _logger = logger;
    }

    public async Task<OASISResult<BridgeTransactionResult>> InitiateBridgeAsync(
        string sourceChain, string targetChain, string tokenId,
        string recipientAddress, Guid avatarId, int amount = 1,
        BridgeMode? mode = null, CancellationToken ct = default,
        string? clientIdempotencyKey = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceChain) || string.IsNullOrWhiteSpace(targetChain))
                return Error<BridgeTransactionResult>("Source and target chain are required");
            if (string.IsNullOrWhiteSpace(tokenId) || string.IsNullOrWhiteSpace(recipientAddress))
                return Error<BridgeTransactionResult>("Token ID and recipient address are required");
            if (amount <= 0)
                return Error<BridgeTransactionResult>("Amount must be positive");

            var resolvedMode = mode ?? _wormholeConfig.DefaultMode;

            if (resolvedMode == BridgeMode.Wormhole && !_wormhole.IsRouteSupported(sourceChain, targetChain))
            {
                _logger.LogWarning(
                    "Wormhole route {Source}→{Target} not supported, falling back to trusted mode",
                    sourceChain, targetChain);
                resolvedMode = BridgeMode.Trusted;
            }

            return resolvedMode == BridgeMode.Wormhole
                ? await InitiateWormholeBridgeAsync(sourceChain, targetChain, tokenId, recipientAddress, avatarId, amount, ct, clientIdempotencyKey)
                : await InitiateTrustedBridgeAsync(sourceChain, targetChain, tokenId, recipientAddress, avatarId, amount, ct, clientIdempotencyKey);
        }
        catch (Exception ex)
        {
            return Error<BridgeTransactionResult>($"Bridge initiation failed: {ex.Message}", ex);
        }
    }

    public async Task<OASISResult<BridgeTransactionResult>> FetchVAAAsync(
        string bridgeTransactionId, CancellationToken ct = default)
    {
        var tx = await _db.BridgeTransactions.FindAsync(new object[] { bridgeTransactionId }, ct);
        if (tx == null)
            return Error<BridgeTransactionResult>("Bridge transaction not found");

        if (tx.Mode != BridgeMode.Wormhole)
            return Error<BridgeTransactionResult>("FetchVAA is only available for Wormhole bridges");

        if (tx.Status != BridgeStatus.AwaitingVAA)
            return Error<BridgeTransactionResult>($"Bridge is in {tx.Status} state, expected AwaitingVAA");

        if (tx.WormholeEmitterChainId == null || tx.WormholeEmitterAddress == null || tx.WormholeSequence == null)
            return Error<BridgeTransactionResult>("Missing Wormhole emitter information");

        var vaaResult = await _wormhole.FetchVAAAsync(
            tx.WormholeEmitterChainId.Value,
            tx.WormholeEmitterAddress,
            tx.WormholeSequence.Value,
            ct);

        if (vaaResult.IsError)
        {
            tx.ErrorMessage = vaaResult.Message;
            await _db.SaveChangesAsync(ct);
            return Error<BridgeTransactionResult>($"VAA fetch failed: {vaaResult.Message}");
        }

        var vaa = vaaResult.Result!;
        tx.VaaBytes = vaa.VaaBytes;
        tx.VaaSignatureCount = vaa.SignatureCount;
        tx.ProofData = vaa.Digest;
        tx.Status = BridgeStatus.VAAReady;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "VAA ready for bridge {Id}: seq={Sequence} sigs={Sigs}",
            tx.Id, vaa.Sequence, vaa.SignatureCount);

        return Ok(tx, "VAA fetched — ready for redemption");
    }

    public async Task<OASISResult<BridgeTransactionResult>> RedeemWithVAAAsync(
        string bridgeTransactionId, CancellationToken ct = default,
        string? clientIdempotencyKey = null)
    {
        var tx = await _db.BridgeTransactions.FindAsync(new object[] { bridgeTransactionId }, ct);
        if (tx == null)
            return Error<BridgeTransactionResult>("Bridge transaction not found");

        if (tx.Mode != BridgeMode.Wormhole)
            return Error<BridgeTransactionResult>("Redeem is only available for Wormhole bridges");

        if (string.IsNullOrWhiteSpace(tx.VaaBytes))
            return Error<BridgeTransactionResult>("No VAA available — call FetchVAA first");

        // Derive a deterministic idempotency key: same (bridge, VAA) ⇒ same key
        // forever, so duplicate/concurrent redeem requests collapse to one mint.
        // Use the SINGLE canonical digest (SHA-256 over the BASE64-DECODED VAA
        // bytes) so the ConsumedVaas replay-ledger key collides with every other
        // producer of the same VAA. If VaaBytes is not valid base64 the canonical
        // formula throws — a VAA whose bytes are unusable must be REJECTED with a
        // deterministic error, never minted, and this runs BEFORE the claim, the
        // atomic transition, and any on-chain call (no state mutated yet).
        string vaaDigest;
        try
        {
            vaaDigest = OASIS.WebAPI.Services.WormholeAdapter.ComputeVaaDigest(tx.VaaBytes);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _logger.LogError(ex,
                "Bridge {Id} redeem rejected: VaaBytes is not valid base64 — cannot compute "
                + "canonical replay digest; refusing to mint an unverifiable VAA", tx.Id);
            return Error<BridgeTransactionResult>(
                "VAA bytes are malformed (not valid base64) — redeem rejected, no mint performed");
        }
        // Client-supplied Idempotency-Key wins (verbatim); else the deterministic
        // (bridge, VAA) content key. Absence is still dedup-safe (never random).
        var idempotencyKey = string.IsNullOrWhiteSpace(clientIdempotencyKey)
            ? $"bridge-redeem:{tx.Id}:{vaaDigest}"
            : clientIdempotencyKey;

        // ── Step 1: exactly-once claim. Won==false ⇒ a duplicate; never mint. ──
        var claim = await _idempotency.TryClaimAsync(idempotencyKey, "bridge-redeem", ct);
        if (!claim.Won)
        {
            switch (claim.Record.State)
            {
                case IdempotencyState.Completed:
                    // Re-read the now-terminal row and replay the prior success.
                    await _db.Entry(tx).ReloadAsync(ct);
                    return Ok(tx,
                        $"Redeem already completed (idempotent replay): redeemTx={claim.Record.ResultPayload}");
                case IdempotencyState.Failed:
                    return Error<BridgeTransactionResult>(
                        $"Redeem already failed (idempotent replay): {claim.Record.Error}");
                default: // InProgress — another request owns the in-flight mint.
                    return Error<BridgeTransactionResult>(
                        "Redeem already in progress for this VAA — request rejected to prevent double-mint");
            }
        }

        // ── Step 2: atomic VAAReady → Redeeming. Persisted BEFORE any on-chain
        // call. The WHERE Status==VAAReady predicate makes this the single point
        // that elects the exclusive redeem owner. ──
        int affected = await _db.BridgeTransactions
            .Where(b => b.Id == tx.Id && b.Status == BridgeStatus.VAAReady)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BridgeStatus.Redeeming)
                .SetProperty(b => b.IdempotencyKey, idempotencyKey), ct);

        if (affected != 1)
        {
            // Lost the race or not in VAAReady. Re-read to decide; never mint.
            await _db.Entry(tx).ReloadAsync(ct);
            if (tx.Status is BridgeStatus.Redeeming or BridgeStatus.Completed)
            {
                await _idempotency.FailAsync(idempotencyKey,
                    $"Concurrent redeem already advanced bridge to {tx.Status}", ct);
                return Error<BridgeTransactionResult>(
                    $"Bridge already being redeemed by a concurrent request (state {tx.Status}) — rejected to prevent double-mint");
            }

            var rejectMsg = $"Bridge is in {tx.Status} state, expected VAAReady";
            await _idempotency.FailAsync(idempotencyKey, rejectMsg, ct);
            return Error<BridgeTransactionResult>(rejectMsg);
        }

        // ── Step 3: VAA replay ledger. Insert-before-redeem; a duplicate digest
        // means this VAA was already consumed elsewhere ⇒ reject, never mint. ──
        _db.ConsumedVaas.Add(new ConsumedVaaRecord
        {
            Digest = vaaDigest,
            EmitterChainId = tx.WormholeEmitterChainId ?? 0,
            EmitterAddress = tx.WormholeEmitterAddress ?? "",
            Sequence = tx.WormholeSequence ?? 0,
            BridgeTransactionId = tx.Id,
            ConsumedAt = DateTime.UtcNow
        });
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            var dupEntry = _db.ChangeTracker.Entries<ConsumedVaaRecord>()
                .FirstOrDefault(e => e.Entity.Digest == vaaDigest);
            if (dupEntry != null)
                dupEntry.State = EntityState.Detached;

            var dup = await _db.ConsumedVaas.AsNoTracking()
                .FirstOrDefaultAsync(v => v.Digest == vaaDigest, ct);
            if (dup is not null)
            {
                const string replayMsg = "VAA already consumed — replay rejected, no mint performed";
                // No on-chain effect on this path (replay is rejected BEFORE the
                // redeem call). Only force the idempotency record to Failed if we
                // actually moved the bridge Redeeming→Failed; otherwise the row
                // already advanced under a concurrent path — mirror its true
                // state instead of stamping a possibly-wrong "failed".
                var failedRows = await FailRedeemAsync(tx.Id, replayMsg, ct);
                if (failedRows == 1)
                    await _idempotency.FailAsync(idempotencyKey, replayMsg, ct);
                else
                    await SettleIdempotencyToBridgeStateAsync(
                        tx.Id, idempotencyKey, "no on-chain redeem (VAA replay rejected)", ct);
                _logger.LogWarning(
                    "VAA replay rejected for bridge {Id}: digest {Digest} already consumed by {OtherBridge}",
                    tx.Id, vaaDigest, dup.BridgeTransactionId);
                return Error<BridgeTransactionResult>(replayMsg);
            }

            throw; // genuine DB error, not a replay
        }

        var vaa = new WormholeVAA
        {
            VaaBytes = tx.VaaBytes,
            EmitterChainId = tx.WormholeEmitterChainId ?? 0,
            EmitterAddress = tx.WormholeEmitterAddress ?? "",
            Sequence = tx.WormholeSequence ?? 0,
            SignatureCount = tx.VaaSignatureCount ?? 0,
            Version = 1
        };

        // ── Step 4: the single irreversible on-chain effect. Reached only by the
        // claim winner that also won the atomic transition and passed replay. ──
        var redeemResult = await _wormhole.RedeemTransferAsync(
            tx.TargetChain, vaa, tx.TargetAddress, ct);

        if (redeemResult.IsError)
        {
            var failMsg = redeemResult.Message;
            // Only stamp the idempotency record Failed if THIS call moved the
            // bridge Redeeming→Failed. If 0 rows changed the row already moved
            // on (concurrent path / reconciliation) — settle the idempotency
            // record to its true terminal state so a duplicate cannot replay a
            // wrong "failed" over a possibly-Completed row.
            var failedRows = await FailRedeemAsync(tx.Id, failMsg, ct);
            if (failedRows == 1)
                await _idempotency.FailAsync(idempotencyKey, failMsg ?? "redeem failed", ct);
            else
                await SettleIdempotencyToBridgeStateAsync(
                    tx.Id, idempotencyKey,
                    $"redeem submission (result error: {failMsg})", ct);
            _logger.LogError(
                "Wormhole redeem failed for bridge {Id} AFTER VAA consumed (digest {Digest}). " +
                "MANUAL INTERVENTION REQUIRED if the on-chain submission partially landed: {Message}",
                tx.Id, vaaDigest, failMsg);
            return Error<BridgeTransactionResult>(
                $"Redemption failed (manual intervention may be required): {failMsg}");
        }

        var redemption = redeemResult.Result!;

        // ── Step 5: atomic Redeeming → Completed (only the Redeeming owner). ──
        int completed = await _db.BridgeTransactions
            .Where(b => b.Id == tx.Id && b.Status == BridgeStatus.Redeeming)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BridgeStatus.Completed)
                .SetProperty(b => b.RedemptionTxHash, redemption.TxHash)
                .SetProperty(b => b.MintTxHash, redemption.TxHash)
                .SetProperty(b => b.CompletedAt, DateTime.UtcNow), ct);

        if (completed == 1)
        {
            // We are the row that performed the Redeeming→Completed transition;
            // the bridge IS Completed ⇒ the idempotency record may record success.
            await _idempotency.CompleteAsync(idempotencyKey, redemption.TxHash ?? string.Empty, ct);
        }
        else
        {
            // The conditional update touched 0 rows: the row was no longer
            // Redeeming when we got here (a concurrent path / reconciliation moved
            // it to Failed/Completed). The on-chain mint already happened, so the
            // idempotency record must settle to the row's ACTUAL terminal state —
            // never an unconditional "Completed" that could replay success over a
            // Failed/needs-intervention row.
            await SettleIdempotencyToBridgeStateAsync(
                tx.Id, idempotencyKey,
                $"redeem tx {redemption.TxHash}", ct);
        }

        await _db.Entry(tx).ReloadAsync(ct);

        _logger.LogInformation(
            "Wormhole bridge completed: {Id} {Source}→{Target} redeemTx={TxHash}",
            tx.Id, tx.SourceChain, tx.TargetChain, redemption.TxHash);

        return Ok(tx, $"Wormhole bridge completed trustlessly: {tx.SourceChain} → {tx.TargetChain}");
    }

    /// <summary>
    /// Atomic Redeeming → Failed transition with an error message, used when a
    /// claimed redeem cannot proceed (replay) or the on-chain call failed.
    /// Returns the affected-row count so callers only force the idempotency
    /// record to Failed when THIS update actually moved the bridge to Failed;
    /// a 0-count means the row was no longer Redeeming and the idempotency
    /// record must instead settle to the bridge's true terminal state.
    /// </summary>
    private async Task<int> FailRedeemAsync(string bridgeId, string? errorMessage, CancellationToken ct)
    {
        return await _db.BridgeTransactions
            .Where(b => b.Id == bridgeId && b.Status == BridgeStatus.Redeeming)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BridgeStatus.Failed)
                .SetProperty(b => b.ErrorMessage, errorMessage), ct);
    }

    /// <summary>
    /// Settle the idempotency record to the bridge row's ACTUAL terminal state
    /// after a conditional Redeeming→terminal update affected 0 rows (the row
    /// already moved on under a concurrent path / reconciliation). The on-chain
    /// effect already happened, so the idempotency record must mirror the true
    /// bridge state — never a blanket success/failure that would replay the
    /// wrong outcome to duplicates. If the row is in a non-terminal/unexpected
    /// state a mint landed against a row another component failed to advance:
    /// log at ERROR with an explicit manual-intervention message; never swallow.
    /// </summary>
    private async Task SettleIdempotencyToBridgeStateAsync(
        string bridgeId, string idempotencyKey, string onChainRef, CancellationToken ct)
    {
        var row = await _db.BridgeTransactions.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == bridgeId, ct);
        var actual = row?.Status;

        switch (actual)
        {
            case BridgeStatus.Completed:
                await _idempotency.CompleteAsync(
                    idempotencyKey, row!.RedemptionTxHash ?? row.MintTxHash ?? string.Empty, ct);
                break;
            case BridgeStatus.Failed:
                await _idempotency.FailAsync(
                    idempotencyKey,
                    row!.ErrorMessage ?? "bridge settled to Failed by a concurrent path", ct);
                break;
            default:
            {
                var manualMsg =
                    $"MANUAL INTERVENTION REQUIRED: {onChainRef} landed on-chain but bridge row " +
                    $"{bridgeId} is {(actual?.ToString() ?? "MISSING")}, not Redeeming — idempotency " +
                    "record left so duplicates cannot replay a wrong terminal outcome.";
                // Pin the idempotency record to Failed: a duplicate replaying
                // "success" over a non-terminal row is the dangerous outcome;
                // a duplicate seeing this explicit failure is safe and forces a
                // human to reconcile the on-chain mint vs the stuck bridge row.
                await _idempotency.FailAsync(idempotencyKey, manualMsg, ct);
                _logger.LogError(
                    "MANUAL INTERVENTION REQUIRED: {OnChainRef} landed on-chain but bridge row "
                    + "{BridgeId} is {Status}, not Redeeming",
                    onChainRef, bridgeId, actual?.ToString() ?? "MISSING");
                break;
            }
        }
    }

    public async Task<OASISResult<BridgeTransactionResult>> CompleteBridgeAsync(
        string bridgeTransactionId, CancellationToken ct = default)
    {
        var tx = await _db.BridgeTransactions.FindAsync(new object[] { bridgeTransactionId }, ct);
        if (tx == null)
            return Error<BridgeTransactionResult>("Bridge transaction not found");

        if (tx.Status == BridgeStatus.Completed)
            return Ok(tx, "Bridge already completed");

        tx.Status = BridgeStatus.Completed;
        tx.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(tx, "Bridge marked as completed");
    }

    public async Task<OASISResult<BridgeTransactionResult>> ReverseBridgeAsync(
        string bridgeTransactionId, string sourceRecipientAddress, CancellationToken ct = default,
        string? clientIdempotencyKey = null)
    {
        var tx = await _db.BridgeTransactions.FindAsync(new object[] { bridgeTransactionId }, ct);
        if (tx == null)
            return Error<BridgeTransactionResult>("Bridge transaction not found");

        if (tx.Status == BridgeStatus.Refunded)
            return Ok(tx, "Bridge already reversed (idempotent replay)");

        if (tx.Status != BridgeStatus.Completed)
            return Error<BridgeTransactionResult>("Only completed bridges can be reversed");

        if (string.IsNullOrWhiteSpace(sourceRecipientAddress))
            return Error<BridgeTransactionResult>("Source recipient address is required for reversal");

        // The reversal itself is an irreversible chain effect (burn-wrapped) —
        // gate it so a retried/concurrent reverse cannot double-burn.
        // Client Idempotency-Key wins (verbatim); else deterministic key.
        var idempotencyKey = string.IsNullOrWhiteSpace(clientIdempotencyKey)
            ? $"bridge-reverse:{tx.Id}:{sourceRecipientAddress}"
            : clientIdempotencyKey;
        var claim = await _idempotency.TryClaimAsync(idempotencyKey, "bridge-reverse", ct);
        if (!claim.Won)
        {
            switch (claim.Record.State)
            {
                case IdempotencyState.Completed:
                    await _db.Entry(tx).ReloadAsync(ct);
                    return Ok(tx, "Bridge already reversed (idempotent replay)");
                case IdempotencyState.Failed:
                    return Error<BridgeTransactionResult>(
                        $"Bridge reversal already failed (idempotent replay): {claim.Record.Error}");
                default:
                    return Error<BridgeTransactionResult>(
                        "Bridge reversal already in progress — rejected to prevent double-burn");
            }
        }

        // Atomic Completed → Reversing guard: only the winner of this transition
        // performs the on-chain burn. Reversing is an EXPLICIT reversal-in-flight
        // state (distinct from the forward redeem's Redeeming) so reversal
        // provenance is unambiguous — never inferred from a CompletedAt stamp.
        // Terminal state below is Refunded (success) or Failed (manual).
        int affected = await _db.BridgeTransactions
            .Where(b => b.Id == tx.Id && b.Status == BridgeStatus.Completed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BridgeStatus.Reversing)
                .SetProperty(b => b.IdempotencyKey, idempotencyKey), ct);

        if (affected != 1)
        {
            await _db.Entry(tx).ReloadAsync(ct);
            var raceMsg = $"Bridge no longer reversible (state {tx.Status}) — concurrent operation won";
            await _idempotency.FailAsync(idempotencyKey, raceMsg, ct);
            return Error<BridgeTransactionResult>(raceMsg);
        }

        // Attempt a real on-chain reversal: burn the wrapped asset on the target
        // chain to release the original on the source chain.
        OASISResult<string>? burnResult = null;
        string? burnError = null;
        try
        {
            var targetProvider = _factory.GetProvider(tx.TargetChain, ChainNetwork.Devnet);
            if (!string.IsNullOrWhiteSpace(tx.TargetTokenId))
            {
                burnResult = await targetProvider.BurnWrappedAsync(
                    tx.TargetTokenId!, tx.Amount, tx.SourceChain,
                    sourceRecipientAddress, tx.TargetAddress, ct);
            }
            else
            {
                burnError = "no wrapped TargetTokenId recorded — cannot burn wrapped asset";
            }
        }
        catch (Exception ex)
        {
            burnError = ex.Message;
        }

        if (burnResult is { IsError: false })
        {
            await _db.BridgeTransactions
                .Where(b => b.Id == tx.Id && b.Status == BridgeStatus.Reversing)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, BridgeStatus.Refunded)
                    .SetProperty(b => b.RedemptionTxHash, burnResult.Result)
                    .SetProperty(b => b.CompletedAt, DateTime.UtcNow), ct);
            await _idempotency.CompleteAsync(idempotencyKey, burnResult.Result ?? string.Empty, ct);
            await _db.Entry(tx).ReloadAsync(ct);

            _logger.LogInformation(
                "Bridge reversed on-chain: {Id} → {SourceRecipient} burnTx={BurnTx}",
                bridgeTransactionId, sourceRecipientAddress, burnResult.Result);
            return Ok(tx, "Bridge reversed — wrapped burned on target, original released on source");
        }

        // No safe automated reversal succeeded — surface an explicit
        // manual-intervention state instead of silently no-op'ing.
        var detail = burnError ?? burnResult?.Message ?? "unknown reversal failure";
        var manualMsg =
            $"MANUAL INTERVENTION REQUIRED: bridge {tx.Id} reversal could not be completed automatically " +
            $"({detail}). Manually burn wrapped {tx.TargetTokenId} on {tx.TargetChain} and release " +
            $"{tx.SourceTokenId} to {sourceRecipientAddress} on {tx.SourceChain}.";
        await _db.BridgeTransactions
            .Where(b => b.Id == tx.Id && b.Status == BridgeStatus.Reversing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BridgeStatus.Failed)
                .SetProperty(b => b.ErrorMessage, manualMsg), ct);
        await _idempotency.FailAsync(idempotencyKey, manualMsg, ct);
        await _db.Entry(tx).ReloadAsync(ct);

        _logger.LogError(
            "Bridge reversal requires manual intervention: {Id} → {SourceRecipient}: {Detail}",
            bridgeTransactionId, sourceRecipientAddress, detail);
        return Error<BridgeTransactionResult>(manualMsg);
    }

    public async Task<OASISResult<IEnumerable<BridgeTransactionResult>>> GetBridgeHistoryAsync(
        Guid avatarId, CancellationToken ct = default)
    {
        var history = await _db.BridgeTransactions
            .Where(t => t.AvatarId == avatarId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);

        return Ok<IEnumerable<BridgeTransactionResult>>(
            history, $"Retrieved {history.Count} bridge transactions");
    }

    public async Task<OASISResult<IEnumerable<BridgeRouteInfo>>> GetSupportedRoutesAsync(
        CancellationToken ct = default)
    {
        var providers = _factory.GetAllEnabledProviders().ToList();
        var routes = new List<BridgeRouteInfo>();

        for (int i = 0; i < providers.Count; i++)
        {
            for (int j = 0; j < providers.Count; j++)
            {
                if (i == j) continue;
                var src = providers[i];
                var tgt = providers[j];

                var wormholeSupported = _wormhole.IsRouteSupported(src.ChainType, tgt.ChainType);
                var modes = new List<BridgeMode> { BridgeMode.Trusted };
                if (wormholeSupported)
                    modes.Add(BridgeMode.Wormhole);

                routes.Add(new BridgeRouteInfo
                {
                    SourceChain = src.ChainType,
                    TargetChain = tgt.ChainType,
                    IsEnabled = src.SupportsBridging && tgt.SupportsBridging,
                    EstimatedTime = wormholeSupported ? "2-15 minutes (Wormhole)" : "1-5 minutes (Trusted)",
                    SupportedAssetTypes = new List<string> { "Native", "SPL/ASA", "NFT" },
                    MinAmount = "1",
                    FeeInfo = wormholeSupported
                        ? "Gas fees on source + target chain + Wormhole relayer fee"
                        : "Gas fees on source and target chain",
                    AvailableModes = modes,
                    WormholeSupported = wormholeSupported,
                    WormholeSourceChainId = _wormhole.GetWormholeChainId(src.ChainType),
                    WormholeTargetChainId = _wormhole.GetWormholeChainId(tgt.ChainType)
                });
            }
        }

        return Ok<IEnumerable<BridgeRouteInfo>>(routes, $"Retrieved {routes.Count} bridge routes");
    }

    public async Task<OASISResult<BridgeTransactionResult>> GetBridgeStatusAsync(
        string bridgeTransactionId, CancellationToken ct = default)
    {
        var tx = await _db.BridgeTransactions.FindAsync(new object[] { bridgeTransactionId }, ct);
        if (tx == null)
            return Error<BridgeTransactionResult>("Bridge transaction not found");

        return Ok(tx, $"Bridge status: {tx.Status} (mode: {tx.Mode})");
    }

    // ─── Private: Wormhole (trustless) flow ───

    private async Task<OASISResult<BridgeTransactionResult>> InitiateWormholeBridgeAsync(
        string sourceChain, string targetChain, string tokenId,
        string recipientAddress, Guid avatarId, int amount,
        CancellationToken ct, string? clientIdempotencyKey = null)
    {
        var bridgeId = $"wh_bridge_{Guid.NewGuid():N}";

        // Persist a tracking row (status Initiated) BEFORE the on-chain lock so a
        // save failure can never strand funds with no record, and an orphaned
        // lock left by a crash is recoverable by the reconciliation sweep.
        // Client Idempotency-Key wins (verbatim); else deterministic content key.
        var idempotencyKey = string.IsNullOrWhiteSpace(clientIdempotencyKey)
            ? $"bridge-wh-initiate:{avatarId:N}:{sourceChain}:{targetChain}:{tokenId}:{recipientAddress}:{amount}"
            : clientIdempotencyKey;

        var bridgeTx = new BridgeTransactionResult
        {
            Id = bridgeId,
            AvatarId = avatarId,
            SourceChain = sourceChain,
            TargetChain = targetChain,
            SourceTokenId = tokenId,
            TargetAddress = recipientAddress,
            Amount = amount,
            Mode = BridgeMode.Wormhole,
            Status = BridgeStatus.Initiated,
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTime.UtcNow
        };
        _db.BridgeTransactions.Add(bridgeTx);
        await _db.SaveChangesAsync(ct);

        var initiationResult = await _wormhole.InitiateTransferAsync(
            sourceChain, targetChain, tokenId, "", recipientAddress, amount, ct);

        if (initiationResult.IsError)
        {
            // Leave the row in a recoverable Failed state — the on-chain lock did
            // not succeed (or its outcome is unknown); the reconciliation sweep
            // re-derives truth from chain confirmations for any orphan.
            var initErr = $"Wormhole initiation failed: {initiationResult.Message}";
            await _db.BridgeTransactions
                .Where(b => b.Id == bridgeId && b.Status == BridgeStatus.Initiated)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, BridgeStatus.Failed)
                    .SetProperty(b => b.ErrorMessage, initErr), ct);
            return Error<BridgeTransactionResult>(initErr);
        }

        var initiation = initiationResult.Result!;

        // Lock landed: record emitter/sequence and advance to AwaitingVAA.
        await _db.BridgeTransactions
            .Where(b => b.Id == bridgeId && b.Status == BridgeStatus.Initiated)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BridgeStatus.AwaitingVAA)
                .SetProperty(b => b.LockTxHash, initiation.TxHash)
                .SetProperty(b => b.WormholeEmitterChainId, initiation.EmitterChainId)
                .SetProperty(b => b.WormholeEmitterAddress, initiation.EmitterAddress)
                .SetProperty(b => b.WormholeSequence, initiation.Sequence), ct);

        await _db.Entry(bridgeTx).ReloadAsync(ct);

        _logger.LogInformation(
            "Wormhole bridge initiated: {Id} {Source}→{Target} seq={Sequence} — awaiting Guardian VAA",
            bridgeTx.Id, sourceChain, targetChain, initiation.Sequence);

        return Ok(bridgeTx,
            $"Wormhole bridge initiated: {sourceChain} → {targetChain}. " +
            $"Call FetchVAA to poll for Guardian signatures, then RedeemWithVAA to complete.");
    }

    // ─── Private: Trusted (custodial) flow ───

    private async Task<OASISResult<BridgeTransactionResult>> InitiateTrustedBridgeAsync(
        string sourceChain, string targetChain, string tokenId,
        string recipientAddress, Guid avatarId, int amount,
        CancellationToken ct, string? clientIdempotencyKey = null)
    {
        var sourceProvider = _factory.GetProvider(sourceChain, ChainNetwork.Devnet);
        var targetProvider = _factory.GetProvider(targetChain, ChainNetwork.Devnet);

        if (!sourceProvider.SupportsBridging)
            return Error<BridgeTransactionResult>($"{sourceChain} does not support bridging");

        // Deterministic idempotency key for the trusted lock→mint pair: identical
        // bridge requests (same avatar/route/token/recipient/amount) collapse to
        // a single irreversible chain effect under duplicate/concurrent calls.
        // Client Idempotency-Key wins (verbatim); else the deterministic key.
        var idempotencyKey = string.IsNullOrWhiteSpace(clientIdempotencyKey)
            ? $"bridge-trusted:{avatarId:N}:{sourceChain}:{targetChain}:{tokenId}:{recipientAddress}:{amount}"
            : clientIdempotencyKey;

        var claim = await _idempotency.TryClaimAsync(idempotencyKey, "bridge-trusted", ct);
        if (!claim.Won)
        {
            switch (claim.Record.State)
            {
                case IdempotencyState.Completed:
                {
                    var prior = await _db.BridgeTransactions.AsNoTracking()
                        .FirstOrDefaultAsync(b => b.IdempotencyKey == idempotencyKey, ct);
                    if (prior is not null)
                        return Ok(prior, $"Trusted bridge already completed (idempotent replay): {sourceChain} → {targetChain}");
                    return Error<BridgeTransactionResult>(
                        $"Trusted bridge already completed (idempotent replay): mint={claim.Record.ResultPayload}");
                }
                case IdempotencyState.Failed:
                    return Error<BridgeTransactionResult>(
                        $"Trusted bridge already failed (idempotent replay): {claim.Record.Error}");
                default:
                    return Error<BridgeTransactionResult>(
                        "Trusted bridge already in progress for this request — rejected to prevent double-mint");
            }
        }

        // Persist a tracking row BEFORE the irreversible lock so a crash between
        // lock and mint is recoverable (orphan sweep), and the lock can never
        // happen without a durable record.
        var bridgeTx = new BridgeTransactionResult
        {
            Id = $"bridge_{Guid.NewGuid():N}",
            AvatarId = avatarId,
            SourceChain = sourceChain,
            TargetChain = targetChain,
            SourceTokenId = tokenId,
            TargetAddress = recipientAddress,
            Amount = amount,
            Mode = BridgeMode.Trusted,
            Status = BridgeStatus.Initiated,
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTime.UtcNow
        };
        _db.BridgeTransactions.Add(bridgeTx);
        await _db.SaveChangesAsync(ct);

        var bridgeVault = GetBridgeVaultAddress(sourceChain, targetChain);
        var lockResult = await sourceProvider.LockForBridgeAsync(
            tokenId, bridgeVault, amount, targetChain, recipientAddress, ct);

        if (lockResult.IsError)
        {
            var lockErr = $"Source chain lock failed: {lockResult.Message}";
            await _db.BridgeTransactions
                .Where(b => b.Id == bridgeTx.Id && b.Status == BridgeStatus.Initiated)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, BridgeStatus.Failed)
                    .SetProperty(b => b.ErrorMessage, lockErr), ct);
            await _idempotency.FailAsync(idempotencyKey, lockErr, ct);
            return Error<BridgeTransactionResult>(lockErr);
        }

        // Lock landed: record it and move to Locked before attempting the mint.
        await _db.BridgeTransactions
            .Where(b => b.Id == bridgeTx.Id && b.Status == BridgeStatus.Initiated)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BridgeStatus.Locked)
                .SetProperty(b => b.LockTxHash, lockResult.Result)
                .SetProperty(b => b.SourceAddress, lockResult.Result ?? ""), ct);

        var mintResult = await targetProvider.MintWrappedAsync(
            sourceChain, tokenId, $"bridge://{sourceChain}/{tokenId}",
            amount, recipientAddress, ct);

        if (mintResult.IsError)
        {
            // Funds are locked on source but mint failed on target: compensation
            // required. Mark Failed with an explicit manual-intervention message
            // so the reconciliation sweep/runbook can release the locked asset.
            var mintErr =
                $"MANUAL INTERVENTION REQUIRED: trusted bridge {bridgeTx.Id} locked source asset " +
                $"(lockTx={lockResult.Result}) but target mint failed: {mintResult.Message}. " +
                $"Release/refund the locked {tokenId} on {sourceChain} or retry the mint.";
            await _db.BridgeTransactions
                .Where(b => b.Id == bridgeTx.Id && b.Status == BridgeStatus.Locked)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.Status, BridgeStatus.Failed)
                    .SetProperty(b => b.ErrorMessage, mintErr), ct);
            await _idempotency.FailAsync(idempotencyKey, mintErr, ct);
            _logger.LogError(
                "Trusted bridge mint FAILED after successful lock: {Id} {Source}→{Target} lockTx={LockTx}: {Message}",
                bridgeTx.Id, sourceChain, targetChain, lockResult.Result, mintResult.Message);
            return Error<BridgeTransactionResult>(mintErr);
        }

        // Lock + mint both succeeded: atomically stamp Completed.
        await _db.BridgeTransactions
            .Where(b => b.Id == bridgeTx.Id && b.Status == BridgeStatus.Locked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Status, BridgeStatus.Completed)
                .SetProperty(b => b.TargetTokenId, mintResult.Result)
                .SetProperty(b => b.MintTxHash, mintResult.Result)
                .SetProperty(b => b.CompletedAt, DateTime.UtcNow), ct);
        await _idempotency.CompleteAsync(idempotencyKey, mintResult.Result ?? string.Empty, ct);

        await _db.Entry(bridgeTx).ReloadAsync(ct);

        _logger.LogInformation(
            "Trusted bridge completed: {Id} {Source}→{Target} token={TokenId} amount={Amount}",
            bridgeTx.Id, sourceChain, targetChain, tokenId, amount);

        return Ok(bridgeTx, $"Trusted bridge completed: {sourceChain} → {targetChain}");
    }

    private string GetBridgeVaultAddress(string sourceChain, string targetChain)
    {
        // Use configured vault address from Wormhole section, falling back to placeholder
        if (_wormholeConfig.BridgeVaults.TryGetValue(sourceChain, out var vaultCfg)
            && !string.IsNullOrWhiteSpace(vaultCfg.VaultAddress))
        {
            return vaultCfg.VaultAddress;
        }

        _logger.LogWarning(
            "No bridge vault configured for {Chain}. Using placeholder. Configure Blockchain:Wormhole:BridgeVaults",
            sourceChain);

        return $"{sourceChain.ToLowerInvariant()}_bridge_vault_for_{targetChain.ToLowerInvariant()}";
    }

    private OASISResult<T> Ok<T>(T result, string message = "")
        => new() { IsError = false, Result = result, Message = message };

    private OASISResult<T> Error<T>(string message, Exception? ex = null)
    {
        _logger.LogError(ex, "Bridge error: {Message}", message);
        return new OASISResult<T> { IsError = true, Message = message, Exception = ex };
    }
}
