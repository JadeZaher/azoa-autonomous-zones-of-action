// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Claims-derived caller context for an allocation receipt lookup or
/// reconciliation. This is constructed by the controller; it is never bound
/// from an HTTP body.
/// </summary>
public sealed class AllocationReceiptRequest
{
    /// <summary>The authenticated API-key record id.</summary>
    public Guid ApiKeyId { get; init; }

    /// <summary>The authenticated user that owns the API key.</summary>
    public Guid CallerUserId { get; init; }

    /// <summary>The required client-supplied <c>Idempotency-Key</c> header.</summary>
    public string ClientIdempotencyKey { get; init; } = string.Empty;
}
