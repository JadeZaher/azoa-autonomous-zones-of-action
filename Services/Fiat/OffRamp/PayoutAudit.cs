// SPDX-License-Identifier: UNLICENSED

using System.Text.Json;
using System.Text.Json.Nodes;

namespace AZOA.WebAPI.Services.Fiat.OffRamp;

/// <summary>Append-only operator audit on <c>fiat_payout.recovery_audit_json</c>; existing entries are kept verbatim.</summary>
public static class PayoutAudit
{
    /// <summary>One operator action on a cash-out.</summary>
    public sealed record Entry(
        Guid ActorId, string Action, string Reason, DateTimeOffset At, string PriorStatus, string? TransferId);

    /// <summary>Appends <paramref name="entry"/> without re-shaping earlier entries of other kinds.</summary>
    public static string Append(string? json, object entry)
    {
        // A corrupt audit throws rather than being replaced: history is never silently dropped.
        var array = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json) as JsonArray
            ?? throw new InvalidOperationException("The cash-out audit is not a JSON array.");
        array.Add(JsonSerializer.SerializeToNode(entry, entry.GetType()));
        return array.ToJsonString();
    }
}
