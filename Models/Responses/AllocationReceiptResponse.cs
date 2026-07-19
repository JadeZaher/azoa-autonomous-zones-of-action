// SPDX-License-Identifier: UNLICENSED

using System.Text.Json.Serialization;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>Externally observable state of a caller-authorized allocation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AllocationReceiptState
{
    /// <summary>The allocation is not safely terminal and needs observation.</summary>
    AwaitingReconciliation,

    /// <summary>The allocation's durable operation and ledger are terminally successful.</summary>
    Completed,

    /// <summary>The allocation reached a terminal failure without a retry instruction.</summary>
    Failed,
}

/// <summary>
/// Secret-free, caller-scoped allocation receipt. It deliberately omits raw
/// idempotency keys, caller identity, operation parameters, provider payloads,
/// and custody material.
/// </summary>
public sealed class AllocationReceiptResponse
{
    /// <summary>Opaque SHA-256 reference for this allocation receipt.</summary>
    public string ReceiptReference { get; init; } = string.Empty;

    /// <summary>Opaque durable operation reference when AZOA persisted one.</summary>
    public Guid? OperationId { get; init; }

    public AllocationReceiptState State { get; init; }
    public bool IsTerminal { get; init; }
    public bool RequiresReconciliation { get; init; }

    /// <summary>Target account facts included only when a safe allocation result exists.</summary>
    public Guid? AvatarId { get; init; }
    public Guid? WalletId { get; init; }
    public string? WalletAddress { get; init; }
    public bool? WalletProvisioned { get; init; }

    /// <summary>Public chain transaction reference when recorded.</summary>
    public string? TransactionReference { get; init; }

    /// <summary>Base-unit value facts for the allocation, when recorded.</summary>
    public string? GrossAmount { get; init; }
    public string? NodeFeeAmount { get; init; }
    public string? NetAmount { get; init; }
    public long? NodeFeeScheduleVersion { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>Bounded terminal error discriminator; never provider detail.</summary>
    public string? FailureCode { get; init; }
}
