namespace AZOA.WebAPI.Models;

/// <summary>One-time invitation consumed atomically with ownership transfer; see Managers/AGENTS.md.</summary>
public class WalletAuthClaimToken
{
    /// <summary>Stable record id (Guid; persisted as the SurrealDB record id).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The cryptographically-random one-time token value; UNIQUE across the table.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>The child user this token authorizes a claim for.</summary>
    public Guid TargetUserId { get; set; }

    /// <summary>The tenant principal that minted the token (asserted owner at mint time).</summary>
    public Guid TenantId { get; set; }

    /// <summary>Expiry instant (UTC). Past this the token is dead even if unconsumed.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Single-use marker (UTC). Non-null = already redeemed; never reusable.</summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True iff unconsumed AND not yet expired at <paramref name="now"/>.</summary>
    public bool IsLiveAt(DateTime now) => ConsumedAt == null && now < ExpiresAt;
}
