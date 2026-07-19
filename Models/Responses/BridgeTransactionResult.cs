using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Core.Blockchain.Wormhole;
using AZOA.WebAPI.Helpers;

namespace AZOA.WebAPI.Models.Responses;

/// <summary>
/// Represents a cross-chain bridge transaction, persisted via EF Core.
/// </summary>
[Table("BridgeTransactions")]
public class BridgeTransactionResult
{
    [Key]
    [MaxLength(64)]
    public string Id { get; set; } = string.Empty;

    public Guid AvatarId { get; set; }

    [MaxLength(32)]
    public string SourceChain { get; set; } = string.Empty;

    [MaxLength(32)]
    public string TargetChain { get; set; } = string.Empty;

    [MaxLength(256)]
    public string SourceTokenId { get; set; } = string.Empty;

    [MaxLength(256)]
    public string? TargetTokenId { get; set; }

    /// <summary>Server-selected source-chain custody/vault address.</summary>
    [MaxLength(512)]
    public string SourceAddress { get; set; } = string.Empty;

    [MaxLength(512)]
    public string TargetAddress { get; set; } = string.Empty;

    [JsonConverter(typeof(UlongDecimalStringJsonConverter))]
    public ulong Amount { get; set; }

    public BridgeStatus Status { get; set; }

    public BridgeMode Mode { get; set; } = BridgeMode.Trusted;

    [MaxLength(256)]
    public string? LockTxHash { get; set; }

    [MaxLength(256)]
    public string? MintTxHash { get; set; }

    [MaxLength(2048)]
    public string? ProofData { get; set; }

    [MaxLength(1024)]
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    // ─── Wormhole-specific fields (populated when Mode == Wormhole) ───

    public int? WormholeEmitterChainId { get; set; }

    [MaxLength(128)]
    public string? WormholeEmitterAddress { get; set; }

    public long? WormholeSequence { get; set; }

    [MaxLength(4096)]
    public string? VaaBytes { get; set; }

    public int? VaaSignatureCount { get; set; }

    [MaxLength(256)]
    public string? RedemptionTxHash { get; set; }

    // ─── Exactly-once / atomic-transition safety fields (Wave 1 contract) ───

    /// <summary>
    /// Idempotency key for the irreversible operation that produced/advances
    /// this bridge transaction (e.g., the redeem request's Idempotency-Key).
    /// Nullable for back-compat with rows created before this field existed.
    /// </summary>
    [MaxLength(200)]
    public string? IdempotencyKey { get; set; }

    /// <summary>Network the bridge was initiated on. Nullable — absent on rows written before this field existed.</summary>
    public ChainNetwork? Network { get; set; }
}

public enum BridgeStatus
{
    Initiated,
    /// <summary>Durable reservation immediately before a trusted source lock.</summary>
    Locking,
    Locked,
    AwaitingVAA,
    VAAReady,
    Redeeming,
    // NOTE: 'Minted' was removed (Wave 1) — it was dead code. Redeeming is the
    // exclusive target-side mint/redeem reservation for both bridge modes.
    Completed,
    Failed,
    Refunded,
    // Explicit reversal-in-flight marker. ReverseBridgeAsync moves a Completed
    // bridge here before the on-chain burn, then to Refunded (success) or
    // Failed (manual intervention). Distinct from Redeeming (forward redeem)
    // so reversal provenance is explicit, not inferred from a CompletedAt
    // timestamp. Appended (int-stored) so existing terminal values are stable.
    Reversing
}
