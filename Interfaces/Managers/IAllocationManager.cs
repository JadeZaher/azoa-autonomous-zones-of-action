// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Interfaces.Managers;

/// <summary>
/// The thin AZOA-side seam a fiat-settlement tenant calls AFTER money has
/// already cleared on its own platform: "provision a custodial wallet for
/// avatar X if absent, and allocate an already-decided amount of asset A into
/// it." This composes three existing primitives behind one idempotent,
/// KYC-gated entry point:
/// <list type="number">
///   <item>KYC gate (<see cref="IKycGateService.RequireVerifiedAsync"/>) —
///   fail-closed before any value-bearing side effect.</item>
///   <item>Provision-if-absent (<c>IWalletManager.GenerateWalletAsync</c> /
///   <c>IWalletStore.GetByAvatarAsync</c>) — never duplicates a wallet.</item>
///   <item>Allocation (<c>INftManager.MintAsync</c> / <c>TransferAsync</c>)
///   deduped via <see cref="AZOA.WebAPI.Interfaces.IIdempotencyStore"/>.</item>
/// </list>
///
/// AZOA holds NO payment-provider secret, runs NO checkout, NO webhook handler,
/// and NO token economics — those stay in the tenant. AZOA receives an
/// already-decided amount and the tenant's idempotency key.
/// </summary>
public interface IAllocationManager
{
    /// <summary>
    /// Idempotent, KYC-gated wallet-provision + asset-allocation primitive.
    ///
    /// Idempotency: a client key wins; absent ⇒ a deterministic content key over
    /// (<paramref name="avatarId"/>, asset descriptor, amount) — NEVER a random
    /// per-request key. The key is partitioned by <paramref name="apiKeyId"/> so
    /// two tenants reusing the same human-friendly key never collide. A duplicate
    /// call under the same (apiKeyId, key) returns the ORIGINAL result and
    /// performs NO second mint/transfer.
    ///
    /// Fail-closed KYC: when the target avatar is not APPROVED the call is
    /// rejected with a <c>KycAuthorizationError.Forbidden</c>-prefixed message and
    /// no value-bearing side effect runs. Per D3 wallet provisioning MAY precede
    /// approval, but allocation may not — so the gate sits before the
    /// mint/transfer.
    ///
    /// IDOR: the allocation always targets <paramref name="avatarId"/> (resolved
    /// from the authenticated principal / contract). Any owner id on the request
    /// body is ignored.
    /// </summary>
    /// <param name="avatarId">The authorised target avatar (route/contract).</param>
    /// <param name="request">Asset descriptor + already-decided amount (D4).</param>
    /// <param name="callerAvatarId">
    /// The authenticated caller (the tenant's avatar). Recorded for diagnostics;
    /// it does not widen authority beyond the API-key scope.
    /// </param>
    /// <param name="clientIdempotencyKey">
    /// The tenant's <c>Idempotency-Key</c> header (e.g. the PaymentIntent id).
    /// Null ⇒ deterministic content-key fallback.
    /// </param>
    /// <param name="apiKeyId">
    /// The <c>ApiKeyId</c> claim — the idempotency partition.
    /// </param>
    /// <param name="actingTenantId">
    /// tenant-consent-delegation AC4: when this allocation is DRIVEN by a tenant via
    /// a child credential (the controller reads the <c>act_as_tenant</c> claim), the
    /// acting tenant id is stamped onto the blockchain op so the signing seam runs
    /// the live consent check before key decrypt. Null = user-driven (no grant
    /// required).
    /// </param>
    Task<AZOAResult<AllocationResult>> AllocateAsync(
        Guid avatarId,
        AllocationRequest request,
        Guid callerAvatarId,
        string? clientIdempotencyKey,
        string apiKeyId,
        Guid? actingTenantId = null);
}
