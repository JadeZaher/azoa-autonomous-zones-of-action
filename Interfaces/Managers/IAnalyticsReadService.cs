// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// Read-only projection of <c>operation_log</c> into a transaction history and per-asset
/// positions. Writes nothing and prices nothing.
/// See <c>Services/Analytics/AGENTS.md</c> §unpriced-representation.
/// </summary>
public interface IAnalyticsReadService
{
    /// <summary>One page of <paramref name="userId"/>'s operations, newest first.</summary>
    Task<AZOAResult<TransactionHistoryPage>> GetTransactionsAsync(
        Guid userId,
        AnalyticsQueryRequest request,
        CancellationToken ct = default);

    /// <summary>Per (chain, asset) net movement for <paramref name="userId"/> over the window.</summary>
    Task<AZOAResult<PositionsResponse>> GetPositionsAsync(
        Guid userId,
        AnalyticsQueryRequest request,
        CancellationToken ct = default);
}
