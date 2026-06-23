namespace AZOA.WebAPI.Models.Requests;

/// <summary>
/// Body for <c>POST api/avatar/claim</c> (user-sovereign-identity AC3/AC3b/AC4). The
/// user takes ownership of a tenant-provisioned avatar by setting a USER-SIDE
/// credential (M1) — a fresh wallet challenge signature OR a user-chosen password.
///
/// <para><b>IDOR (AC5):</b> the claimed avatar id is NEVER read from this body. It is
/// resolved from either the authenticated child-JWT subject OR the single-use
/// <see cref="ClaimToken"/> — both server-trusted principals. A cross-tenant claim
/// resolves to NotFound.</para>
/// </summary>
public class ClaimAvatarRequest
{
    /// <summary>
    /// OPTIONAL single-use claim-invite token (AC4). When present it identifies the
    /// target avatar and is atomically redeemed. When absent the claim targets the
    /// authenticated child-JWT subject.
    /// </summary>
    public string? ClaimToken { get; set; }

    // ── User-side credential (set EXACTLY ONE; never derivable from a child JWT) ──

    /// <summary>A user-chosen password to set as the avatar's own login credential.</summary>
    public string? NewPassword { get; set; }

    /// <summary>Wallet address for a wallet-challenge credential (paired with the signature).</summary>
    public string? Address { get; set; }

    /// <summary>Chain type for the wallet-challenge credential.</summary>
    public string? ChainType { get; set; }

    /// <summary>Signature over the wallet challenge domain message proving control of the wallet.</summary>
    public string? Signature { get; set; }

    /// <summary>OPTIONAL exact-equality echo of the signed domain message (see WalletVerifyRequest).</summary>
    public string? Message { get; set; }
}
