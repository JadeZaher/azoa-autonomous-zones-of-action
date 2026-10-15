// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Window + page for the analytics read surface. There is deliberately NO user id here:
/// the subject comes from the caller's claims. See <c>Services/Analytics/AGENTS.md</c> §self-scope.
/// </summary>
public sealed class AnalyticsQueryRequest
{
    /// <summary>Inclusive lower bound on <c>created_date</c>. Null means "from the beginning".</summary>
    public DateTimeOffset? From { get; set; }

    /// <summary>Exclusive upper bound on <c>created_date</c>. Null means "up to now".</summary>
    public DateTimeOffset? To { get; set; }

    /// <summary>Page offset. Negative values are rejected, not clamped.</summary>
    public int? Skip { get; set; }

    /// <summary>Page size. Clamped to the service's maximum; null takes the default.</summary>
    public int? Take { get; set; }
}
