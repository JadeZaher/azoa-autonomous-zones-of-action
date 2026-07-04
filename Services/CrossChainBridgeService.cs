using Microsoft.Extensions.Options;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Bridge;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Bridge;

namespace AZOA.WebAPI.Services;

/// <summary>
/// Hybrid cross-chain bridge orchestrator.
///
/// Trusted mode: AZOA server coordinates lock→mint (fast, custodial).
/// Wormhole mode: Guardian network produces VAAs for trustless proof verification.
///
/// Bridge transactions are persisted via IBridgeStore. Service is Scoped
/// (tied to request scope).
/// </summary>
public class CrossChainBridgeService : ICrossChainBridgeService
{
    private readonly IBlockchainProviderFactory _factory;
    private readonly IWormholeAdapter _wormhole;
    private readonly WormholeConfig _wormholeConfig;
    private readonly IBridgeStore _bridgeStore;
    private readonly IIdempotencyStore _idempotency;
    private readonly ILogger<CrossChainBridgeService> _logger;
    private readonly BridgeOptions _bridgeOptions;
    private readonly ChainNetwork _network;

    public CrossChainBridgeService(
        IBlockchainProviderFactory factory,
        IWormholeAdapter wormhole,
        IOptions<WormholeConfig> wormholeConfig,
        IBridgeStore bridgeStore,
        IIdempotencyStore idempotency,
        ILogger<CrossChainBridgeService> logger,
        IOptions<BridgeOptions> bridgeOptions,
        IConfiguration config)
    {
        _factory = factory;
        _wormhole = wormhole;
        _wormholeConfig = wormholeConfig.Value;
        _bridgeStore = bridgeStore;
        _idempotency = idempotency;
        _logger = logger;
        _bridgeOptions = bridgeOptions.Value;
        // Single config-driven network for provider resolution + row stamping.
        _network = Enum.TryParse<ChainNetwork>(
            config.GetValue<string>("Blockchain:DefaultNetwork"), ignoreCase: true, out var net)
            ? net : ChainNetwork.Devnet;
    }

    public async Task<AZOAResult<BridgeTransactionResult>> InitiateBridgeAsync(
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

            // Kill switch: refuse real-value movement when flag is off and at least one chain is non-simulated.
            if (!_bridgeOptions.RealValueEnabled && !IsSimulatedRoute(sourceChain, targetChain))
                return Error<BridgeTransactionResult>(
                    "Bridge real-value movement is disabled (Blockchain:Bridge:RealValueEnabled=false). Simulated-chain routes remain available.");

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

    public async Task<AZOAResult<BridgeTransactionResult>> FetchVAAAsync(
        string bridgeTransactionId, CancellationToken ct = default)
    {
        var tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
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
            await _bridgeStore.RecordVaaFetchErrorAsync(tx.Id, vaaResult.Message, ct);
            return Error<BridgeTransactionResult>($"VAA fetch failed: {vaaResult.Message}");
        }

        var vaa = vaaResult.Result!;
        bool saved = await _bridgeStore.SaveVaaFetchResultAsync(
            tx.Id, vaa.VaaBytes, vaa.SignatureCount, vaa.Digest, BridgeStatus.VAAReady, ct);

        if (!saved)
        {
            // Predicate missed: row was not AwaitingVAA — a concurrent call already advanced it.
            // Re-read to determine if the outcome is already acceptable (idempotent) or unexpected.
            tx = await _bridgeStore.GetBridgeAsync(tx.Id, ct);
            if (tx is null)
                return Error<BridgeTransactionResult>("Bridge transaction vanished during concurrent VAA save");
            if (tx.Status is BridgeStatus.VAAReady or BridgeStatus.Redeeming
                          or BridgeStatus.Completed or BridgeStatus.Refunded)
            {
                // VAA is already saved and the bridge has advanced — replay as success.
                _logger.LogInformation(
                    "VAA save skipped for bridge {Id}: row already in {Status} (concurrent advance won)",
                    tx.Id, tx.Status);
                return Ok(tx, $"VAA already fetched (concurrent advance won — bridge is {tx.Status})");
            }
            return Error<BridgeTransactionResult>(
                $"VAA save rejected: bridge {tx.Id} is in unexpected state {tx.Status} after concurrent advance");
        }

        // Re-fetch to return the updated snapshot.
        tx = await _bridgeStore.GetBridgeAsync(tx.Id, ct);
        if (tx == null)
            return Error<BridgeTransactionResult>("Bridge transaction vanished after VAA save");

        _logger.LogInformation(
            "VAA ready for bridge {Id}: seq={Sequence} sigs={Sigs}",
            tx.Id, vaa.Sequence, vaa.SignatureCount);

        return Ok(tx, "VAA fetched — ready for redemption");
    }

