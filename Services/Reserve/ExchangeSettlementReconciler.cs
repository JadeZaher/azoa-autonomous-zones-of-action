// SPDX-License-Identifier: UNLICENSED

// Retired exchange rows only; see Services/Reserve/AGENTS.md §retired-rows.

using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>Classifies retired exchange operation rows for generic reconciliation.</summary>
public interface IExchangeSettlementReconciler
{
    /// <summary>True when this operation is a retired exchange row.</summary>
    bool Owns(IBlockchainOperation operation);

    /// <summary>True only when an operator attested <c>Confirmed</c> for this exact transaction; never settles from the row alone.</summary>
    Task<bool> SettleConfirmedAsync(IBlockchainOperation operation, CancellationToken ct = default);

    /// <summary>True only when an operator attested <c>Failed</c> for this exact transaction; never unwinds from the row alone.</summary>
    Task<bool> CompensateFailedAsync(IBlockchainOperation operation, CancellationToken ct = default);
}

/// <summary>Persists and reads operator resolutions of retired exchange rows.</summary>
public interface IExchangeRetiredResolutionStore
{
    /// <summary>Reads the resolution for one operation, if any.</summary>
    Task<ExchangeRetiredResolution?> GetRetiredResolutionAsync(Guid operationId, CancellationToken ct = default);

    /// <summary>Creates the resolution once; returns the stored row (the original on an identical replay) or null on a conflicting one.</summary>
    Task<ExchangeRetiredResolution?> RecordRetiredResolutionAsync(ExchangeRetiredResolution resolution, CancellationToken ct = default);

    /// <summary>Lists unresolved-by-reconciliation exchange operation rows, oldest first.</summary>
    Task<IReadOnlyList<OperationLog>> ListRetiredExchangeRowsAsync(int limit, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ExchangeSettlementReconciler(IExchangeRetiredResolutionStore resolutions,
    ILogger<ExchangeSettlementReconciler>? logger = null) : IExchangeSettlementReconciler
{
    /// <summary>Parameter carrying the observed transaction on an operation row.</summary>
    public const string TransactionHashParameter = "TxHash";

    /// <inheritdoc />
    public bool Owns(IBlockchainOperation operation) =>
        operation is not null
        && string.Equals(operation.OperationType, ReserveExchangeManager.ChainOperationType, StringComparison.Ordinal);

    /// <inheritdoc />
    public Task<bool> SettleConfirmedAsync(IBlockchainOperation operation, CancellationToken ct = default)
        => ResolveAsync(operation, "Confirmed", ct);

    /// <inheritdoc />
    public Task<bool> CompensateFailedAsync(IBlockchainOperation operation, CancellationToken ct = default)
        => ResolveAsync(operation, "Failed", ct);

    private async Task<bool> ResolveAsync(IBlockchainOperation operation, string chainOutcome, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!Owns(operation)) return true;
        var resolution = await resolutions.GetRetiredResolutionAsync(operation.Id, ct);
        if (resolution is not null && resolution.Outcome == chainOutcome
            && operation.Parameters.TryGetValue(TransactionHashParameter, out var hash) && resolution.TransactionHash == hash)
        {
            logger?.LogInformation("Retired exchange row {OperationId} closes as chain-{Outcome} on operator resolution {ResolvedBy}.",
                operation.Id, chainOutcome, resolution.ResolvedByUserId);
            return true;
        }
        logger?.LogError(
            "MANUAL INTERVENTION REQUIRED — retired exchange row {OperationId} is chain-{Outcome} but has no matching operator "
            + "resolution; it is neither completed nor unwound automatically.", operation.Id, chainOutcome);
        return false;
    }
}
