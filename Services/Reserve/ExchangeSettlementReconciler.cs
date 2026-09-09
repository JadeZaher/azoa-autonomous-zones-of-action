// SPDX-License-Identifier: UNLICENSED
//
// OI-33, both halves. An exchange group the rail ACCEPTS is not an exchange that happened, so
// ReserveExchangeManager stops short of two things and leaves them to chain truth: it does not
// credit the project, and it does not unwind. This file is what chain truth then does about it.
// Without it "we credit only on confirmation" is a refusal with no completion, and "an
// accepted-then-dropped group is compensated" is true only for the seconds the request is still
// on the wire.
//
// See Services/Reserve/AGENTS.md §exchange-settlement.

using System.Globalization;
using System.Numerics;
using AZOA.WebAPI.Core.Idempotency;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
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
    private readonly ILogger<ExchangeSettlementReconciler>? _logger;

    public ExchangeSettlementReconciler(
        IProjectTreasury treasury,
        IPlatformReserve reserve,
        IPlatformHold holds,
        IBlockchainOperationStore operations,
        ILogger<ExchangeSettlementReconciler>? logger = null)
    {
        _treasury = treasury ?? throw new ArgumentNullException(nameof(treasury));
        _reserve = reserve ?? throw new ArgumentNullException(nameof(reserve));
        _holds = holds ?? throw new ArgumentNullException(nameof(holds));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
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

        // The SAME ledger key the in-request credit would have used, so a credit that did land and
        // whose flag was never flipped replays instead of doubling. RecordMovementAsync is
        // idempotent on it and reports Replayed.
        var credited = await _treasury.RecordMovementAsync(
            new ProjectTreasuryMovementRequest(
                new ProjectTreasuryReference(legs.ChainType, legs.TenantId, legs.ProjectReference),
                legs.PlatformAssetId,
                ProjectTreasuryMovementKind.Credit,
                legs.PlatformAmount,
                legs.LedgerKey),
            ct);

        if (credited.IsError)
        {
            _logger?.LogError(
                "MANUAL INTERVENTION REQUIRED — exchange {OperationId} is chain-confirmed but its "
                + "project credit could not be recorded: {Detail}. NOT advancing it.",
                operation.Id, credited.Message);
            return false;
        }

        // The flag is the read side two surfaces consult, so it has to move with the money. A row
        // that credited and still says "false" reports AwaitingReconciliation forever.
        operation.Parameters[ReserveExchangeManager.ProjectCreditAppliedParameter] = "true";
        RestateReplayPayloadCredit(operation, legs.PlatformAmount);

        var persisted = await _operations.UpsertAsync(operation, ct);
        if (persisted.IsError)
        {
            _logger?.LogError(
                "Exchange {OperationId} had its project credit recorded but the row still says the "
                + "credit is outstanding: {Detail}. The credit is idempotent on its ledger key, so "
                + "the next sweep replays it rather than doubling it.",
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

        var handedBack = kind switch
        {
            ReserveExchangeManager.CommitmentKinds.PartitionIssue =>
                await ReturnToPartitionAsync(legs, ct),
            ReserveExchangeManager.CommitmentKinds.PartitionReturn =>
                await ReIssueFromPartitionAsync(legs, ct),
            ReserveExchangeManager.CommitmentKinds.Hold =>
                await RefundHoldAsync(operation, legs, ct),
            _ => false,
        };

        if (!handedBack)
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
            var refunded = await _treasury.RecordMovementAsync(
                new ProjectTreasuryMovementRequest(
                    new ProjectTreasuryReference(legs.ChainType, legs.TenantId, legs.ProjectReference),
                    legs.PlatformAssetId,
                    ProjectTreasuryMovementKind.Credit,
                    legs.PlatformAmount,
                    legs.LedgerKey + CompensationSuffix),
                ct);

            if (refunded.IsError)
            {
                _logger?.LogError(
                    "MANUAL INTERVENTION REQUIRED — exchange {OperationId} failed on chain, its "
                    + "partition was handed back, but the project's treasury is still short "
                    + "{Amount} base units: {Detail}.",
                    operation.Id, legs.PlatformAmount, refunded.Message);
                return false;
            }
        }

        _logger?.LogWarning(
            "Exchange {OperationId} failed on chain after the rail had accepted it; every commitment "
            + "it had taken has been handed back.",
            operation.Id);

        return true;
    }

    /// <summary>The key suffix a compensating draw carries, matching the in-request unwind.</summary>
    private const string CompensationSuffix = ":compensate";

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
