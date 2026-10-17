// SPDX-License-Identifier: UNLICENSED

using System.Text.Json.Serialization;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>Externally observable state of a caller-authorized exchange.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExchangeReceiptState
{
    /// <summary>The exchange is not safely terminal and needs observation.</summary>
    AwaitingReconciliation,

    /// <summary>The exchange settled and its ledger entry is terminal.</summary>
    Completed,

    /// <summary>The exchange reached a terminal failure. Nothing moved, or what moved was reversed.</summary>
    Failed,
}

/// <summary>
/// Secret-free, caller-scoped exchange receipt — the named read surface behind
/// <c>GET /api/exchange/receipt/{idempotencyKey}</c> that a consumer reconciles against
/// (W6.3). It deliberately omits raw idempotency keys, caller identity, account keys and
/// provider payloads.
/// </summary>
/// <remarks>
/// This is a different receipt from <c>AllocationReceiptResponse</c> on purpose: the
/// allocation receipt route resolves an allocation ledger entry by its own operation type
/// and cannot see an exchange, so an exchange needs its own route and its own shape. The
/// two ledger keys are domain-separated for the same reason.
/// </remarks>
public sealed class ExchangeReceipt
{
    /// <summary>Opaque SHA-256 reference for this exchange. Safe to log and to echo back.</summary>
    public string ReceiptReference { get; set; } = string.Empty;

    /// <summary>Opaque durable operation reference once AZOA has persisted one.</summary>
    public Guid? OperationId { get; set; }

    public ExchangeReceiptState State { get; set; }
    public bool IsTerminal { get; set; }
    public bool RequiresReconciliation { get; set; }

    /// <summary>True when this answer replays an earlier identical request rather than a new exchange.</summary>
    public bool Replayed { get; set; }

    /// <summary>Which way the units moved.</summary>
    public ExchangeDirection Direction { get; set; }

    /// <summary>The person the exchange was for.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The consumer's own project id, echoed back.</summary>
    public string? ProjectReference { get; set; }

    /// <summary>Base units the initiator gave up, and the asset they are counted in.</summary>
    public string? AmountSpent { get; set; }
    public string? AmountSpentAssetId { get; set; }

    /// <summary>Base units the initiator received, and the asset they are counted in.</summary>
    public string? AmountReceived { get; set; }
    public string? AmountReceivedAssetId { get; set; }

    /// <summary>Base units credited to the project's own account by this exchange.</summary>
    public string? ProjectCreditAmount { get; set; }

    /// <summary>The rail's reference for the whole atomic group, when one settled.</summary>
    public string? SettlementReference { get; set; }

    /// <summary>The rail's per-leg references, in the order the group declared them.</summary>
    public IReadOnlyList<string> SettlementLegReferences { get; set; } = Array.Empty<string>();

    /// <summary>The hold the received units are committed to, when the exchange opened one.</summary>
    public string? HoldReference { get; set; }

    /// <summary>When the held units become the recipient's to move.</summary>
    public DateTimeOffset? HoldReleasesAt { get; set; }

    /// <summary>The declared rate that priced this exchange, and where it came from (D7/D16).</summary>
    public string? Rate { get; set; }
    public string? RateCurrencyCode { get; set; }
    public string? RateSource { get; set; }
    public string? RateVersion { get; set; }
    public DateTimeOffset? RateAsOf { get; set; }

    /// <summary>
    /// What the spent leg was worth in the rate currency's minor unit at settlement, when a
    /// rate source priced it. Absent means never priced — never zero.
    /// </summary>
    public string? ValueMinorUnits { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Bounded terminal error discriminator; never provider detail.</summary>
    public string? FailureCode { get; set; }

    /// <summary>Durable exchange source this receipt reads; safe to retain for discovery.</summary>
    public string? SourceId { get; set; }

    /// <summary>Server-authored automatic-release disclosure a preview returns for explicit acceptance.</summary>
    public ExchangeReleaseDisclosure? ReleaseDisclosure { get; set; }

    /// <summary>Release permission created with a held purchase, separate from its settlement.</summary>
    public string? ReleaseMandateId { get; set; }
}
