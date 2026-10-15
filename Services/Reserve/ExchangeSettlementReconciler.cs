// SPDX-License-Identifier: UNLICENSED
//
// OI-33, both halves. An exchange group the rail ACCEPTS is not an exchange that happened, so
// ReserveExchangeManager stops short of two things and leaves them to chain truth: it does not
// credit the project, and it does not unwind. This file is what chain truth then does about it.
// Without it "we credit only on confirmation" is a refusal with no completion, and "an
// accepted-then-dropped group is compensated" is true only for the seconds the request is still
// on the wire.
//
// Everything here is irreversible and everything here is re-entered: a sweep calls back in
// whenever a previous pass returned false, and the commonest reason a pass returns false is that
// the effect LANDED and the bookkeeping after it did not. The primitives underneath dedupe on a
// BOUNDED window (128 keys, both of them, evicted oldest-first) and both say in as many words
// that a caller must keep its own anchor. So each effect below is claimed in the unbounded
// insert-wins idempotency ledger first — see OnceAsync — and a window is never the thing standing
// between a project and a second credit.
//
// See Services/Reserve/AGENTS.md §exchange-settlement.

using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Idempotency;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Hold;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>
/// Finishes an exchange whose fate was only settled later, from the operation row alone.
/// </summary>
/// <remarks>
/// Reconciliation owns the chain probe and the status transition; it does not know what an
/// exchange owes anybody. This seam is that knowledge, kept beside
/// <see cref="ReserveExchangeManager"/> so the row's parameter names have one owner.
/// </remarks>
public interface IExchangeSettlementReconciler
{
    /// <summary>True when this operation is one of ours.</summary>
    bool Owns(IBlockchainOperation operation);

    /// <summary>
    /// Applies whatever a CHAIN-CONFIRMED exchange still owes — today, the project treasury credit
    /// a buy deliberately withheld until confirmation.
    /// </summary>
    /// <returns>
    /// True when nothing is outstanding (either there never was, or it has just been applied).
    /// False when something is outstanding and could NOT be applied: the caller must not advance
    /// the operation to a terminal success, because doing so settles the replay anchor for a
    /// credit that never landed.
    /// </returns>
    Task<bool> SettleConfirmedAsync(IBlockchainOperation operation, CancellationToken ct = default);

