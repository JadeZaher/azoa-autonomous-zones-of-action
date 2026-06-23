namespace AZOA.WebAPI.Models;

/// <summary>
/// A tenant-minted, single-use, short-TTL claim-invite token (user-sovereign-identity
/// §2, AC4). A tenant generates it for a child avatar it owns; the user later redeems
/// it together with a USER-SIDE credential (a fresh wallet challenge OR a user-chosen
/// password) to take ownership.
///
/// <para><b>The token alone never sets a credential (M1).</b> It authorizes the
/// credential-setting step but does not derive from any tenant-held child JWT, so a
/// tenant cannot complete the claim on the user's behalf. Redemption is single-use and
/// atomic (same no-TOCTOU rule as the auth nonce).</para>
/// </summary>
public class WalletAuthClaimToken
{
    /// <summary>Stable record id (Guid; persisted as the SurrealDB record id).</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The cryptographically-random one-time token value; UNIQUE across the table.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>The child avatar this token authorizes a claim for.</summary>
    public Guid TargetAvatarId { get; set; }

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
