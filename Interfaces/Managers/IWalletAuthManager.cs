using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// Wallet-challenge authentication + claim orchestration (user-sovereign-identity §1/§2).
/// Owns the challenge issue → atomic single-use consume → ed25519 verify →
/// create-or-login pipeline (AC1/AC2), the authenticated wallet-link (AC2b), and the
/// tenant-provisioned-avatar claim flow with its post-claim watermark cut (AC3/AC3b/AC4).
///
/// <para>IDOR rule (mirrors <see cref="ITenantManager"/> /
/// <c>AvatarManager.UpdateAsync</c>): every owned-identity id is taken from a
/// server-trusted principal (authenticated avatar id or a single-use token), NEVER a
/// request body field. Cross-tenant targets resolve to
/// <see cref="TenantAuthorizationError.NotFound"/>.</para>
/// </summary>
public interface IWalletAuthManager
{
    /// <summary>
    /// AC1/AC1b: issues a one-time, ≤5-min, single-use nonce bound to
    /// <c>(address, chainType)</c>, wrapped in a domain-separated message the user signs.
    /// </summary>
    Task<AZOAResult<WalletChallengeResponse>> CreateChallengeAsync(
        string address, string chainType, CancellationToken ct = default);

    /// <summary>
    /// AC1/AC2: atomically consumes the live challenge, verifies the ed25519 signature
    /// over the stored domain message, then create-or-login ONLY — a never-seen
    /// <c>(address, chainType)</c> mints a new self-owned avatar
    /// (<c>OwnerTenantId == null</c>); a wallet-bound address logs into THAT avatar.
    /// Returns the standard login JWT.
    /// </summary>
    Task<AZOAResult<WalletAuthTokenResponse>> VerifyAsync(
        string address, string chainType, string signature, string? message, CancellationToken ct = default);

    /// <summary>
    /// AC2b: binds a wallet to the ALREADY-AUTHENTICATED avatar after a challenge+signature
    /// proof. Rejects if that <c>(address, chainType)</c> is already bound to a different
    /// avatar. The avatar id comes from the caller's JWT, never a body field.
    /// </summary>
    Task<AZOAResult<bool>> LinkWalletAsync(
        Guid authedAvatarId, string address, string chainType, string signature, string? message,
        CancellationToken ct = default);

    /// <summary>
    /// AC4: a tenant mints a single-use, short-TTL claim token for a child it owns
    /// (ownership asserted; cross-tenant/unowned target → NotFound). The token alone
    /// never sets a credential (M1).
    /// </summary>
    Task<AZOAResult<ClaimInviteResponse>> CreateClaimInviteAsync(
        Guid tenantId, Guid childAvatarId, CancellationToken ct = default);

    /// <summary>
    /// AC3/AC3b/AC4: claims a tenant-provisioned avatar with a USER-SIDE credential
    /// (a fresh wallet challenge OR a user-chosen password). Resolves the target from a
    /// single-use claim token OR the authenticated child-JWT subject (never a body
    /// field). On success: sets the user's own credential, clears
    /// <c>OwnerTenantId</c> to null, stamps <c>AuthNotBefore = now</c> (closes the
    /// residual child-JWT window), and returns a fresh login JWT.
    /// </summary>
    Task<AZOAResult<WalletAuthTokenResponse>> ClaimAsync(
        Guid? authedAvatarId,
        string? claimToken,
        string? newPassword,
        string? address,
        string? chainType,
        string? signature,
        string? message,
        CancellationToken ct = default);
}
