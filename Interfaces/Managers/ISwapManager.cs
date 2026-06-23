using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

public interface ISwapManager
{
    Task<AZOAResult<SwapQuoteResponse>> GetQuoteAsync(SwapQuoteRequest request);

    /// <summary>
    /// Build a transaction for executing a swap (Jupiter v2 /swap or Tinyman).
    /// Returns an unsigned transaction payload for client-side signing.
    /// </summary>
    /// <param name="clientIdempotencyKey">
    /// Optional client-supplied idempotency key (the <c>Idempotency-Key</c>
    /// request header). Accepted for forward-compatibility and audit; the swap
    /// path returns an UNSIGNED transaction (the client signs and broadcasts),
    /// so there is no server-side irreversible effect to dedupe here. The key
    /// is plumbed through additively (default null) so a future server-broadcast
    /// swap path can dedupe on it without an interface change.
    /// </param>
    Task<AZOAResult<SwapQuoteResponse>> GetSwapTransactionAsync(SwapExecuteRequest request, string? clientIdempotencyKey = null);
}
