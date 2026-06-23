using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Stores;

/// <summary>
/// Persistence seam for the tenant-minted claim-invite token
/// (<see cref="WalletAuthClaimToken"/>, user-sovereign-identity §2, AC4).
///
/// <para>Like the auth nonce, the single-use guarantee is the atomic
/// <see cref="TryConsumeAsync"/> conditional UPDATE — a redeemed token can never be
/// re-presented. The token only AUTHORIZES the claim; the user-side credential is set
/// separately by the manager (M1).</para>
/// </summary>
public interface IWalletAuthClaimTokenStore
{
    /// <summary>Persists a freshly-minted claim token (CREATE; UNIQUE on token).</summary>
    Task<AZOAResult<WalletAuthClaimToken>> CreateAsync(WalletAuthClaimToken token, CancellationToken ct = default);

    /// <summary>
    /// Loads a claim token by its UNIQUE value, or <c>Result == null</c> with no error
    /// when none matches. Used to resolve the target avatar before redemption.
    /// </summary>
    Task<AZOAResult<WalletAuthClaimToken>> GetByTokenAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// ATOMIC single-use redeem (AC4). A conditional UPDATE that succeeds iff the token
    /// is currently unconsumed AND unexpired at <paramref name="now"/>; returns
    /// <c>true</c> iff exactly one row was consumed. No-TOCTOU — the caller MUST treat a
    /// <c>false</c> as "already redeemed/expired → reject".
    /// </summary>
    Task<AZOAResult<bool>> TryConsumeAsync(string token, DateTime now, CancellationToken ct = default);
}
