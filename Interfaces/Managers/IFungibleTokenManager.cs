// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// The thin AZOA-side seam for launching a FUNGIBLE token (an Algorand ASA with a
/// real supply + decimals) — the parallel to the supply-1 mint path that
/// <see cref="IAllocationManager"/> drives. It composes the same disciplines behind
/// one idempotent, KYC-gated entry point:
/// <list type="number">
///   <item>KYC gate (<see cref="IKycGateService.RequireVerifiedAsync"/>) —
///   fail-closed before any value-bearing side effect.</item>
///   <item>Provision-if-absent (<c>IWalletManager.GenerateWalletAsync</c> /
///   <c>IWalletStore.GetByAvatarAsync</c>) — never duplicates a wallet.</item>
///   <item>Token launch via the Algorand ASA capability module
///   (<c>IAlgorandASAModule.CreateASAAsync</c>) deduped via
///   <see cref="AZOA.WebAPI.Interfaces.IIdempotencyStore"/>.</item>
/// </list>
///
/// AZOA runs NO token economics: the total supply + decimals are tenant-supplied
/// and authoritative. AZOA materialises the custodial wallet (if absent) and
/// creates the on-chain asset exactly once.
/// </summary>
public interface IFungibleTokenManager
{
    /// <summary>
    /// Idempotent, KYC-gated wallet-provision + fungible-token (ASA) launch.
    ///
    /// Idempotency: a client key wins; absent ⇒ a deterministic content key over
    /// (<paramref name="avatarId"/>, name, unit, total, decimals, chain) — NEVER a
    /// random per-request key. The key is partitioned by <paramref name="apiKeyId"/>
    /// so two tenants reusing the same human-friendly key never collide. A duplicate
    /// call under the same (apiKeyId, key) returns the ORIGINAL result and creates
    /// NO second token.
    ///
    /// Fail-closed KYC: when the target avatar is not APPROVED the call is rejected
    /// and no value-bearing side effect runs.
    ///
    /// IDOR: the launch always targets <paramref name="avatarId"/> (resolved from
    /// the authenticated principal / run context). Any owner id on the request body
    /// is ignored.
    /// </summary>
    /// <param name="avatarId">The authorised target avatar (route/run context).</param>
    /// <param name="request">Token descriptor (name, unit, total, decimals, chain).</param>
    /// <param name="callerAvatarId">
    /// The authenticated caller. Recorded for diagnostics; it does not widen
    /// authority beyond the API-key scope.
    /// </param>
    /// <param name="clientIdempotencyKey">
    /// The caller's idempotency key. Null ⇒ deterministic content-key fallback.
    /// </param>
    /// <param name="apiKeyId">The idempotency partition (the API-key id).</param>
    /// <param name="actingTenantId">
    /// tenant-consent-delegation AC4b: when a tenant DRIVES this (platform-signed)
    /// token launch via a child credential, the acting tenant id flows to the custody
    /// seam (via the <c>CreateASAAsync(..., SigningContext)</c> overload) so the live
    /// consent check runs before key decrypt. Null = user-driven / platform-internal.
    /// </param>
    Task<AZOAResult<FungibleTokenResult>> CreateAsync(
        Guid avatarId,
        FungibleTokenCreateRequest request,
        Guid callerAvatarId,
        string? clientIdempotencyKey,
        string apiKeyId,
        Guid? actingTenantId = null);
}