    /// <summary>
    /// Hands back every node-side commitment a group left standing once the chain reports it
    /// FAILED. Reached only for a group the rail accepted, because a group refused in-request is
    /// unwound in-request and never persists an operation row.
    /// </summary>
    /// <returns>True when nothing is left standing; false when an operator has to finish it.</returns>
    Task<bool> CompensateFailedAsync(IBlockchainOperation operation, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ExchangeSettlementReconciler : IExchangeSettlementReconciler
{
    private readonly IProjectTreasury _treasury;
    private readonly IPlatformReserve _reserve;
    private readonly IPlatformHold _holds;
    private readonly IBlockchainOperationStore _operations;
    private readonly IIdempotencyStore _anchors;
    private readonly ILogger<ExchangeSettlementReconciler>? _logger;

    public ExchangeSettlementReconciler(
        IProjectTreasury treasury,
        IPlatformReserve reserve,
        IPlatformHold holds,
        IBlockchainOperationStore operations,
        IIdempotencyStore anchors,
        ILogger<ExchangeSettlementReconciler>? logger = null)
    {
        _treasury = treasury ?? throw new ArgumentNullException(nameof(treasury));
        _reserve = reserve ?? throw new ArgumentNullException(nameof(reserve));
        _holds = holds ?? throw new ArgumentNullException(nameof(holds));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        // Not optional. A reconciler with no anchor is the defect this parameter exists to end:
        // the only marker it had was the flag on the row, and the failure it is written to
        // survive is the one that leaves that flag stale.
        _anchors = anchors ?? throw new ArgumentNullException(nameof(anchors));
        _logger = logger;
    }

    /// <inheritdoc />
    public bool Owns(IBlockchainOperation operation) =>
        operation is not null
        && string.Equals(
            operation.OperationType,
            ReserveExchangeManager.ChainOperationType,
            StringComparison.Ordinal);

    /// <inheritdoc />
    public async Task<bool> SettleConfirmedAsync(
        IBlockchainOperation operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!Owns(operation))
            return true;

        // "true" and absent both mean nothing is owed: absent is a sell, which credits no project.
        if (!operation.Parameters.TryGetValue(
                ReserveExchangeManager.ProjectCreditAppliedParameter, out var applied)
            || !string.Equals(applied, "false", StringComparison.Ordinal))
        {
            return true;
        }

        if (!TryReadLegs(operation, out var legs, out var missing))
        {
            _logger?.LogError(
                "MANUAL INTERVENTION REQUIRED — exchange {OperationId} is chain-confirmed and still "
                + "owes its project a credit, but the row does not say {Missing}. Refusing to report "
                + "it Completed: a settled replay anchor over an unmade credit is a project the node "
                + "presents as funded when it is not.",
                operation.Id, missing);
            return false;
        }

        // The credit still carries the SAME treasury ledger key the in-request credit would have
        // used, so an in-request credit whose flag was never flipped is a replay there. But that
        // window is BOUNDED — ProjectTreasuryService.MovementReplayWindowSize is 128 keys, evicted
        // oldest-first — and outside it a repeated key is unknown and the movement APPLIES. The
        // exact failure this method exists to survive is the one that leaves the row's flag stale:
        // the credit lands, the UpsertAsync below fails, the row still says "false", and the next
        // sweep re-enters here. With 128 other movements on that project in between, a bounded
        // window credits the project TWICE for one exchange. So the reconciler keeps its own
        // anchor, in the unbounded insert-wins ledger, and never leans on somebody else's window.
        var credit = await OnceAsync(
            SettlementAnchor(legs.LedgerKey, ReconcileCreditAnchorSuffix),
            operation.Id,
            "the project credit",
            async () =>
            {
                var credited = await _treasury.RecordMovementAsync(
                    new ProjectTreasuryMovementRequest(
                        new ProjectTreasuryReference(legs.ChainType, legs.TenantId, legs.ProjectReference),
                        legs.PlatformAssetId,
                        ProjectTreasuryMovementKind.Credit,
                        legs.PlatformAmount,
                        legs.LedgerKey),
                    ct);

                if (!credited.IsError)
                    return true;

                _logger?.LogError(
                    "MANUAL INTERVENTION REQUIRED — exchange {OperationId} is chain-confirmed but "
                    + "its project credit could not be recorded: {Detail}. NOT advancing it.",
                    operation.Id, credited.Message);
                return false;
            },
            ct);

        if (credit == SettlementEffect.Refused)
            return false;

        // The flag is the read side two surfaces consult, so it has to move with the money. A row
        // that credited and still says "false" reports AwaitingReconciliation forever.
        operation.Parameters[ReserveExchangeManager.ProjectCreditAppliedParameter] = "true";
        RestateReplayPayloadCredit(operation, legs.PlatformAmount);

        var persisted = await _operations.UpsertAsync(operation, ct);
        if (persisted.IsError)
        {
            _logger?.LogError(
                "Exchange {OperationId} had its project credit recorded but the row still says the "
                + "credit is outstanding: {Detail}. The next sweep re-enters, finds this "
                + "settlement's own anchor already Completed, and flips the flag WITHOUT crediting "
                + "again.",
                operation.Id, persisted.Message);
            return false;
        }

        _logger?.LogInformation(
            "Exchange {OperationId} was chain-confirmed and its project credit of {Amount} base "
            + "units has now been recorded against {Project}.",
            operation.Id, legs.PlatformAmount, legs.ProjectReference);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> CompensateFailedAsync(
        IBlockchainOperation operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!Owns(operation))
            return true;

        if (!TryReadLegs(operation, out var legs, out var missing))
        {
            _logger?.LogError(
                "MANUAL INTERVENTION REQUIRED — exchange {OperationId} failed on chain and its "
                + "node-side commitments are still standing, but the row does not say {Missing}. "
                + "Nothing was handed back; a guessed unwind would return units nobody took.",
                operation.Id, missing);
            return false;
        }

        var kind = operation.Parameters.GetValueOrDefault(
            ReserveExchangeManager.CommitmentKindParameter, string.Empty);

        // A row written before this parameter existed names no commitment. Refusing to guess is
        // the whole point: an unwound PartitionIssue that was really a Hold hands the partition
        // units the hold primitive still holds.
        if (string.IsNullOrWhiteSpace(kind))
        {
            _logger?.LogError(
                "MANUAL INTERVENTION REQUIRED — exchange {OperationId} failed on chain but its row "
                + "does not name which commitment it took, so nothing was handed back.",
                operation.Id);
            return false;
        }

        // The unwind rides PlatformReserveService's replay window, bounded exactly as the
        // treasury's is (DrawReplayWindowSize = 128). This method is re-entered whenever ANY later
        // step in it returns false — the treasury refund below, most obviously — so a bounded
        // window is not enough on its own: 128 draws later the partition would be handed back a
        // second time, on top of a group that never happened. The anchor is what makes it once.
        var handedBack = await OnceAsync(
            SettlementAnchor(legs.LedgerKey, ReconcileUnwindAnchorSuffix),
            operation.Id,
            "the " + kind + " unwind",
            () => kind switch
            {
                ReserveExchangeManager.CommitmentKinds.PartitionIssue =>
                    ReturnToPartitionAsync(legs, ct),
                ReserveExchangeManager.CommitmentKinds.PartitionReturn =>
                    ReIssueFromPartitionAsync(legs, ct),
                ReserveExchangeManager.CommitmentKinds.Hold =>
                    RefundHoldAsync(operation, legs, ct),
                _ => Task.FromResult(false),
            },
            ct);

        if (handedBack == SettlementEffect.Refused)
        {
            _logger?.LogError(
                "MANUAL INTERVENTION REQUIRED — exchange {OperationId} failed on chain and its "
                + "'{Kind}' commitment could not be handed back. The partition stays drawn until an "
                + "operator releases it.",
                operation.Id, kind);
            return false;
        }

        // Only a SELL spends the project's treasury up front, and only then is there anything to
        // put back. The same ledger key as the spend makes a repeat a replay.
        if (string.Equals(
                operation.Parameters.GetValueOrDefault(
                    ReserveExchangeManager.CommitmentTreasurySpentParameter, "false"),
                "true",
                StringComparison.Ordinal))
        {
            var refund = await OnceAsync(
                SettlementAnchor(legs.LedgerKey, ReconcileRefundAnchorSuffix),
                operation.Id,
                "the treasury refund",
                async () =>
                {
                    var refunded = await _treasury.RecordMovementAsync(
                        new ProjectTreasuryMovementRequest(
                            new ProjectTreasuryReference(legs.ChainType, legs.TenantId, legs.ProjectReference),
                            legs.PlatformAssetId,
                            ProjectTreasuryMovementKind.Credit,
                            legs.PlatformAmount,
                            legs.LedgerKey + CompensationSuffix),
                        ct);

                    if (!refunded.IsError)
                        return true;

                    _logger?.LogError(
                        "MANUAL INTERVENTION REQUIRED — exchange {OperationId} failed on chain, its "
                        + "partition was handed back, but the project's treasury is still short "
                        + "{Amount} base units: {Detail}.",
                        operation.Id, legs.PlatformAmount, refunded.Message);
                    return false;
                },
                ct);

            if (refund == SettlementEffect.Refused)
                return false;
        }

        _logger?.LogWarning(
            "Exchange {OperationId} failed on chain after the rail had accepted it; every commitment "
            + "it had taken has been handed back.",
            operation.Id);

        return true;
    }

    /// <summary>The key suffix a compensating draw carries, matching the in-request unwind.</summary>
    private const string CompensationSuffix = ":compensate";

    // ── The reconciler's own anchors ─────────────────────────────────────────
    //
    // Every irreversible thing this file does rides somebody else's replay window, and every one
    // of those windows is BOUNDED: ProjectTreasuryService.MovementReplayWindowSize and
    // PlatformReserveService.DrawReplayWindowSize are both 128 keys, evicted oldest-first, and
    // both services say in as many words that a caller must keep its own anchor because outside
    // the window a repeated key is unknown and the effect APPLIES. This reconciler is re-entered
    // by a sweep precisely when a previous pass returned false, and the most ordinary reason it
    // returns false is that the effect LANDED and the bookkeeping after it did not. So the anchor
    // has to be the unbounded, insert-wins idempotency ledger, one key per effect.

    /// <summary>
    /// Reserves the settlement key space away from callers. An exchange's ledger key is
    /// <c>exchange:{apiKeyId}:{clientIdempotencyKey}</c> (ReserveExchangeManager.CreateFromClientKey),
    /// so before this prefix existed a caller who sent <c>Idempotency-Key: foo:reconcile-credit</c>
    /// produced, byte for byte, the anchor of the exchange whose client key was <c>foo</c>. The
    /// idempotency ledger guarantees uniqueness on the KEY ALONE (Models/Idempotency/IdempotencyRecord.cs),
    /// so that row is the row this reconciler then reads: it sees Completed, reports the effect
    /// already applied, and silently skips a credit, an unwind or a refund that never happened.
    /// Every anchor is prefixed instead of suffixed because a caller controls only the TAIL of its
    /// own ledger key, never the head -- so no caller-reachable key can begin with this.
    /// </summary>
    public const string SettlementAnchorPrefix = "settlement:";

    /// <summary>Anchors the project credit a confirmed buy still owed.</summary>
    public const string ReconcileCreditAnchorSuffix = ":reconcile-credit";

    /// <summary>Anchors the partition/hold hand-back a chain-failed group left standing.</summary>
    public const string ReconcileUnwindAnchorSuffix = ":reconcile-unwind";

    /// <summary>Anchors the treasury refund a chain-failed SELL is owed.</summary>
    public const string ReconcileRefundAnchorSuffix = ":reconcile-refund";

    /// <summary>
    /// The ledger's operation type for every anchor above. Distinct from
    /// <see cref="ReserveExchangeManager.OperationType"/> on purpose: the receipt surface refuses
    /// a ledger row whose operation type is not the exchange's own, so a settlement anchor can
    /// never be mistaken for — or replayed as — a caller's receipt.
    /// </summary>
    public const string AnchorOperationType = "reserve_exchange_settlement";

    private async Task<bool> ReturnToPartitionAsync(ExchangeLegs legs, CancellationToken ct)
    {
        var returned = await _reserve.DrawAsync(
            new ReserveDrawRequest(
                legs.ChainType,
                legs.ProjectAssetId,
                legs.Partition,
                ReserveDrawKind.Return,
                legs.ProjectUnitAmount,
                legs.LedgerKey + CompensationSuffix,
                ReserveDrawAuthority.ForTenant(legs.TenantId)),
            ct);

        return !returned.IsError;
    }

    private async Task<bool> ReIssueFromPartitionAsync(ExchangeLegs legs, CancellationToken ct)
    {
        var issued = await _reserve.DrawAsync(
            new ReserveDrawRequest(
                legs.ChainType,
                legs.ProjectAssetId,
                legs.Partition,
                ReserveDrawKind.Issue,
                legs.ProjectUnitAmount,
                legs.LedgerKey + CompensationSuffix,
                ReserveDrawAuthority.ForTenant(legs.TenantId)),
            ct);

        return !issued.IsError;
    }

    private async Task<bool> RefundHoldAsync(
        IBlockchainOperation operation, ExchangeLegs legs, CancellationToken ct)
    {
        var holdId = operation.Parameters.GetValueOrDefault(
            ReserveExchangeManager.CommitmentHoldIdParameter, string.Empty);
        if (string.IsNullOrWhiteSpace(holdId))
            return false;

        var refunded = await _holds.RefundAsync(
            new HoldSettlementRequest(
                new HoldReference(legs.ChainType, holdId),
                legs.TenantId,
                "The exchange did not settle on chain."),
            ct);

        return !refunded.IsError;
    }

    /// <summary>How many attempt generations one effect may burn before an operator is needed.</summary>
    /// <remarks>
    /// Bounded because each generation is a real ledger row and a real retry of an irreversible
    /// effect. An effect that has failed cleanly this many times is not having a bad minute.
    /// </remarks>
    private const int MaxSettlementAttempts = 8;

    /// <summary>What an anchored effect did, from the ledger's point of view.</summary>
    private enum SettlementEffect
    {
        /// <summary>It ran now and landed.</summary>
        Applied,

        /// <summary>A previous pass ran it and the ledger recorded that it landed. Not re-run.</summary>
        AlreadyApplied,

        /// <summary>
        /// It did not land, or nobody can say whether it did. The caller must not advance.
        /// </summary>
        Refused,
    }

    /// <summary>
    /// Runs one irreversible settlement effect at most once across every sweep, ever.
    /// </summary>
    /// <remarks>
    /// <para>Each anchor key transitions AT MOST ONCE, because that is all the ledger allows: the
    /// package issues <c>UPDATE ... SET state = $_next WHERE state = $_expected</c> and throws
    /// "Cannot complete idempotency key" when the row has moved on. So a retry cannot reuse a key
    /// it has already settled — it takes the NEXT generation of that key, and "has this effect
    /// landed?" is answered by walking the generations until one of them says so.</para>
    /// <para>The states are read as claims about the WORLD, not about this process:</para>
    /// <list type="bullet">
    /// <item><c>Won</c> — this generation is unclaimed; run the effect.</item>
    /// <item><c>Completed</c> — a pass ran it and recorded that it landed. It is NOT run again,
    /// which is the whole point: the bounded replay windows underneath cannot be trusted to notice
    /// a repeat once 128 other movements have gone by.</item>
    /// <item><c>Failed</c> — a pass ran it and recorded that it did NOT land. Retrying is what the
    /// sweep is for, so this walks on to the next generation.</item>
    /// <item><c>InProgress</c> and not ours — either another pass holds it right now, or one died
    /// between claiming and settling. Neither can be told from the other, and nobody can say
    /// whether the money moved, so this refuses. Crediting a project twice mints value out of
    /// nothing; leaving it short is visible, flagged, and an operator can finish it.</item>
    /// </list>
    /// <para>A generation whose effect THREW is left open rather than marked Failed: "Failed" is a
    /// claim that the effect did not land, and a throw does not know that.</para>
    /// </remarks>
    private async Task<SettlementEffect> OnceAsync(
        string anchorKey,
        Guid operationId,
        string what,
        Func<Task<bool>> apply,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxSettlementAttempts; attempt++)
        {
            var key = AttemptKey(anchorKey, attempt);

            IdempotencyClaim claim;
            try
            {
                claim = await _anchors.TryClaimAsync(key, AnchorOperationType, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger?.LogError(
                    exception,
                    "Exchange {OperationId}: {What} was NOT attempted because its settlement anchor "
                    + "could not be claimed. Refusing rather than running an irreversible effect no "
                    + "record would remember.",
                    operationId, what);
                return SettlementEffect.Refused;
            }

            if (!claim.Won)
            {
                // Whose row is this? The ledger's uniqueness is on the KEY ALONE, so a row at this
                // key is not proof it is OURS. SettlementAnchorPrefix makes a collision unreachable
                // from a caller today; this check is what keeps that true if the key derivation ever
                // moves again. Reading a foreign Completed row as "already applied" is precisely how
                // an unmade credit gets reported as handed back, so a stranger's row refuses.
                if (!string.Equals(
                        claim.Record.OperationType, AnchorOperationType, StringComparison.Ordinal))
                {
                    _logger?.LogError(
                        "MANUAL INTERVENTION REQUIRED — exchange {OperationId}: {What} was NOT applied "
                        + "because its settlement anchor '{Anchor}' is held by a '{Foreign}' record, "
                        + "not this reconciler's '{Expected}'. Nothing was applied. Two key spaces "
                        + "have collided; do not settle this anchor until you know which effect owns it.",
                        operationId, what, key, claim.Record.OperationType, AnchorOperationType);
                    return SettlementEffect.Refused;
                }

                switch (claim.Record.State)
                {
                    case IdempotencyState.Completed:
                        _logger?.LogInformation(
                            "Exchange {OperationId}: {What} had already been applied by an earlier "
                            + "settlement pass; it is NOT applied again.",
                            operationId, what);
                        return SettlementEffect.AlreadyApplied;

                    case IdempotencyState.Failed:
                        // Positively recorded as not-landed. This generation is spent and the
                        // ledger will not let it be settled twice, so the retry takes the next one.
                        continue;

                    default:
                        _logger?.LogError(
                            "MANUAL INTERVENTION REQUIRED — exchange {OperationId}: {What} has a "
                            + "settlement anchor that is still open, so either another pass is "
                            + "running it now or one died mid-effect. Nothing was applied. If no "
                            + "pass is running, read the project's ledger and then settle anchor "
                            + "'{Anchor}' Completed or Failed to say whether the effect landed.",
                            operationId, what, key);
                        return SettlementEffect.Refused;
                }
            }

            bool landed;
            try
            {
                landed = await apply();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The effect may or may not have landed, so this generation is left OPEN rather
                // than marked Failed, and every later pass refuses at it.
                _logger?.LogError(
                    exception,
                    "MANUAL INTERVENTION REQUIRED — exchange {OperationId}: {What} threw. Its anchor "
                    + "'{Anchor}' is left open so no later pass silently repeats it.",
                    operationId, what, key);
                return SettlementEffect.Refused;
            }

            if (!landed)
            {
                // Recorded as not-landed, then refused for THIS sweep: an effect that just failed
                // is not worth hammering, and the next sweep picks up the next generation.
                await TrySettleAnchorAsync(key, operationId, what, landedOk: false);
                return SettlementEffect.Refused;
            }

            await TrySettleAnchorAsync(key, operationId, what, landedOk: true);
            return SettlementEffect.Applied;
        }

        _logger?.LogError(
            "MANUAL INTERVENTION REQUIRED — exchange {OperationId}: {What} has failed "
            + "{Attempts} times. It is not being retried again automatically.",
            operationId, what, MaxSettlementAttempts);
        return SettlementEffect.Refused;
    }

    /// <summary>
    /// The nth generation of an anchor key. The first generation IS the base key, so a row written
    /// before generations existed keeps its meaning.
    /// </summary>
    private static string AttemptKey(string anchorKey, int attempt) =>
        attempt == 1 ? anchorKey : anchorKey + "#" + attempt.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Builds a settlement anchor in the reserved key space. See <see cref="SettlementAnchorPrefix"/>
    /// for why the namespace leads rather than trails.
    /// </summary>
    public static string SettlementAnchor(string ledgerKey, string suffix) =>
        SettlementAnchorPrefix + ledgerKey + suffix;

    /// <summary>
    /// Records what became of one attempt generation. A settle that itself fails leaves the
    /// generation open, which is the fail-closed direction: the next pass refuses at it instead of
    /// repeating the effect.
    /// </summary>
    private async Task TrySettleAnchorAsync(
        string anchorKey, Guid operationId, string what, bool landedOk)
    {
        try
        {
            if (landedOk)
                await _anchors.CompleteAsync(anchorKey, "applied", CancellationToken.None);
            else
                await _anchors.FailAsync(anchorKey, "not applied", CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger?.LogError(
                exception,
                "MANUAL INTERVENTION REQUIRED — exchange {OperationId}: {What} finished but its "
                + "anchor '{Anchor}' could not be settled. Later passes will refuse to touch it "
                + "until an operator settles it by hand, which is the safe direction.",
                operationId, what, anchorKey);
        }
    }

    /// <summary>
    /// Restates the stored replay payload so it names the credit that has now landed. Without it
    /// a redelivered POST would replay a receipt whose ProjectCreditAmount is null for an exchange
    /// whose project HAS been credited — the same statement drifting between two seams again, just
    /// in the harmless direction.
    /// </summary>
    private void RestateReplayPayloadCredit(IBlockchainOperation operation, BigInteger amount)
    {
        if (!operation.Parameters.TryGetValue(
                IdempotencyParameterNames.ResultPayload, out var payload)
            || string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        try
        {
            var receipt = IdempotencyReplay.DeserializeForReplay<ExchangeReceipt>(payload);
            if (receipt is null)
                return;

            receipt.ProjectCreditAmount = amount.ToString(CultureInfo.InvariantCulture);
            operation.Parameters[IdempotencyParameterNames.ResultPayload] =
                IdempotencyReplay.SerializeForReplay(receipt);
        }
        catch (Exception exception)
        {
            // A payload that cannot be re-stated is left exactly as it was: understating a credit
            // is safe, and losing the payload entirely is not.
            _logger?.LogWarning(
                exception,
                "Exchange {OperationId} was credited but its stored replay payload could not be "
                + "restated; the receipt will under-report the credit until the row is rewritten.",
                operation.Id);
        }
    }

    /// <summary>Everything an unwind or a credit needs, read from the row and never guessed.</summary>
    private readonly record struct ExchangeLegs(
        string ChainType,
        Guid TenantId,
        string ProjectReference,
        string Partition,
        string LedgerKey,
        string PlatformAssetId,
        BigInteger PlatformAmount,
        string ProjectAssetId,
        BigInteger ProjectUnitAmount);

    /// <summary>
    /// Reads both legs off the row. The row records the leg that was GIVEN UP as
    /// <c>Amount</c>/<c>SourceTokenId</c> and the leg RECEIVED as
    /// <c>AmountOut</c>/<c>TargetTokenId</c>, so the direction decides which is the platform's.
    /// </summary>
    private static bool TryReadLegs(
        IBlockchainOperation operation, out ExchangeLegs legs, out string missing)
    {
        legs = default;

        var chainType = operation.Parameters.GetValueOrDefault("ChainType", string.Empty);
        var projectReference = operation.Parameters.GetValueOrDefault("ProjectReference", string.Empty);
        var partition = operation.Parameters.GetValueOrDefault("ReservePartition", string.Empty);
        var ledgerKey = operation.Parameters.GetValueOrDefault("IdempotencyKey", string.Empty);
        var direction = operation.Parameters.GetValueOrDefault("ExchangeDirection", string.Empty);
        var spentAsset = operation.Parameters.GetValueOrDefault("SourceTokenId", string.Empty);
        var receivedAsset = operation.Parameters.GetValueOrDefault("TargetTokenId", string.Empty);
        var spentAmountText = operation.Parameters.GetValueOrDefault("Amount", string.Empty);
        var receivedAmountText = operation.Parameters.GetValueOrDefault("AmountOut", string.Empty);

        if (string.IsNullOrWhiteSpace(chainType)) { missing = "which rail it settled on"; return false; }
        if (string.IsNullOrWhiteSpace(projectReference)) { missing = "which project it was for"; return false; }
        if (string.IsNullOrWhiteSpace(partition)) { missing = "which reserve partition it drew"; return false; }
        if (string.IsNullOrWhiteSpace(ledgerKey)) { missing = "its ledger key"; return false; }
        if (operation.ActingTenantId is not { } tenantId || tenantId == Guid.Empty)
        {
            missing = "which tenant drove it";
            return false;
        }

        if (!Enum.TryParse<ExchangeDirection>(direction, ignoreCase: false, out var parsedDirection))
        {
            missing = "which way it went";
            return false;
        }

        if (!BigInteger.TryParse(
                spentAmountText, NumberStyles.None, CultureInfo.InvariantCulture, out var spentAmount)
            || !BigInteger.TryParse(
                receivedAmountText, NumberStyles.None, CultureInfo.InvariantCulture, out var receivedAmount))
        {
            missing = "how much moved on each leg";
            return false;
        }

        if (string.IsNullOrWhiteSpace(spentAsset) || string.IsNullOrWhiteSpace(receivedAsset))
        {
            missing = "which assets its legs were";
            return false;
        }

        var buying = parsedDirection == ExchangeDirection.Buy;

        legs = new ExchangeLegs(
            chainType,
            tenantId,
            projectReference,
            partition,
            ledgerKey,
            PlatformAssetId: buying ? spentAsset : receivedAsset,
            PlatformAmount: buying ? spentAmount : receivedAmount,
            ProjectAssetId: buying ? receivedAsset : spentAsset,
            ProjectUnitAmount: buying ? receivedAmount : spentAmount);

        missing = string.Empty;
        return true;
    }
}