    public async Task<AZOAResult<BridgeTransactionResult>> RedeemWithVAAAsync(
        string bridgeTransactionId, CancellationToken ct = default,
        string? clientIdempotencyKey = null)
    {
        var tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
        if (tx == null)
            return Error<BridgeTransactionResult>("Bridge transaction not found");

        if (tx.Mode != BridgeMode.Wormhole)
            return Error<BridgeTransactionResult>("Redeem is only available for Wormhole bridges");

        if (string.IsNullOrWhiteSpace(tx.VaaBytes))
            return Error<BridgeTransactionResult>("No VAA available — call FetchVAA first");

        // Kill switch: refuse real-value redeem when flag is off and route is non-simulated.
        if (!_bridgeOptions.RealValueEnabled && !IsSimulatedRoute(tx.SourceChain, tx.TargetChain))
            return Error<BridgeTransactionResult>(
                "Bridge real-value movement is disabled (Blockchain:Bridge:RealValueEnabled=false). Simulated-chain routes remain available.");

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
            vaaDigest = AZOA.WebAPI.Services.WormholeAdapter.ComputeVaaDigest(tx.VaaBytes);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _logger.LogError(ex,
                "Bridge {Id} redeem rejected: VaaBytes is not valid base64 — cannot compute "
                + "canonical replay digest; refusing to mint an unverifiable VAA", tx.Id);
            return Error<BridgeTransactionResult>(
                "VAA bytes are malformed (not valid base64) — redeem rejected, no mint performed");
        }
        // Client-supplied Idempotency-Key wins but is avatar-namespaced (two avatars
        // sending the same key must never collide on one claim record); else the
        // deterministic (bridge, VAA) content key. Absence is dedup-safe (never random).
        var idempotencyKey = string.IsNullOrWhiteSpace(clientIdempotencyKey)
            ? $"bridge-redeem:{tx.Id}:{vaaDigest}"
            : $"{tx.AvatarId:N}:{clientIdempotencyKey}";

