// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Reserve;

/// <summary>Operator view of one retired exchange operation row and its resolution, if any.</summary>
public sealed record RetiredExchangeRowView(Guid OperationId, string Status, string? TransactionHash, string? ChainType,
    DateTimeOffset CreatedAt, string? ResolvedOutcome, string? ResolvedReason, DateTimeOffset? ResolvedAt);

/// <summary>Operator request: attest the chain outcome the node-side ledger was made good for.</summary>
public sealed class RetiredExchangeResolutionRequest
{
    /// <summary><c>Confirmed</c> or <c>Failed</c>; reconciliation applies it only when chain truth agrees.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>What the operator did to the ledger (credited, returned, verified nothing owed).</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Lists retired exchange rows and records audited operator resolutions; see Services/Reserve/AGENTS.md §retired-rows.</summary>
public sealed class ExchangeRetiredRowService(IExchangeRetiredResolutionStore store, TimeProvider? clock = null)
{
    private const int MaximumReasonLength = 1000;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Lists retired exchange rows the reconciler leaves to an operator.</summary>
    public async Task<AZOAResult<IReadOnlyList<RetiredExchangeRowView>>> ListAsync(int limit, CancellationToken ct = default)
    {
        var rows = await store.ListRetiredExchangeRowsAsync(Math.Clamp(limit, 1, 200), ct);
        var views = new List<RetiredExchangeRowView>(rows.Count);
        foreach (var row in rows)
        {
            var id = SurrealRecordGuid.Parse(row.Id);
            var resolution = await store.GetRetiredResolutionAsync(id, ct);
            views.Add(View(row, id, resolution));
        }
        return AZOAResult<IReadOnlyList<RetiredExchangeRowView>>.Success(views);
    }

    /// <summary>Records one immutable resolution for an unresolved retired exchange row.</summary>
    public async Task<AZOAResult<RetiredExchangeRowView>> ResolveAsync(Guid operationId, RetiredExchangeResolutionRequest request,
        Guid operatorUserId, CancellationToken ct = default)
    {
        var outcome = request?.Outcome?.Trim();
        var reason = request?.Reason?.Trim();
        if (operationId == Guid.Empty || operatorUserId == Guid.Empty || outcome is not ("Confirmed" or "Failed")
            || string.IsNullOrWhiteSpace(reason) || reason.Length > MaximumReasonLength)
            return AZOAResult<RetiredExchangeRowView>.FailureWithCode(
                "An operation, an outcome of Confirmed or Failed, and a reason of at most 1000 characters are required.",
                AzoaErrorCodes.InvalidRequest);
        var row = await store.GetRetiredExchangeRowAsync(operationId, ct);
        if (row is null || TransactionHash(row) is not { } hash)
            return AZOAResult<RetiredExchangeRowView>.FailureWithCode(
                "No unresolved retired exchange row with an observed transaction has that id.", AzoaErrorCodes.NotFound);
        var saved = await store.RecordRetiredResolutionAsync(new ExchangeRetiredResolution
        {
            Id = operationId.ToString("N"), OperationId = operationId.ToString("N"), Outcome = outcome, TransactionHash = hash,
            Reason = reason, ResolvedByUserId = operatorUserId.ToString("N"), CreatedAt = _clock.GetUtcNow(),
        }, ct);
        return saved is null
            ? AZOAResult<RetiredExchangeRowView>.FailureWithCode("This row already carries a different resolution.", AzoaErrorCodes.Conflict)
            : AZOAResult<RetiredExchangeRowView>.Success(View(row, operationId, saved),
                "Resolution recorded; reconciliation closes the row once chain truth agrees.");
    }

    private static RetiredExchangeRowView View(OperationLog row, Guid id, ExchangeRetiredResolution? resolution)
        => new(id, row.Status.ToString(), TransactionHash(row), Parameter(row, "ChainType"), row.CreatedDate,
            resolution?.Outcome, resolution?.Reason, resolution?.CreatedAt);

    private static string? TransactionHash(OperationLog row)
        => Parameter(row, ExchangeSettlementReconciler.TransactionHashParameter) is { Length: > 0 } hash ? hash : null;

    private static string? Parameter(OperationLog row, string name)
        => row.Parameters is { ValueKind: System.Text.Json.JsonValueKind.Object } parameters
           && parameters.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() : null;

}
