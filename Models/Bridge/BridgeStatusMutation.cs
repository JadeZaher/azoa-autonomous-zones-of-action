namespace AZOA.WebAPI.Models.Bridge;

/// <summary>
/// Optional SET fields for <c>IBridgeStore.TryTransitionBridgeStatusAsync</c>;
/// only non-null fields are applied. The store applies these verbatim — it
/// never asserts row count, retries, or read-modify-writes.
/// </summary>
public sealed record BridgeStatusMutation
{
    /// <summary>Idempotency key of the operation advancing the bridge.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Failure detail to persist on the transaction.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Source-chain lock transaction hash.</summary>
    public string? LockTxHash { get; init; }

    /// <summary>Source-chain sender address.</summary>
    public string? SourceAddress { get; init; }

    /// <summary>Target-chain redemption transaction hash.</summary>
    public string? RedemptionTxHash { get; init; }

    /// <summary>Target-chain mint transaction hash.</summary>
    public string? MintTxHash { get; init; }

    /// <summary>Target-chain token id minted/credited.</summary>
    public string? TargetTokenId { get; init; }

    /// <summary>Wormhole emitter chain id of the originating message.</summary>
    public int? WormholeEmitterChainId { get; init; }

    /// <summary>Wormhole emitter address (hex, 32-byte Wormhole format).</summary>
    public string? WormholeEmitterAddress { get; init; }

    /// <summary>Wormhole sequence number of the originating message.</summary>
    public long? WormholeSequence { get; init; }

    /// <summary>When true, set <c>CompletedAt</c> to the current UTC time.</summary>
    public bool SetCompletedAtUtcNow { get; init; }

    /// <summary>When true, clear <c>CompletedAt</c> back to null.</summary>
    public bool ClearCompletedAt { get; init; }
}