        // ── Step 1: exactly-once claim. Won==false ⇒ a duplicate; never mint. ──
        var claim = await _idempotency.TryClaimAsync(idempotencyKey, "bridge-redeem", ct);
        if (!claim.Won)
        {
            switch (claim.Record.State)
            {
                case IdempotencyState.Completed:
                    // Re-fetch the now-terminal row and replay the prior success.
                    tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
                    if (tx == null)
                        return Error<BridgeTransactionResult>("Bridge transaction not found during idempotent replay");
                    return Ok(tx,
                        $"Redeem already completed (idempotent replay): redeemTx={claim.Record.ResultPayload}");
                case IdempotencyState.Failed:
                    return Error<BridgeTransactionResult>(
                        $"Redeem already failed (idempotent replay): {claim.Record.Error}");
                default: // InProgress — stale-claim takeover decision tree.
                {
                    var claimAge = DateTime.UtcNow - claim.Record.CreatedAt;
                    if (claimAge.TotalSeconds < _bridgeOptions.StaleClaimTakeoverSeconds)
                        // Still fresh: a live request is probably in flight; reject.
                        return Error<BridgeTransactionResult>(
                            "Redeem already in progress for this VAA — request rejected to prevent double-mint");

                    // Stale claim: re-read the bridge row to determine safe resume path.
                    tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
                    if (tx == null)
                        return Error<BridgeTransactionResult>("Bridge transaction not found during stale-claim recovery");

                    switch (tx.Status)
                    {
                        case BridgeStatus.Completed:
                            // Already terminal: settle idempotency and return success.
                            await _idempotency.CompleteAsync(idempotencyKey,
                                tx.RedemptionTxHash ?? tx.MintTxHash ?? string.Empty, ct);
                            return Ok(tx, "Redeem already completed (stale-claim idempotent replay)");

                        case BridgeStatus.Failed:
                            await _idempotency.FailAsync(idempotencyKey,
                                tx.ErrorMessage ?? "redeem failed", ct);
                            return Error<BridgeTransactionResult>(
                                $"Redeem already failed (stale-claim idempotent replay): {tx.ErrorMessage}");

                        case BridgeStatus.VAAReady:
                            // Crash window: claim was inserted but the VAAReady→Redeeming
                            // transition never committed. RESUME under the existing claim
                            // using the normal post-claim flow.
                            _logger.LogInformation(
                                "Stale redeem claim for bridge {Id}: resuming from VAAReady under existing claim",
                                tx.Id);
                            return await RedeemFromVaaReadyAsync(
                                tx, vaaDigest, idempotencyKey, ct);

                        case BridgeStatus.Redeeming:
                        {
                            // Check the consumed-VAA ledger: ABSENCE proves no on-chain submit.
                            var existingVaa = await _bridgeStore.GetConsumedVaaAsync(vaaDigest, ct);
                            if (existingVaa == null)
                            {
                                // No consume-row → proven no on-chain call → safe to resume from consume step.
                                _logger.LogInformation(
                                    "Stale redeem claim for bridge {Id}: no consumed-VAA row found, "
                                    + "resuming from consume step under existing claim", tx.Id);
                                return await RedeemFromConsumeStepAsync(
                                    tx, vaaDigest, idempotencyKey, ct);
                            }
                            // Consumed row exists: ambiguous — crash windows 4/5 (consume-row written,
                            // on-chain may or may not have landed). Fail closed; leave Redeeming for sweep.
                            const string ambigMsg =
                                "redeem outcome ambiguous after crash — parked for reconciliation";
                            _logger.LogWarning(
                                "Stale redeem claim for bridge {Id}: consumed-VAA row present but "
                                + "bridge is Redeeming — parked for reconciliation (digest {Digest})",
                                tx.Id, vaaDigest);
                            return Error<BridgeTransactionResult>(
                                $"{ambigMsg} (bridge {tx.Id}); manual/operator resolution required");
                        }

                        default:
                            return Error<BridgeTransactionResult>(
                                $"Redeem in progress for bridge {tx.Id} (state {tx.Status}) — rejected to prevent double-mint");
                    }
                }
            }
        }

