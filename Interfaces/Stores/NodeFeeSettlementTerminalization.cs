using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>Validated chain-observation proof required to complete a paired settlement.</summary>
public sealed record NodeFeeSettlementTerminalization
{
    /// <summary>Preserves the original raw-parent-key constructor for callers that possess it.</summary>
    public NodeFeeSettlementTerminalization(
        string parentIdempotencyKey,
        string primaryEffectReference,
        string feeEffectReference,
        string parentResultPayload)
        : this(
            NodeFeeSettlement.HashParentIdempotencyKey(parentIdempotencyKey),
            NodeFeeSettlement.CanonicalizeParentIdempotencyKey(parentIdempotencyKey),
            primaryEffectReference,
            feeEffectReference,
            parentResultPayload)
    {
    }

    private NodeFeeSettlementTerminalization(
        string parentIdempotencyKeyHash,
        string? parentIdempotencyKey,
        string primaryEffectReference,
        string feeEffectReference,
        string parentResultPayload)
    {
        ParentIdempotencyKeyHash = parentIdempotencyKeyHash;
        ParentIdempotencyKey = parentIdempotencyKey;
        PrimaryEffectReference = primaryEffectReference;
        FeeEffectReference = feeEffectReference;
        ParentResultPayload = parentResultPayload;
    }

    /// <summary>Persisted deterministic parent record id used when the raw key is intentionally unavailable.</summary>
    public string ParentIdempotencyKeyHash { get; }
    /// <summary>Optional raw parent key retained only for legacy callers that already hold it.</summary>
    public string? ParentIdempotencyKey { get; }
    public string PrimaryEffectReference { get; }
    public string FeeEffectReference { get; }
    public string ParentResultPayload { get; }

    /// <summary>Builds a proof from the durable parent hash without reconstructing a caller secret.</summary>
    public static NodeFeeSettlementTerminalization FromParentIdempotencyKeyHash(
        string parentIdempotencyKeyHash,
        string primaryEffectReference,
        string feeEffectReference,
        string parentResultPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentIdempotencyKeyHash);
        if (parentIdempotencyKeyHash.Length != 64
            || !parentIdempotencyKeyHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ArgumentException(
                "A parent idempotency hash must be a canonical lowercase SHA-256 digest.",
                nameof(parentIdempotencyKeyHash));
        }

        return new NodeFeeSettlementTerminalization(
            parentIdempotencyKeyHash,
            null,
            primaryEffectReference,
            feeEffectReference,
            parentResultPayload);
    }
}
