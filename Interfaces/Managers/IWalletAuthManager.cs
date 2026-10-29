using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// Wallet-challenge authentication + claim orchestration (user-sovereign-identity §1/§2).
/// Owns the challenge issue → atomic single-use consume → ed25519 verify →
/// create-or-login pipeline (AC1/AC2), the authenticated wallet-link (AC2b), and the
/// tenant-provisioned-user claim flow with its post-claim watermark cut (AC3/AC3b/AC4).
///
/// <para>IDOR rule (mirrors <see cref="ITenantManager"/> /
/// <c>UserManager.UpdateAsync</c>): every owned-identity id is taken from a
/// server-trusted principal (authenticated user id or a single-use token), NEVER a
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
    /// <c>(address, chainType)</c> mints a new self-owned user
    /// (<c>OwnerTenantId == null</c>); a wallet-bound address logs into THAT user.
    /// Returns the standard login JWT.
    /// </summary>
    Task<AZOAResult<WalletAuthTokenResponse>> VerifyAsync(
        string address, string chainType, string signature, string? message, CancellationToken ct = default);

    /// <summary>
    /// AC2b: binds a wallet to the ALREADY-AUTHENTICATED user after a challenge+signature
    /// proof. Rejects if that <c>(address, chainType)</c> is already bound to a different
    /// user. The user id comes from the caller's JWT, never a body field.
    /// </summary>
    Task<AZOAResult<bool>> LinkWalletAsync(
        Guid authedUserId, string address, string chainType, string signature, string? message,
        CancellationToken ct = default);

    /// <summary>Issues a purpose-bound ownership-proof challenge naming <paramref name="userId"/>, <paramref name="walletId"/>,
    /// the chain and address; a login challenge never satisfies an ownership proof, nor the reverse.</summary>
    Task<AZOAResult<WalletChallengeResponse>> CreateOwnershipChallengeAsync(
        Guid userId, Guid walletId, string address, string chainType, CancellationToken ct = default);

    /// <summary>Consumes the ownership-proof challenge issued for exactly this user, wallet, chain and address and verifies its
    /// signature; returns the canonical <c>(Address, ChainType)</c> proved. No identity is created or changed.</summary>
    Task<AZOAResult<(string Address, string ChainType)>> VerifyOwnershipProofAsync(
        Guid userId, Guid walletId, string address, string chainType, string signature, string? message, CancellationToken ct = default);

    /// <summary>
    /// AC4: a tenant mints a single-use claim invite for a child it owns (ownership asserted;
    /// cross-tenant/unowned target → NotFound). Only the token's digest is stored and the raw
    /// token is withheld from the tenant; see Services/Auth/AGENTS.md §claim-invites.
    /// </summary>
    Task<AZOAResult<ClaimInviteResponse>> CreateClaimInviteAsync(
        Guid tenantId, Guid childUserId, CancellationToken ct = default);

    /// <summary>Claims the exact invited identity with a new user credential; use normal login to recover a lost successful response.</summary>
    Task<AZOAResult<WalletAuthTokenResponse>> ClaimAsync(
        Guid expectedUserId,
        string? claimToken,
        string? newPassword,
        string? address,
        string? chainType,
        string? signature,
        string? message,
        CancellationToken ct = default);
}