        return await RedeemFromVaaReadyAsync(tx, vaaDigest, idempotencyKey, ct);
    }

    /// <summary>
    /// Shared continuation: atomic VAAReady→Redeeming, then consume and redeem.
    /// Called both by the fresh-claim winner and by the stale-claim VAAReady resume path.
    /// Preserves the full exactly-once invariant: transition predicate is the gate.
    /// </summary>
    private async Task<AZOAResult<BridgeTransactionResult>> RedeemFromVaaReadyAsync(
        BridgeTransactionResult tx, string vaaDigest, string idempotencyKey, CancellationToken ct)
    {
        // ── Step 2: atomic VAAReady → Redeeming. Persisted BEFORE any on-chain
        // call. The WHERE Status==VAAReady predicate makes this the single point
        // that elects the exclusive redeem owner. ──
        int affected = await _bridgeStore.TryTransitionBridgeStatusAsync(
            tx.Id, BridgeStatus.VAAReady, BridgeStatus.Redeeming,
            new BridgeStatusMutation { IdempotencyKey = idempotencyKey }, ct);

        if (affected != 1)
        {
            // Lost the race or not in VAAReady. Re-read to decide; never mint.
            tx = await _bridgeStore.GetBridgeAsync(tx.Id, ct);
            if (tx != null && (tx.Status is BridgeStatus.Redeeming or BridgeStatus.Completed))
            {
                await _idempotency.FailAsync(idempotencyKey,
                    $"Concurrent redeem already advanced bridge to {tx.Status}", ct);
                return Error<BridgeTransactionResult>(
                    $"Bridge already being redeemed by a concurrent request (state {tx.Status}) — rejected to prevent double-mint");
            }

            var rejectMsg = tx != null
                ? $"Bridge is in {tx.Status} state, expected VAAReady"
                : "Bridge transaction not found";
            await _idempotency.FailAsync(idempotencyKey, rejectMsg, ct);
            return Error<BridgeTransactionResult>(rejectMsg);
        }

        return await RedeemFromConsumeStepAsync(tx, vaaDigest, idempotencyKey, ct);
    }

    /// <summary>
    /// Shared continuation starting at the consume-insert step (after VAAReady→Redeeming
    /// has been persisted). Used by both the normal path and the stale-claim Redeeming+no-consumed-row
    /// resume path.
    /// </summary>
    private async Task<AZOAResult<BridgeTransactionResult>> RedeemFromConsumeStepAsync(
        BridgeTransactionResult tx, string vaaDigest, string idempotencyKey, CancellationToken ct)
    {
        // ── Step 3: VAA replay ledger. Insert-before-redeem; a duplicate digest
        // means this VAA was already consumed elsewhere ⇒ reject, never mint. ──
        var vaaRecord = new ConsumedVaaRecord
        {
            Digest = vaaDigest,
            EmitterChainId = tx.WormholeEmitterChainId ?? 0,
            EmitterAddress = tx.WormholeEmitterAddress ?? "",
            Sequence = tx.WormholeSequence ?? 0,
            BridgeTransactionId = tx.Id,
            ConsumedAt = DateTime.UtcNow
        };
        bool inserted = await _bridgeStore.TryInsertConsumedVaaAsync(vaaRecord, ct);
        if (!inserted)
        {
            // Consume insert rejected — check if the existing row belongs to THIS bridge
            // (our own prior crashed attempt) vs. a different bridge (genuine cross-bridge replay).
            var existingVaa = await _bridgeStore.GetConsumedVaaAsync(vaaDigest, ct);
            if (existingVaa?.BridgeTransactionId != null
                && string.Equals(existingVaa.BridgeTransactionId, tx.Id, StringComparison.Ordinal))
            {
                // Our own prior crashed attempt: row was already written before the crash.
                // AMBIGUOUS — on-chain may or may not have landed. Fail closed; leave Redeeming.
                const string ownCrashMsg =
                    "redeem outcome ambiguous after crash — parked for reconciliation";
                _logger.LogWarning(
                    "VAA consume-insert skipped for bridge {Id}: row already present from our "
                    + "own prior attempt (digest {Digest}) — parked for reconciliation",
                    tx.Id, vaaDigest);
                return Error<BridgeTransactionResult>(
                    $"{ownCrashMsg} (bridge {tx.Id}); manual/operator resolution required");
            }

            // Cross-bridge replay: the VAA was consumed by a DIFFERENT bridge.
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
                "VAA replay rejected for bridge {Id}: digest {Digest} consumed by a different bridge",
                tx.Id, vaaDigest);
            return Error<BridgeTransactionResult>(replayMsg);
        }

        var vaaObj = new WormholeVAA
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
            tx.TargetChain, vaaObj, tx.TargetAddress, ct);

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
        int completed = await _bridgeStore.TryTransitionBridgeStatusAsync(
            tx.Id, BridgeStatus.Redeeming, BridgeStatus.Completed,
            new BridgeStatusMutation
            {
                RedemptionTxHash = redemption.TxHash,
                MintTxHash = redemption.TxHash,
                SetCompletedAtUtcNow = true,
            }, ct);

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

        // Re-fetch final state for the response.
        tx = await _bridgeStore.GetBridgeAsync(tx.Id, ct);
        if (tx == null)
            return Error<BridgeTransactionResult>("Bridge transaction vanished after completion");

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
        return await _bridgeStore.TryTransitionBridgeStatusAsync(
            bridgeId, BridgeStatus.Redeeming, BridgeStatus.Failed,
            new BridgeStatusMutation { ErrorMessage = errorMessage }, ct);
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
        var row = await _bridgeStore.GetBridgeAsync(bridgeId, ct);
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

    public async Task<AZOAResult<BridgeTransactionResult>> CompleteBridgeAsync(
        string bridgeTransactionId, CancellationToken ct = default)
    {
        var tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
        if (tx == null)
            return Error<BridgeTransactionResult>("Bridge transaction not found");

        if (tx.Status == BridgeStatus.Completed)
            return Ok(tx, "Bridge already completed");

        var affected = await _bridgeStore.ForceCompleteBridgeAsync(bridgeTransactionId, ct);
        if (affected == 0)
        {
            // Raced — re-fetch and report the actual terminal state.
            tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
            if (tx?.Status == BridgeStatus.Completed) return Ok(tx, "Bridge already completed");
            return Error<BridgeTransactionResult>(
                $"Could not complete bridge: state {tx?.Status.ToString() ?? "MISSING"}");
        }

        tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
        return Ok(tx!, "Bridge marked as completed");
    }

    public async Task<AZOAResult<BridgeTransactionResult>> ReverseBridgeAsync(
        string bridgeTransactionId, string sourceRecipientAddress, CancellationToken ct = default,
        string? clientIdempotencyKey = null)
    {
        var tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
        if (tx == null)
            return Error<BridgeTransactionResult>("Bridge transaction not found");

        if (tx.Status == BridgeStatus.Refunded)
            return Ok(tx, "Bridge already reversed (idempotent replay)");

        if (tx.Status != BridgeStatus.Completed)
            return Error<BridgeTransactionResult>("Only completed bridges can be reversed");

        // Kill switch: refuse real-value reversal when flag is off and route is non-simulated.
        if (!_bridgeOptions.RealValueEnabled && !IsSimulatedRoute(tx.SourceChain, tx.TargetChain))
            return Error<BridgeTransactionResult>(
                "Bridge real-value movement is disabled (Blockchain:Bridge:RealValueEnabled=false). Simulated-chain routes remain available.");

        if (string.IsNullOrWhiteSpace(sourceRecipientAddress))
            return Error<BridgeTransactionResult>("Source recipient address is required for reversal");

        // The reversal itself is an irreversible chain effect (burn-wrapped) —
        // gate it so a retried/concurrent reverse cannot double-burn.
        // Client Idempotency-Key wins but is avatar-namespaced; else deterministic key.
        var idempotencyKey = string.IsNullOrWhiteSpace(clientIdempotencyKey)
            ? $"bridge-reverse:{tx.Id}:{sourceRecipientAddress}"
            : $"{tx.AvatarId:N}:{clientIdempotencyKey}";
        var claim = await _idempotency.TryClaimAsync(idempotencyKey, "bridge-reverse", ct);
        if (!claim.Won)
        {
            switch (claim.Record.State)
            {
                case IdempotencyState.Completed:
                    tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
                    if (tx == null)
                        return Error<BridgeTransactionResult>("Bridge transaction not found during idempotent replay");
                    return Ok(tx, "Bridge already reversed (idempotent replay)");
                case IdempotencyState.Failed:
                    return Error<BridgeTransactionResult>(
                        $"Bridge reversal already failed (idempotent replay): {claim.Record.Error}");
                default: // InProgress — stale-claim takeover decision tree.
                {
                    var claimAge = DateTime.UtcNow - claim.Record.CreatedAt;
                    if (claimAge.TotalSeconds < _bridgeOptions.StaleClaimTakeoverSeconds)
                        return Error<BridgeTransactionResult>(
                            "Bridge reversal already in progress — rejected to prevent double-burn");

                    // Stale claim: re-read the bridge row to determine the safe path.
                    tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
                    if (tx == null)
                        return Error<BridgeTransactionResult>("Bridge transaction not found during stale-claim recovery");

                    switch (tx.Status)
                    {
                        case BridgeStatus.Refunded:
                            // Already reversed: settle and return success.
                            await _idempotency.CompleteAsync(idempotencyKey,
                                tx.RedemptionTxHash ?? string.Empty, ct);
                            return Ok(tx, "Bridge already reversed (stale-claim idempotent replay)");

                        case BridgeStatus.Completed:
                            // Crash before the Completed→Reversing transition: no burn happened.
                            // RESUME under the existing claim into the normal reverse flow.
                            _logger.LogInformation(
                                "Stale reverse claim for bridge {Id}: resuming from Completed under existing claim",
                                tx.Id);
                            break; // falls through to the Completed→Reversing transition below

                        case BridgeStatus.Reversing:
                            // Burn tx hash is NOT persisted before the terminal transition (audit-verified).
                            // AMBIGUOUS — burn may or may not have landed. Fail closed; leave Reversing for sweep.
                            const string revAmbigMsg =
                                "reversal outcome ambiguous after crash — parked for reconciliation";
                            _logger.LogWarning(
                                "Stale reverse claim for bridge {Id}: row is Reversing — "
                                + "parked for reconciliation (burn tx hash not persisted pre-transition)",
                                tx.Id);
                            return Error<BridgeTransactionResult>(
                                $"{revAmbigMsg} (bridge {tx.Id}); manual/operator resolution required");

                        default:
                            return Error<BridgeTransactionResult>(
                                $"Bridge reversal in unexpected state {tx.Status} during stale-claim recovery — rejected");
                    }
                    break; // resume into the Completed→Reversing transition
                }
            }
        }

        // Re-fetch tx if the stale-claim Completed-resume path updated it, otherwise use as-is.
        // (Fresh-claim winner already has the original tx above; stale-Completed resume re-read above.)
        tx ??= await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
        if (tx == null)
            return Error<BridgeTransactionResult>("Bridge transaction not found before reversal transition");

        // Atomic Completed → Reversing guard: only the winner of this transition
        // performs the on-chain burn. Reversing is an EXPLICIT reversal-in-flight
        // state (distinct from the forward redeem's Redeeming) so reversal
        // provenance is unambiguous — never inferred from a CompletedAt stamp.
        // Terminal state below is Refunded (success) or Failed (manual).
        int affected = await _bridgeStore.TryTransitionBridgeStatusAsync(
            tx.Id, BridgeStatus.Completed, BridgeStatus.Reversing,
            new BridgeStatusMutation { IdempotencyKey = idempotencyKey }, ct);

        if (affected != 1)
        {
            tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
            var raceMsg = $"Bridge no longer reversible (state {tx?.Status.ToString() ?? "MISSING"}) — concurrent operation won";
            await _idempotency.FailAsync(idempotencyKey, raceMsg, ct);
            return Error<BridgeTransactionResult>(raceMsg);
        }

        // Attempt a real on-chain reversal: burn the wrapped asset on the target
        // chain to release the original on the source chain.
        AZOAResult<string>? burnResult = null;
        string? burnError = null;
        try
        {
            var targetProvider = _factory.GetProvider(tx.TargetChain, _network);
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
            await _bridgeStore.TryTransitionBridgeStatusAsync(
                tx.Id, BridgeStatus.Reversing, BridgeStatus.Refunded,
                new BridgeStatusMutation
                {
                    RedemptionTxHash = burnResult.Result,
                    SetCompletedAtUtcNow = true,
                }, ct);
            await _idempotency.CompleteAsync(idempotencyKey, burnResult.Result ?? string.Empty, ct);

            // Re-fetch final state for the response.
            tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
            if (tx == null)
                return Error<BridgeTransactionResult>("Bridge transaction vanished after refund");

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
        await _bridgeStore.TryTransitionBridgeStatusAsync(
            tx.Id, BridgeStatus.Reversing, BridgeStatus.Failed,
            new BridgeStatusMutation { ErrorMessage = manualMsg }, ct);
        await _idempotency.FailAsync(idempotencyKey, manualMsg, ct);

        // Re-fetch final state for the response.
        tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);

        _logger.LogError(
            "Bridge reversal requires manual intervention: {Id} → {SourceRecipient}: {Detail}",
            bridgeTransactionId, sourceRecipientAddress, detail);
        return Error<BridgeTransactionResult>(manualMsg);
    }

    public async Task<AZOAResult<IEnumerable<BridgeTransactionResult>>> GetBridgeHistoryAsync(
        Guid avatarId, CancellationToken ct = default)
    {
        var history = await _bridgeStore.GetBridgeHistoryAsync(avatarId, descending: true, ct);

        return Ok<IEnumerable<BridgeTransactionResult>>(
            history, $"Retrieved {history.Count} bridge transactions");
    }

    public async Task<AZOAResult<IEnumerable<BridgeRouteInfo>>> GetSupportedRoutesAsync(
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
                    WormholeTargetChainId = _wormhole.GetWormholeChainId(tgt.ChainType),
                    RealValueEnabled = _bridgeOptions.RealValueEnabled || IsSimulatedRoute(src.ChainType, tgt.ChainType)
                });
            }
        }

        return Ok<IEnumerable<BridgeRouteInfo>>(routes, $"Retrieved {routes.Count} bridge routes");
    }

    public async Task<AZOAResult<BridgeTransactionResult>> GetBridgeStatusAsync(
        string bridgeTransactionId, CancellationToken ct = default)
    {
        var tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
        if (tx == null)
            return Error<BridgeTransactionResult>("Bridge transaction not found");

        return Ok(tx, $"Bridge status: {tx.Status} (mode: {tx.Mode})");
    }

    // ─── Private: Wormhole (trustless) flow ───

    private async Task<AZOAResult<BridgeTransactionResult>> InitiateWormholeBridgeAsync(
        string sourceChain, string targetChain, string tokenId,
        string recipientAddress, Guid avatarId, int amount,
        CancellationToken ct, string? clientIdempotencyKey = null)
    {
        var bridgeId = $"wh_bridge_{Guid.NewGuid():N}";

        // Persist a tracking row (status Initiated) BEFORE the on-chain lock so a
        // save failure can never strand funds with no record, and an orphaned
        // lock left by a crash is recoverable by the reconciliation sweep.
        // Client Idempotency-Key wins but is avatar-namespaced; else deterministic content key.
        var idempotencyKey = string.IsNullOrWhiteSpace(clientIdempotencyKey)
            ? $"bridge-wh-initiate:{avatarId:N}:{sourceChain}:{targetChain}:{tokenId}:{recipientAddress}:{amount}"
            : $"{avatarId:N}:{clientIdempotencyKey}";

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
            CreatedAt = DateTime.UtcNow,
            Network = _network,
        };
        await _bridgeStore.AddBridgeAsync(bridgeTx, ct);

        var initiationResult = await _wormhole.InitiateTransferAsync(
            sourceChain, targetChain, tokenId, "", recipientAddress, amount, ct);

        if (initiationResult.IsError)
        {
            // Leave the row in a recoverable Failed state — the on-chain lock did
            // not succeed (or its outcome is unknown); the reconciliation sweep
            // re-derives truth from chain confirmations for any orphan.
            var initErr = $"Wormhole initiation failed: {initiationResult.Message}";
            await _bridgeStore.TryTransitionBridgeStatusAsync(
                bridgeId, BridgeStatus.Initiated, BridgeStatus.Failed,
                new BridgeStatusMutation { ErrorMessage = initErr }, ct);
            return Error<BridgeTransactionResult>(initErr);
        }

        var initiation = initiationResult.Result!;

        // Lock landed: record emitter/sequence and advance to AwaitingVAA.
        await _bridgeStore.TryTransitionBridgeStatusAsync(
            bridgeId, BridgeStatus.Initiated, BridgeStatus.AwaitingVAA,
            new BridgeStatusMutation
            {
                LockTxHash = initiation.TxHash,
                WormholeEmitterChainId = initiation.EmitterChainId,
                WormholeEmitterAddress = initiation.EmitterAddress,
                WormholeSequence = initiation.Sequence,
            }, ct);

        // Re-fetch to return the updated snapshot.
        var updated = await _bridgeStore.GetBridgeAsync(bridgeId, ct);
        if (updated == null)
            return Error<BridgeTransactionResult>("Bridge transaction vanished after initiation");

        _logger.LogInformation(
            "Wormhole bridge initiated: {Id} {Source}→{Target} seq={Sequence} — awaiting Guardian VAA",
            bridgeId, sourceChain, targetChain, initiation.Sequence);

        return Ok(updated,
            $"Wormhole bridge initiated: {sourceChain} → {targetChain}. " +
            $"Call FetchVAA to poll for Guardian signatures, then RedeemWithVAA to complete.");
    }

    // ─── Private: Trusted (custodial) flow ───

    private async Task<AZOAResult<BridgeTransactionResult>> InitiateTrustedBridgeAsync(
        string sourceChain, string targetChain, string tokenId,
        string recipientAddress, Guid avatarId, int amount,
        CancellationToken ct, string? clientIdempotencyKey = null)
    {
        var sourceProvider = _factory.GetProvider(sourceChain, _network);
        var targetProvider = _factory.GetProvider(targetChain, _network);

        if (!sourceProvider.SupportsBridging)
            return Error<BridgeTransactionResult>($"{sourceChain} does not support bridging");

        // Deterministic idempotency key for the trusted lock→mint pair: identical
        // bridge requests (same avatar/route/token/recipient/amount) collapse to
        // a single irreversible chain effect under duplicate/concurrent calls.
        // Client Idempotency-Key wins but is avatar-namespaced; else the deterministic key.
        var idempotencyKey = string.IsNullOrWhiteSpace(clientIdempotencyKey)
            ? $"bridge-trusted:{avatarId:N}:{sourceChain}:{targetChain}:{tokenId}:{recipientAddress}:{amount}"
            : $"{avatarId:N}:{clientIdempotencyKey}";

        var claim = await _idempotency.TryClaimAsync(idempotencyKey, "bridge-trusted", ct);
        if (!claim.Won)
        {
            switch (claim.Record.State)
            {
                case IdempotencyState.Completed:
                {
                    var prior = await _bridgeStore.GetBridgeByIdempotencyKeyAsync(idempotencyKey, ct);
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
            CreatedAt = DateTime.UtcNow,
            Network = _network,
        };
        await _bridgeStore.AddBridgeAsync(bridgeTx, ct);

        var bridgeVault = GetBridgeVaultAddress(sourceChain, targetChain);
        var lockResult = await sourceProvider.LockForBridgeAsync(
            tokenId, bridgeVault, amount, targetChain, recipientAddress, ct);

        if (lockResult.IsError)
        {
            var lockErr = $"Source chain lock failed: {lockResult.Message}";
            await _bridgeStore.TryTransitionBridgeStatusAsync(
                bridgeTx.Id, BridgeStatus.Initiated, BridgeStatus.Failed,
                new BridgeStatusMutation { ErrorMessage = lockErr }, ct);
            await _idempotency.FailAsync(idempotencyKey, lockErr, ct);
            return Error<BridgeTransactionResult>(lockErr);
        }

        // Lock landed: record it and move to Locked before attempting the mint.
        await _bridgeStore.TryTransitionBridgeStatusAsync(
            bridgeTx.Id, BridgeStatus.Initiated, BridgeStatus.Locked,
            new BridgeStatusMutation
            {
                LockTxHash = lockResult.Result,
                SourceAddress = lockResult.Result ?? "",
            }, ct);

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
            await _bridgeStore.TryTransitionBridgeStatusAsync(
                bridgeTx.Id, BridgeStatus.Locked, BridgeStatus.Failed,
                new BridgeStatusMutation { ErrorMessage = mintErr }, ct);
            await _idempotency.FailAsync(idempotencyKey, mintErr, ct);
            _logger.LogError(
                "Trusted bridge mint FAILED after successful lock: {Id} {Source}→{Target} lockTx={LockTx}: {Message}",
                bridgeTx.Id, sourceChain, targetChain, lockResult.Result, mintResult.Message);
            return Error<BridgeTransactionResult>(mintErr);
        }

        // Lock + mint both succeeded: atomically stamp Completed.
        await _bridgeStore.TryTransitionBridgeStatusAsync(
            bridgeTx.Id, BridgeStatus.Locked, BridgeStatus.Completed,
            new BridgeStatusMutation
            {
                TargetTokenId = mintResult.Result,
                MintTxHash = mintResult.Result,
                SetCompletedAtUtcNow = true,
            }, ct);
        await _idempotency.CompleteAsync(idempotencyKey, mintResult.Result ?? string.Empty, ct);

        // Re-fetch to return the updated snapshot.
        var updated = await _bridgeStore.GetBridgeAsync(bridgeTx.Id, ct);
        if (updated == null)
            return Error<BridgeTransactionResult>("Bridge transaction vanished after completion");

        _logger.LogInformation(
            "Trusted bridge completed: {Id} {Source}→{Target} token={TokenId} amount={Amount}",
            bridgeTx.Id, sourceChain, targetChain, tokenId, amount);

        return Ok(updated, $"Trusted bridge completed: {sourceChain} → {targetChain}");
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

    private AZOAResult<T> Ok<T>(T result, string message = "")
        => new() { IsError = false, Result = result, Message = message };

    private AZOAResult<T> Error<T>(string message, Exception? ex = null)
    {
        _logger.LogError(ex, "Bridge error: {Message}", message);
        return new AZOAResult<T> { IsError = true, Message = message, Exception = ex };
    }

    /// <summary>
    /// True when both chains resolve to the Simulated provider via the factory.
    /// Global Blockchain:Mode=Simulated short-circuits all chains to Simulated — see BlockchainProviderFactory.
    /// Fails closed: unknown chain → not simulated.
    /// </summary>
    private bool IsSimulatedRoute(string sourceChain, string targetChain)
    {
        const string Simulated = "Simulated";
        try
        {
            var srcProvider = _factory.GetProvider(sourceChain, _network);
            var tgtProvider = _factory.GetProvider(targetChain, _network);
            return string.Equals(srcProvider.ChainType, Simulated, StringComparison.OrdinalIgnoreCase)
                && string.Equals(tgtProvider.ChainType, Simulated, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Unknown chain → cannot confirm simulated; treat as real-value (fail-closed).
            return false;
        }
    }
}
