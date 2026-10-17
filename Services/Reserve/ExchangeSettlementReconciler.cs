// SPDX-License-Identifier: UNLICENSED

// Retired exchange rows only; see Services/Reserve/AGENTS.md §durable-exchange (Retired rows).

using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Managers;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>Classifies retired exchange operation rows for generic reconciliation.</summary>
public interface IExchangeSettlementReconciler
{
    /// <summary>True when this operation is a retired exchange row.</summary>
    bool Owns(IBlockchainOperation operation);

    /// <summary>Returns false for every owned row: a confirmed retired exchange requires operator evidence before completion.</summary>
    Task<bool> SettleConfirmedAsync(IBlockchainOperation operation, CancellationToken ct = default);

    /// <summary>Returns false for every owned row: a failed retired exchange requires operator evidence before unwinding.</summary>
    Task<bool> CompensateFailedAsync(IBlockchainOperation operation, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ExchangeSettlementReconciler(ILogger<ExchangeSettlementReconciler>? logger = null) : IExchangeSettlementReconciler
{
    /// <inheritdoc />
    public bool Owns(IBlockchainOperation operation) =>
        operation is not null
        && string.Equals(operation.OperationType, ReserveExchangeManager.ChainOperationType, StringComparison.Ordinal);

    /// <inheritdoc />
    public Task<bool> SettleConfirmedAsync(IBlockchainOperation operation, CancellationToken ct = default)
        => Task.FromResult(Refuse(operation, "confirmed"));

    /// <inheritdoc />
    public Task<bool> CompensateFailedAsync(IBlockchainOperation operation, CancellationToken ct = default)
        => Task.FromResult(Refuse(operation, "failed"));

    private bool Refuse(IBlockchainOperation operation, string outcome)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!Owns(operation)) return true;
        logger?.LogError(
            "MANUAL INTERVENTION REQUIRED — retired exchange row {OperationId} is chain-{Outcome} but has no durable "
            + "exchange source evidence; it is neither completed nor unwound automatically.", operation.Id, outcome);
        return false;
    }
}
