// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces;

/// <summary>
/// The treasury-rate exchange: a sponsor spends the platform unit, the project receives it
/// on chain, and the sponsor receives project units — as one atomic group (N-X1 / D50) settled through
/// a durable exchange source (see Services/Reserve/AGENTS.md §durable-exchange).
/// </summary>
/// <remarks>
/// <para><b>This is not a swap and not an AMM.</b> The DEX path stays separate and nothing here
/// resolves a DEX service. There is no pool, no curve and no price discovery: the consumer computes
/// the rate and this seam executes the move it already decided (D7/D16).</para>
/// <para><b>Every exchange is gated exactly like an allocation.</b> Identity readiness (D46),
/// the person's live consent when a tenant drives it (W3.6), the node governance guard, and
/// the reserve's own named-partition ledger (W3.1), which is what makes an oversubscription
/// refusable on the node rather than only in a consumer's database.</para>
/// </remarks>
public interface IReserveExchangeManager
{
    /// <summary>
    /// Runs one exchange, or replays the one this caller already ran under the same
    /// idempotency key. Never performs a second movement for a repeated key.
    /// </summary>
    Task<AZOAResult<ExchangeReceipt>> ExchangeAsync(
        ExchangeRequest request, ExchangeCallerContext caller, CancellationToken ct = default);

    /// <summary>
    /// The receipt for one caller's exchange — the named read surface a consumer reconciles
    /// against. Scoped to the API key and the caller that ran it; another caller's key
    /// resolves nothing.
    /// </summary>
    Task<AZOAResult<ExchangeReceipt>> GetReceiptAsync(
        ExchangeReceiptQuery query, CancellationToken ct = default);
}
