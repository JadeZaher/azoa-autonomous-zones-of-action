using Microsoft.Extensions.Options;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Idempotency;
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
    private readonly IRealValueKycGate _realValueKycGate;
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
        IConfiguration config,
        IRealValueKycGate realValueKycGate)
    {
        _factory = factory;
        _wormhole = wormhole;
        _wormholeConfig = wormholeConfig.Value;
        _bridgeStore = bridgeStore;
        _idempotency = idempotency;
        _realValueKycGate = realValueKycGate;
        _logger = logger;
        _bridgeOptions = bridgeOptions.Value;
        // Single config-driven network for provider resolution + row stamping.
        _network = Enum.TryParse<ChainNetwork>(
            config.GetValue<string>("Blockchain:DefaultNetwork"), ignoreCase: true, out var net)
            ? net : ChainNetwork.Devnet;
    }

    public async Task<AZOAResult<BridgeTransactionResult>> InitiateBridgeAsync(
        string sourceChain, string targetChain, string tokenId,
        string recipientAddress, Guid avatarId, ulong amount = 1,
        BridgeMode? mode = null, CancellationToken ct = default,
        string? clientIdempotencyKey = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceChain) || string.IsNullOrWhiteSpace(targetChain))
                return Error<BridgeTransactionResult>("Source and target chain are required");
            if (string.IsNullOrWhiteSpace(tokenId) || string.IsNullOrWhiteSpace(recipientAddress))
                return Error<BridgeTransactionResult>("Token ID and recipient address are required");
            if (amount == 0)
                return Error<BridgeTransactionResult>("Amount must be positive");

            var simulatedRoute = IsSimulatedRoute(sourceChain, targetChain);

            // Kill switch: refuse real-value movement when flag is off and at least one chain is non-simulated.
            if (!_bridgeOptions.RealValueEnabled && !simulatedRoute)
                return Error<BridgeTransactionResult>(
                    "Bridge real-value movement is disabled (Blockchain:Bridge:RealValueEnabled=false). Simulated-chain routes remain available.");

            var resolvedMode = mode ?? _wormholeConfig.DefaultMode;

            if (resolvedMode == BridgeMode.Wormhole)
                return Error<BridgeTransactionResult>(
                    "Wormhole bridge mode is disabled: executable source-message sequence "
                    + "derivation and launch-safe provider settlement are not implemented.");

            var sourceProvider = _factory.GetProvider(sourceChain, _network);
            var targetProvider = _factory.GetProvider(targetChain, _network);
            if (!sourceProvider.SupportsBridging)
                return Error<BridgeTransactionResult>(
                    $"{sourceChain} does not support the complete bridge lifecycle");
            if (!targetProvider.SupportsBridging)
                return Error<BridgeTransactionResult>(
                    $"{targetChain} does not support the complete bridge lifecycle");

            if (!simulatedRoute)
            {
                var kycFailure = await RequireCurrentKycApprovalAsync(avatarId, ct);
                if (kycFailure is not null)
                    return kycFailure;
            }

            return await InitiateTrustedBridgeAsync(
                sourceChain, targetChain, tokenId, recipientAddress, avatarId,
                amount, ct, clientIdempotencyKey);
        }
        catch (Exception ex)
        {
            return Error<BridgeTransactionResult>($"Bridge initiation failed: {ex.Message}", ex);
        }
    }

    public async Task<AZOAResult<BridgeTransactionResult>> FetchVAAAsync(
        string bridgeTransactionId, CancellationToken ct = default,
        Guid? callerAvatarId = null)
    {
        var tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
        if (tx == null || !CallerOwns(tx, callerAvatarId))
            return Error<BridgeTransactionResult>("Bridge transaction not found");

        if (tx.Mode != BridgeMode.Wormhole)
            return Error<BridgeTransactionResult>("FetchVAA is only available for Wormhole bridges");

        if (tx.Status != BridgeStatus.AwaitingVAA)
        {
            // Idempotent replay: a concurrent fetch already advanced the row past
            // AwaitingVAA and persisted the VAA bytes. Any post-fetch state that
            // carries VAA bytes is an acceptable already-fetched outcome — return
            // success rather than erroring (mirrors the post-save lost-race branch).
            if (!string.IsNullOrWhiteSpace(tx.VaaBytes)
                && tx.Status is BridgeStatus.VAAReady or BridgeStatus.Redeeming
                             or BridgeStatus.Completed or BridgeStatus.Refunded)
            {
                return Ok(tx, $"VAA already fetched (bridge is {tx.Status})");
            }
            return Error<BridgeTransactionResult>($"Bridge is in {tx.Status} state, expected AwaitingVAA");
        }

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
        // A VAA with no bytes or no digest is unusable proof — reject rather than
        // persisting an empty/null proof field that later steps would trust.
        if (string.IsNullOrWhiteSpace(vaa.VaaBytes) || string.IsNullOrWhiteSpace(vaa.Digest))
            return Error<BridgeTransactionResult>(
                "VAA fetch returned incomplete proof (missing VAA bytes or digest) — refusing to persist");

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
        string? clientIdempotencyKey = null,
        Guid? callerAvatarId = null)
    {
        var tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
        if (tx == null || !CallerOwns(tx, callerAvatarId))
            return Error<BridgeTransactionResult>("Bridge transaction not found");

        if (tx.Mode != BridgeMode.Wormhole)
            return Error<BridgeTransactionResult>("Redeem is only available for Wormhole bridges");

        var targetProvider = _factory.GetProvider(tx.TargetChain, _network);
        if (!targetProvider.SupportsBridging)
            return Error<BridgeTransactionResult>(
                $"{tx.TargetChain} cannot safely settle wrapped redemption");

        if (string.IsNullOrWhiteSpace(tx.VaaBytes))
            return Error<BridgeTransactionResult>("No VAA available — call FetchVAA first");

        var simulatedRoute = IsSimulatedRoute(tx.SourceChain, tx.TargetChain);

        // Kill switch: refuse real-value redeem when flag is off and route is non-simulated.
        if (!_bridgeOptions.RealValueEnabled && !simulatedRoute)
            return Error<BridgeTransactionResult>(
                "Bridge real-value movement is disabled (Blockchain:Bridge:RealValueEnabled=false). Simulated-chain routes remain available.");

        if (!simulatedRoute)
        {
            var kycFailure = await RequireCurrentKycApprovalAsync(tx.AvatarId, ct);
            if (kycFailure is not null)
                return kycFailure;
        }

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
        var hasClientIdempotencyKey = !string.IsNullOrWhiteSpace(clientIdempotencyKey);
        var idempotencyKey = !hasClientIdempotencyKey
            ? $"bridge-redeem:{tx.Id}:{vaaDigest}"
            : BuildClientBridgeKey(tx.AvatarId, clientIdempotencyKey!);
        var operationType = hasClientIdempotencyKey
            ? BuildBoundOperationType("bridge-redeem", tx.Id, vaaDigest)
            : "bridge-redeem";

        // ── Step 1: exactly-once claim. Won==false ⇒ a duplicate; never mint. ──
        var claim = await _idempotency.TryClaimAsync(idempotencyKey, operationType, ct);
        if (!claim.Won)
        {
            if (!string.Equals(claim.Record.OperationType, operationType, StringComparison.Ordinal))
                return IdempotencyBindingError();

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
            BridgeTransactionResult? current = await _bridgeStore.GetBridgeAsync(tx.Id, ct);
            if (current != null && (current.Status is BridgeStatus.Redeeming or BridgeStatus.Completed))
            {
                await _idempotency.FailAsync(idempotencyKey,
                    $"Concurrent redeem already advanced bridge to {current.Status}", ct);
                return Error<BridgeTransactionResult>(
                    $"Bridge already being redeemed by a concurrent request (state {current.Status}) — rejected to prevent double-mint");
            }

            var rejectMsg = current != null
                ? $"Bridge is in {current.Status} state, expected VAAReady"
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

        // VaaBytes is proven non-null upstream (RedeemWithVAAAsync guards it and
        // the canonical digest is computed from it); guard defensively so a
        // stale-claim resume path can never construct a VAA with null bytes.
        if (string.IsNullOrWhiteSpace(tx.VaaBytes))
        {
            await FailRedeemAsync(tx.Id, "VAA bytes missing at redeem step", ct);
            await _idempotency.FailAsync(idempotencyKey, "VAA bytes missing at redeem step", ct);
            return Error<BridgeTransactionResult>("VAA bytes missing at redeem step — redeem rejected, no mint performed");
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
        BridgeTransactionResult? finalTx = await _bridgeStore.GetBridgeAsync(tx.Id, ct);
        if (finalTx == null)
            return Error<BridgeTransactionResult>("Bridge transaction vanished after completion");

        _logger.LogInformation(
            "Wormhole bridge completed: {Id} {Source}→{Target} redeemTx={TxHash}",
            finalTx.Id, finalTx.SourceChain, finalTx.TargetChain, redemption.TxHash);

        return Ok(finalTx, $"Wormhole bridge completed trustlessly: {finalTx.SourceChain} → {finalTx.TargetChain}");
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
            case BridgeStatus.Refunded:
                await _idempotency.CompleteAsync(
                    idempotencyKey, row!.ProofData ?? row.RedemptionTxHash ?? string.Empty, ct);
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

    public async Task<AZOAResult<BridgeTransactionResult>> ReverseBridgeAsync(
        string bridgeTransactionId, string sourceRecipientAddress, CancellationToken ct = default,
        string? clientIdempotencyKey = null,
        Guid? callerAvatarId = null)
    {
        var tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
        if (tx == null || !CallerOwns(tx, callerAvatarId))
            return Error<BridgeTransactionResult>("Bridge transaction not found");

        // Only a Completed bridge starts a fresh reversal. Refunded and Reversing
        // are RECOVERY states reachable only through an existing idempotency claim —
        // they must fall through to the stale-claim decision tree below (which settles
        // Refunded as an idempotent success and parks Reversing for reconciliation),
        // NOT be short-circuited here. Anything genuinely non-reversible is rejected now.
        if (tx.Status is not (BridgeStatus.Completed or BridgeStatus.Refunded or BridgeStatus.Reversing))
            return Error<BridgeTransactionResult>("Only completed bridges can be reversed");

        var simulatedRoute = IsSimulatedRoute(tx.SourceChain, tx.TargetChain);

        // Kill switch: refuse real-value reversal when flag is off and route is non-simulated.
        if (!_bridgeOptions.RealValueEnabled && !simulatedRoute)
            return Error<BridgeTransactionResult>(
                "Bridge real-value movement is disabled (Blockchain:Bridge:RealValueEnabled=false). Simulated-chain routes remain available.");

        if (string.IsNullOrWhiteSpace(sourceRecipientAddress))
            return Error<BridgeTransactionResult>("Source recipient address is required for reversal");

        var targetProvider = _factory.GetProvider(tx.TargetChain, _network);
        var sourceProvider = _factory.GetProvider(tx.SourceChain, _network);
        if (!targetProvider.SupportsBridging || !sourceProvider.SupportsBridging)
            return Error<BridgeTransactionResult>(
                "Bridge reversal is unavailable because the route cannot prove both target burn and source release");

        if (!simulatedRoute)
        {
            var kycFailure = await RequireCurrentKycApprovalAsync(tx.AvatarId, ct);
            if (kycFailure is not null)
                return kycFailure;
        }

        // The reversal itself is an irreversible chain effect (burn-wrapped) —
        // gate it so a retried/concurrent reverse cannot double-burn.
        // Client Idempotency-Key wins but is avatar-namespaced; else deterministic key.
        var hasClientIdempotencyKey = !string.IsNullOrWhiteSpace(clientIdempotencyKey);
        var idempotencyKey = !hasClientIdempotencyKey
            ? $"bridge-reverse:{tx.Id}:{sourceRecipientAddress}"
            : BuildClientBridgeKey(tx.AvatarId, clientIdempotencyKey!);
        var operationType = hasClientIdempotencyKey
            ? BuildBoundOperationType("bridge-reverse", tx.Id, sourceRecipientAddress)
            : "bridge-reverse";
        var claim = await _idempotency.TryClaimAsync(idempotencyKey, operationType, ct);
        if (!claim.Won)
        {
            if (!string.Equals(claim.Record.OperationType, operationType, StringComparison.Ordinal))
                return IdempotencyBindingError();

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
                                tx.ProofData ?? tx.RedemptionTxHash ?? string.Empty, ct);
                            return Ok(tx, "Bridge already reversed (stale-claim idempotent replay)");

                        case BridgeStatus.Completed:
                            // Crash before the Completed→Reversing transition: no burn happened.
                            // RESUME under the existing claim into the normal reverse flow.
                            _logger.LogInformation(
                                "Stale reverse claim for bridge {Id}: resuming from Completed under existing claim",
                                tx.Id);
                            break; // falls through to the Completed→Reversing transition below

                        case BridgeStatus.Reversing:
                            // A durable burn/release handle means an irreversible leg may have landed.
                            // Never re-broadcast either leg; reconciliation or an operator must resolve it.
                            const string revAmbigMsg =
                                "reversal outcome ambiguous after crash — parked for reconciliation";
                            _logger.LogWarning(
                                "Stale reverse claim for bridge {Id}: row is Reversing — "
                                + "parked for reconciliation (burn/release outcome requires confirmation)",
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
            if (tx?.Status == BridgeStatus.Refunded)
            {
                // Already reversed (idempotent replay of a fresh claim that found the
                // bridge terminal). Settle the claim we hold to Completed, not Failed.
                await _idempotency.CompleteAsync(
                    idempotencyKey, tx.ProofData ?? tx.RedemptionTxHash ?? string.Empty, ct);
                return Ok(tx, "Bridge already reversed (idempotent replay)");
            }
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

        if (burnResult is { IsError: false } && !string.IsNullOrWhiteSpace(burnResult.Result))
        {
            var burnPersisted = await _bridgeStore.TryTransitionBridgeStatusAsync(
                tx.Id, BridgeStatus.Reversing, BridgeStatus.Reversing,
                new BridgeStatusMutation { RedemptionTxHash = burnResult.Result }, ct);
            if (burnPersisted != 1)
            {
                var persistErr =
                    $"MANUAL INTERVENTION REQUIRED: target burn {burnResult.Result} was submitted "
                    + "but its durable reconciliation handle could not be persisted; source release was not attempted.";
                await _idempotency.FailAsync(idempotencyKey, persistErr, ct);
                return Error<BridgeTransactionResult>(persistErr);
            }

            if (IsPendingSubmission(burnResult))
                return Error<BridgeTransactionResult>(
                    $"Target burn {burnResult.Result} submitted and awaiting positive confirmation; source release not attempted");

            if (string.IsNullOrWhiteSpace(tx.SourceAddress))
            {
                const string missingCustodyError =
                    "Target burn confirmed but the persisted source custody address is missing; source release not attempted";
                await _bridgeStore.TryTransitionBridgeStatusAsync(
                    tx.Id, BridgeStatus.Reversing, BridgeStatus.Reversing,
                    new BridgeStatusMutation { ErrorMessage = missingCustodyError }, ct);
                return Error<BridgeTransactionResult>(missingCustodyError);
            }

            var releaseResult = await sourceProvider.ReleaseFromBridgeAsync(
                tx.SourceTokenId, tx.SourceAddress, tx.Amount, sourceRecipientAddress, ct);

            if (releaseResult.IsError || string.IsNullOrWhiteSpace(releaseResult.Result))
            {
                var releaseError = releaseResult.Message ?? "source release returned no transaction hash";
                _logger.LogError(
                    "Bridge {Id} target burn {BurnTx} confirmed but source release failed: {Error}",
                    tx.Id, burnResult.Result, releaseError);
                var manualReleaseError =
                    $"MANUAL INTERVENTION REQUIRED: target burn confirmed ({burnResult.Result}) but source release failed: {releaseError}";
                await _bridgeStore.TryTransitionBridgeStatusAsync(
                    tx.Id, BridgeStatus.Reversing, BridgeStatus.Reversing,
                    new BridgeStatusMutation { ErrorMessage = manualReleaseError }, ct);
                return Error<BridgeTransactionResult>(manualReleaseError);
            }

            var releasePending = IsPendingSubmission(releaseResult);
            var releasePersisted = await _bridgeStore.TryTransitionBridgeStatusAsync(
                tx.Id, BridgeStatus.Reversing,
                releasePending ? BridgeStatus.Reversing : BridgeStatus.Refunded,
                new BridgeStatusMutation
                {
                    RedemptionTxHash = burnResult.Result,
                    ProofData = releaseResult.Result,
                    SetCompletedAtUtcNow = !releasePending,
                }, ct);

            if (releasePersisted != 1)
            {
                var persistErr =
                    $"MANUAL INTERVENTION REQUIRED: source release {releaseResult.Result} was submitted "
                    + "but its durable reconciliation state could not be persisted.";
                await _idempotency.FailAsync(idempotencyKey, persistErr, ct);
                return Error<BridgeTransactionResult>(persistErr);
            }

            if (releasePending)
                return Error<BridgeTransactionResult>(
                    $"Source release {releaseResult.Result} submitted and awaiting positive confirmation");

            await _idempotency.CompleteAsync(idempotencyKey, releaseResult.Result, ct);
            tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
            return Ok(tx!, "Bridge reversed after confirmed target burn and confirmed source release");
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
                var src = providers[i];
                var tgt = providers[j];
                // Skip same-provider self-routes EXCEPT the simulated self-route:
                // Simulated→Simulated is an always-available sim-only lane and must
                // be emitted so callers can move test value while real value is off.
                if (i == j && !IsSimulatedRoute(src.ChainType, tgt.ChainType)) continue;

                var modes = new List<BridgeMode> { BridgeMode.Trusted };
                const bool wormholeSupported = false;

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
                    RealValueEnabled = src.SupportsBridging && tgt.SupportsBridging
                        && (_bridgeOptions.RealValueEnabled || IsSimulatedRoute(src.ChainType, tgt.ChainType))
                });
            }
        }

        return Ok<IEnumerable<BridgeRouteInfo>>(routes, $"Retrieved {routes.Count} bridge routes");
    }

    public async Task<AZOAResult<BridgeTransactionResult>> GetBridgeStatusAsync(
        string bridgeTransactionId, CancellationToken ct = default,
        Guid? callerAvatarId = null)
    {
        var tx = await _bridgeStore.GetBridgeAsync(bridgeTransactionId, ct);
        if (tx == null || !CallerOwns(tx, callerAvatarId))
            return Error<BridgeTransactionResult>("Bridge transaction not found");

        return Ok(tx, $"Bridge status: {tx.Status} (mode: {tx.Mode})");
    }

    /// <summary>
    /// IDOR guard: internal callers pass null (no scoping); an untrusted caller
    /// passes its authenticated avatar id and may only touch its own rows. A
    /// mismatch is surfaced as "not found" (never a distinct 403) so bridge-id
    /// existence is not leaked across avatars.
    /// </summary>
    private static bool CallerOwns(BridgeTransactionResult tx, Guid? callerAvatarId)
        => !callerAvatarId.HasValue || tx.AvatarId == callerAvatarId.Value;

    // ─── Private: Trusted (custodial) flow ───

    private async Task<AZOAResult<BridgeTransactionResult>> InitiateTrustedBridgeAsync(
        string sourceChain, string targetChain, string tokenId,
        string recipientAddress, Guid avatarId, ulong amount,
        CancellationToken ct, string? clientIdempotencyKey = null)
    {
        var sourceProvider = _factory.GetProvider(sourceChain, _network);
        var targetProvider = _factory.GetProvider(targetChain, _network);

        if (!sourceProvider.SupportsBridging)
            return Error<BridgeTransactionResult>($"{sourceChain} does not support bridging");
        if (!targetProvider.SupportsBridging)
            return Error<BridgeTransactionResult>($"{targetChain} does not support bridging");

        // Deterministic idempotency key for the trusted lock→mint pair: identical
        // bridge requests (same avatar/route/token/recipient/amount) collapse to
        // a single irreversible chain effect under duplicate/concurrent calls.
        // Client Idempotency-Key wins but is avatar-namespaced; else the deterministic key.
        var hasClientIdempotencyKey = !string.IsNullOrWhiteSpace(clientIdempotencyKey);
        var idempotencyKey = !hasClientIdempotencyKey
            ? $"bridge-trusted:{avatarId:N}:{sourceChain}:{targetChain}:{tokenId}:{recipientAddress}:{amount}"
            : BuildClientBridgeKey(avatarId, clientIdempotencyKey!);
        var operationType = hasClientIdempotencyKey
            ? BuildBoundOperationType(
                "bridge-trusted",
                sourceChain,
                targetChain,
                tokenId,
                recipientAddress,
                amount.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : "bridge-trusted";

        var bridgeVault = GetBridgeVaultAddress(sourceChain, targetChain);
        if (string.IsNullOrWhiteSpace(bridgeVault))
            return Error<BridgeTransactionResult>(
                "No server-controlled bridge vault is configured for this real-value source chain");

        // Bind a client key before creating a bridge row. This prevents a key
        // already owned by redeem/reverse (or a different trusted request) from
        // leaving an unowned Initiated row in the reconciliation queue.
        var existingClaim = await _idempotency.GetAsync(idempotencyKey, ct);
        if (existingClaim is not null
            && !string.Equals(existingClaim.OperationType, operationType, StringComparison.Ordinal))
        {
            return IdempotencyBindingError();
        }

        if (existingClaim?.State == IdempotencyState.Completed)
        {
            var prior = await _bridgeStore.GetBridgeByIdempotencyKeyAsync(idempotencyKey, ct);
            if (prior is not null)
            {
                return MatchesTrustedRequest(
                        prior, sourceChain, targetChain, tokenId, recipientAddress,
                        avatarId, amount, bridgeVault)
                    ? Ok(prior, $"Trusted bridge already completed (idempotent replay): {sourceChain} → {targetChain}")
                    : IdempotencyBindingError();
            }

            return Error<BridgeTransactionResult>(
                $"Trusted bridge already completed (idempotent replay): mint={existingClaim.ResultPayload}");
        }

        if (existingClaim?.State == IdempotencyState.Failed)
        {
            return Error<BridgeTransactionResult>(
                $"Trusted bridge already failed (idempotent replay): {existingClaim.Error}");
        }

        if (existingClaim?.State == IdempotencyState.InProgress
            && (DateTime.UtcNow - existingClaim.CreatedAt).TotalSeconds
                < _bridgeOptions.StaleClaimTakeoverSeconds)
        {
            return Error<BridgeTransactionResult>(
                "Trusted bridge already in progress for this request — rejected to prevent double-mint");
        }

        var candidate = new BridgeTransactionResult
        {
            Id = $"bridge_{IdempotencyReplay.ContentHash(idempotencyKey)[..32]}",
            AvatarId = avatarId,
            SourceChain = sourceChain,
            TargetChain = targetChain,
            SourceTokenId = tokenId,
            SourceAddress = bridgeVault,
            TargetAddress = recipientAddress,
            Amount = amount,
            Mode = BridgeMode.Trusted,
            Status = BridgeStatus.Initiated,
            IdempotencyKey = idempotencyKey,
            CreatedAt = DateTime.UtcNow,
            Network = _network,
        };
        var tracking = await GetOrCreateTrustedTrackingAsync(candidate, ct);
        if (tracking.IsError || tracking.Result is null)
            return tracking;
        var bridgeTx = tracking.Result;
        if (!MatchesTrustedRequest(
                bridgeTx,
                sourceChain,
                targetChain,
                tokenId,
                recipientAddress,
                avatarId,
                amount,
                bridgeVault))
        {
            return IdempotencyBindingError();
        }

        var claim = await _idempotency.TryClaimAsync(idempotencyKey, operationType, ct);
        if (!claim.Won)
        {
            if (!string.Equals(claim.Record.OperationType, operationType, StringComparison.Ordinal))
            {
                await _bridgeStore.TryTransitionBridgeStatusAsync(
                    bridgeTx.Id, BridgeStatus.Initiated, BridgeStatus.Failed,
                    new BridgeStatusMutation
                    {
                        ErrorMessage = "Idempotency key became bound to another operation before trusted bridge execution."
                    }, ct);
                return IdempotencyBindingError();
            }

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
                case IdempotencyState.InProgress when
                    (DateTime.UtcNow - claim.Record.CreatedAt).TotalSeconds
                        < _bridgeOptions.StaleClaimTakeoverSeconds:
                    return Error<BridgeTransactionResult>(
                        "Trusted bridge already in progress for this request — rejected to prevent double-mint");
            }
        }

        // A stale claim is recoverable only from a durable phase whose next
        // side effect has not started. Locking and Redeeming are deliberately
        // ambiguous reservations: never re-broadcast from either state.
        bridgeTx = await _bridgeStore.GetBridgeAsync(bridgeTx.Id, ct) ?? bridgeTx;
        switch (bridgeTx.Status)
        {
            case BridgeStatus.Completed:
                await _idempotency.CompleteAsync(
                    idempotencyKey, bridgeTx.MintTxHash ?? bridgeTx.TargetTokenId ?? string.Empty, ct);
                return Ok(bridgeTx, $"Trusted bridge already completed (idempotent replay): {sourceChain} → {targetChain}");
            case BridgeStatus.Failed:
                await _idempotency.FailAsync(
                    idempotencyKey, bridgeTx.ErrorMessage ?? "trusted bridge failed", ct);
                return Error<BridgeTransactionResult>(
                    $"Trusted bridge already failed: {bridgeTx.ErrorMessage ?? "unknown failure"}");
            case BridgeStatus.Locked when string.IsNullOrWhiteSpace(bridgeTx.MintTxHash):
                if (string.IsNullOrWhiteSpace(bridgeTx.LockTxHash))
                    return Error<BridgeTransactionResult>(
                        "Trusted bridge is Locked without a durable source transaction hash; manual reconciliation is required.");
                return await MintTrustedLockedBridgeAsync(
                    bridgeTx, targetProvider, idempotencyKey, ct);
            case BridgeStatus.Locking:
                return Error<BridgeTransactionResult>(
                    "Trusted bridge lock outcome is ambiguous after an interrupted attempt; reconciliation is required.");
            case BridgeStatus.Locked:
            case BridgeStatus.Redeeming:
                return Error<BridgeTransactionResult>(
                    "Trusted bridge target mint is already submitted or its outcome is ambiguous; reconciliation is required.");
            case BridgeStatus.Initiated:
                break;
            default:
                return Error<BridgeTransactionResult>(
                    $"Trusted bridge cannot resume from state {bridgeTx.Status}.");
        }

        // Persist a tracking row BEFORE the irreversible lock so a crash between
        // lock and mint is recoverable (orphan sweep), and the lock can never
        // happen without a durable record.
        var lockReserved = await _bridgeStore.TryTransitionBridgeStatusAsync(
            bridgeTx.Id,
            BridgeStatus.Initiated,
            BridgeStatus.Locking,
            null,
            ct);
        if (lockReserved != 1)
        {
            var current = await _bridgeStore.GetBridgeAsync(bridgeTx.Id, ct);
            return Error<BridgeTransactionResult>(
                current?.Status == BridgeStatus.Locking
                    ? "Trusted bridge lock outcome is ambiguous after an interrupted attempt; reconciliation is required."
                    : $"Trusted bridge could not reserve the source lock from state {current?.Status.ToString() ?? "MISSING"}.");
        }

        var lockResult = await sourceProvider.LockForBridgeAsync(
            tokenId, bridgeVault, amount, targetChain, recipientAddress, ct);

        if (lockResult.IsError)
        {
            var lockErr = $"Source chain lock failed: {lockResult.Message}";
            await _bridgeStore.TryTransitionBridgeStatusAsync(
                bridgeTx.Id, BridgeStatus.Locking, BridgeStatus.Failed,
                new BridgeStatusMutation { ErrorMessage = lockErr }, ct);
            await _idempotency.FailAsync(idempotencyKey, lockErr, ct);
            return Error<BridgeTransactionResult>(lockErr);
        }

        if (string.IsNullOrWhiteSpace(lockResult.Result))
        {
            var lockErr = "Source chain lock returned no transaction hash";
            await _bridgeStore.TryTransitionBridgeStatusAsync(
                bridgeTx.Id, BridgeStatus.Locking, BridgeStatus.Failed,
                new BridgeStatusMutation { ErrorMessage = lockErr }, ct);
            await _idempotency.FailAsync(idempotencyKey, lockErr, ct);
            return Error<BridgeTransactionResult>(lockErr);
        }

        var lockPending = IsPendingSubmission(lockResult);
        var lockPersisted = await _bridgeStore.TryTransitionBridgeStatusAsync(
            bridgeTx.Id, BridgeStatus.Locking,
            lockPending ? BridgeStatus.Locking : BridgeStatus.Locked,
            new BridgeStatusMutation
            {
                LockTxHash = lockResult.Result,
                SourceAddress = bridgeVault,
            }, ct);

        if (lockPersisted != 1)
        {
            var persistErr =
                $"MANUAL INTERVENTION REQUIRED: source lock {lockResult.Result} was submitted "
                + "but its durable bridge state could not be persisted; target mint was not attempted.";
            await _idempotency.FailAsync(idempotencyKey, persistErr, ct);
            return Error<BridgeTransactionResult>(persistErr);
        }

        if (lockPending)
        {
            var pending = await _bridgeStore.GetBridgeAsync(bridgeTx.Id, ct);
            return Ok(pending!,
                $"Source lock {lockResult.Result} submitted and awaiting positive chain confirmation; target mint not attempted");
        }

        var locked = await _bridgeStore.GetBridgeAsync(bridgeTx.Id, ct);
        if (locked is null)
            return Error<BridgeTransactionResult>(
                "Bridge transaction vanished after the source lock was persisted.");

        return await MintTrustedLockedBridgeAsync(locked, targetProvider, idempotencyKey, ct);
    }

    private async Task<AZOAResult<BridgeTransactionResult>> MintTrustedLockedBridgeAsync(
        BridgeTransactionResult bridgeTx,
        IBlockchainProvider targetProvider,
        string idempotencyKey,
        CancellationToken ct)
    {
        var mintReserved = await _bridgeStore.TryTransitionBridgeStatusAsync(
            bridgeTx.Id, BridgeStatus.Locked, BridgeStatus.Redeeming, null, ct);
        if (mintReserved != 1)
        {
            var current = await _bridgeStore.GetBridgeAsync(bridgeTx.Id, ct);
            if (current?.Status == BridgeStatus.Completed)
            {
                await _idempotency.CompleteAsync(
                    idempotencyKey, current.MintTxHash ?? current.TargetTokenId ?? string.Empty, ct);
                return Ok(current, $"Trusted bridge already completed (idempotent replay): {current.SourceChain} → {current.TargetChain}");
            }

            return Error<BridgeTransactionResult>(
                current?.Status is BridgeStatus.Redeeming or BridgeStatus.Locked
                    ? "Trusted bridge target mint is already reserved or submitted; reconciliation is required."
                    : $"Trusted bridge could not reserve the target mint from state {current?.Status.ToString() ?? "MISSING"}.");
        }

        var mintResult = await targetProvider.MintWrappedAsync(
            bridgeTx.SourceChain, bridgeTx.SourceTokenId,
            $"bridge://{bridgeTx.SourceChain}/{bridgeTx.SourceTokenId}",
            bridgeTx.Amount, bridgeTx.TargetAddress, ct);

        if (mintResult.IsError)
        {
            // Funds are locked on source but mint failed on target: compensation
            // required. Mark Failed with an explicit manual-intervention message
            // so the reconciliation sweep/runbook can release the locked asset.
            var mintErr =
                $"MANUAL INTERVENTION REQUIRED: trusted bridge {bridgeTx.Id} locked source asset " +
                $"(lockTx={bridgeTx.LockTxHash}) but target mint failed: {mintResult.Message}. " +
                $"Release/refund the locked {bridgeTx.SourceTokenId} on {bridgeTx.SourceChain} or retry the mint.";
            await _bridgeStore.TryTransitionBridgeStatusAsync(
                bridgeTx.Id, BridgeStatus.Redeeming, BridgeStatus.Failed,
                new BridgeStatusMutation { ErrorMessage = mintErr }, ct);
            await _idempotency.FailAsync(idempotencyKey, mintErr, ct);
            _logger.LogError(
                "Trusted bridge mint FAILED after successful lock: {Id} {Source}→{Target} lockTx={LockTx}: {Message}",
                bridgeTx.Id, bridgeTx.SourceChain, bridgeTx.TargetChain,
                bridgeTx.LockTxHash, mintResult.Message);
            return Error<BridgeTransactionResult>(mintErr);
        }

        if (string.IsNullOrWhiteSpace(mintResult.Result))
        {
            var mintErr =
                $"MANUAL INTERVENTION REQUIRED: trusted bridge {bridgeTx.Id} locked source asset "
                + $"(lockTx={bridgeTx.LockTxHash}) but target mint returned no transaction reference.";
            await _bridgeStore.TryTransitionBridgeStatusAsync(
                bridgeTx.Id, BridgeStatus.Redeeming, BridgeStatus.Failed,
                new BridgeStatusMutation { ErrorMessage = mintErr }, ct);
            await _idempotency.FailAsync(idempotencyKey, mintErr, ct);
            return Error<BridgeTransactionResult>(mintErr);
        }

        if (IsPendingSubmission(mintResult))
        {
            var mintPersisted = await _bridgeStore.TryTransitionBridgeStatusAsync(
                bridgeTx.Id, BridgeStatus.Redeeming, BridgeStatus.Redeeming,
                new BridgeStatusMutation { MintTxHash = mintResult.Result }, ct);
            if (mintPersisted != 1)
            {
                var persistErr =
                    $"MANUAL INTERVENTION REQUIRED: target mint {mintResult.Result} was submitted "
                    + "but its durable reconciliation handle could not be persisted.";
                await _idempotency.FailAsync(idempotencyKey, persistErr, ct);
                return Error<BridgeTransactionResult>(persistErr);
            }

            var pending = await _bridgeStore.GetBridgeAsync(bridgeTx.Id, ct);
            return Ok(pending!,
                $"Target mint {mintResult.Result} submitted and awaiting positive chain confirmation");
        }

        // Lock + mint both positively confirmed: atomically stamp Completed.
        var completed = await _bridgeStore.TryTransitionBridgeStatusAsync(
            bridgeTx.Id, BridgeStatus.Redeeming, BridgeStatus.Completed,
            new BridgeStatusMutation
            {
                TargetTokenId = mintResult.Result,
                MintTxHash = mintResult.Result,
                SetCompletedAtUtcNow = true,
            }, ct);

        if (completed != 1)
        {
            await SettleIdempotencyToBridgeStateAsync(
                bridgeTx.Id, idempotencyKey, $"target mint {mintResult.Result}", ct);
            return Error<BridgeTransactionResult>(
                "Target mint confirmed but the bridge completion state was not persisted; manual reconciliation required");
        }

        await _idempotency.CompleteAsync(idempotencyKey, mintResult.Result, ct);

        // Re-fetch to return the updated snapshot.
        var updated = await _bridgeStore.GetBridgeAsync(bridgeTx.Id, ct);
        if (updated == null)
            return Error<BridgeTransactionResult>("Bridge transaction vanished after completion");

        _logger.LogInformation(
            "Trusted bridge completed: {Id} {Source}→{Target} token={TokenId} amount={Amount}",
            bridgeTx.Id, bridgeTx.SourceChain, bridgeTx.TargetChain,
            bridgeTx.SourceTokenId, bridgeTx.Amount);

        return Ok(updated, $"Trusted bridge completed: {bridgeTx.SourceChain} → {bridgeTx.TargetChain}");
    }

    private async Task<AZOAResult<BridgeTransactionResult>> GetOrCreateTrustedTrackingAsync(
        BridgeTransactionResult candidate,
        CancellationToken ct)
    {
        var existing = await _bridgeStore.GetBridgeByIdempotencyKeyAsync(candidate.IdempotencyKey!, ct);
        if (existing is not null)
            return Ok(existing);

        try
        {
            await _bridgeStore.AddBridgeAsync(candidate, ct);
            return Ok(candidate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            existing = await _bridgeStore.GetBridgeByIdempotencyKeyAsync(candidate.IdempotencyKey!, ct);
            return existing is not null
                ? Ok(existing)
                : Error<BridgeTransactionResult>(
                    "Trusted bridge tracking could not be persisted before the source lock.",
                    ex);
        }
    }

    private bool MatchesTrustedRequest(
        BridgeTransactionResult transaction,
        string sourceChain,
        string targetChain,
        string tokenId,
        string recipientAddress,
        Guid avatarId,
        ulong amount,
        string bridgeVault)
        => transaction.AvatarId == avatarId
           && transaction.Amount == amount
           && transaction.Mode == BridgeMode.Trusted
           && transaction.Network == _network
           && string.Equals(transaction.SourceChain, sourceChain, StringComparison.Ordinal)
           && string.Equals(transaction.TargetChain, targetChain, StringComparison.Ordinal)
           && string.Equals(transaction.SourceTokenId, tokenId, StringComparison.Ordinal)
           && string.Equals(transaction.TargetAddress, recipientAddress, StringComparison.Ordinal)
           && string.Equals(transaction.SourceAddress, bridgeVault, StringComparison.Ordinal);

    private static string BuildClientBridgeKey(Guid avatarId, string clientKey)
        => $"bridge-client:{avatarId:N}:{IdempotencyReplay.ContentHash(clientKey.Trim())}";

    private static string BuildBoundOperationType(string operation, params string[] requestFields)
    {
        var canonical = string.Join(
            ".",
            requestFields.Select(field => Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(field))));
        return $"{operation}:{IdempotencyReplay.ContentHash(canonical)[..48]}";
    }

    private AZOAResult<BridgeTransactionResult> IdempotencyBindingError()
        => Error<BridgeTransactionResult>(
            "Idempotency-Key is already bound to a different bridge operation or request.");

    private string? GetBridgeVaultAddress(string sourceChain, string targetChain)
    {
        if (_wormholeConfig.BridgeVaults.TryGetValue(sourceChain, out var vaultCfg)
            && !string.IsNullOrWhiteSpace(vaultCfg.VaultAddress))
        {
            return vaultCfg.VaultAddress;
        }

        if (IsSimulatedRoute(sourceChain, targetChain))
            return $"simulated_bridge_vault_for_{targetChain.ToLowerInvariant()}";

        _logger.LogError(
            "No server-controlled bridge vault configured for real-value source chain {Chain}",
            sourceChain);
        return null;
    }

    private AZOAResult<T> Ok<T>(T result, string message = "")
        => new() { IsError = false, Result = result, Message = message };

    private AZOAResult<T> Error<T>(string message, Exception? ex = null)
    {
        _logger.LogError(ex, "Bridge error: {Message}", message);
        return new AZOAResult<T> { IsError = true, Message = message, Exception = ex };
    }

    private async Task<AZOAResult<BridgeTransactionResult>?> RequireCurrentKycApprovalAsync(
        Guid avatarId,
        CancellationToken ct)
    {
        var gate = await _realValueKycGate.RequireCurrentApprovalAsync(avatarId, ct);
        if (!gate.IsError && gate.Result)
            return null;

        var message = string.IsNullOrWhiteSpace(gate.Message)
            ? "KYC authorization could not be confirmed for this real-value operation."
            : gate.Message;
        return Error<BridgeTransactionResult>(message, gate.Exception);
    }

    private static bool IsPendingSubmission(AZOAResult<string> result) =>
        result.Message?.StartsWith(
            OperationStatus.PendingConfirmationMarker,
            StringComparison.OrdinalIgnoreCase) == true;

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
