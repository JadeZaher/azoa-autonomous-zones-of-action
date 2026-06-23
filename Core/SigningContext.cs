// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Core;

/// <summary>
/// The signer-resolution context threaded into the value-moving
/// <see cref="AZOA.WebAPI.Interfaces.IBlockchainProvider"/> methods
/// (<c>MintAsync</c> / <c>BurnAsync</c> / <c>TransferAsync</c>) so the provider
/// signs with the RIGHT key (value-path-wiring C1 / decision D1).
///
/// <para>It carries only identity — never key material. The provider hands it to
/// <see cref="AZOA.WebAPI.Interfaces.Managers.IKeyCustodyService"/>:
/// <list type="bullet">
/// <item><b>User-wallet op</b> (<see cref="IsPlatform"/> = <c>false</c>): resolves
/// via <c>WithSigningKeyAsync(<see cref="WalletId"/>, <see cref="AvatarId"/>, …)</c>,
/// inheriting the custody IDOR guard (the caller must own the wallet).</item>
/// <item><b>Platform / ASA-admin op</b> (<see cref="IsPlatform"/> = <c>true</c>):
/// create / destroy-burn / clawback / opt-in of the platform account — resolves
/// via <c>WithPlatformSigningKeyAsync(true, …)</c>.</item>
/// </list>
/// </para>
///
/// <para><b>Interim safety:</b> a value-moving call that arrives WITHOUT a
/// <see cref="SigningContext"/> (a <c>null</c> argument) is treated as a
/// platform/ASA-admin op (the only direct callers are platform-authority paths:
/// ASA-create, wrapped-mint, soulbound, opt-in). A user-wallet op MUST arrive
/// with <c>IsPlatform = false</c> and a real <see cref="WalletId"/>; the provider
/// returns a CLEAR ERROR (never a platform-key mis-sign) for any user context it
/// cannot resolve.</para>
/// </summary>
/// <param name="AvatarId">The authenticated owner of the moving wallet (custody
/// IDOR authority). <see cref="System.Guid.Empty"/> on a platform op.</param>
/// <param name="WalletId">The platform-managed wallet whose key signs a user op.
/// <see cref="System.Guid.Empty"/> on a platform op.</param>
/// <param name="IsPlatform"><c>true</c> for an ASA-admin op signed by the platform
/// key; <c>false</c> for a per-user custodial op.</param>
/// <param name="ActingTenantId">tenant-consent-delegation C1/AC4: the tenant that
/// DROVE this signing action via a child credential (the <c>act_as_tenant</c> JWT
/// claim), or <see cref="System.Guid.Empty"/> when the action is user-driven or a
/// pure platform-internal op. When non-empty, the custody seam does a LIVE consent
/// grant check (grantor=<see cref="GrantorAvatarId"/>, tenant=this,
/// scope ⊇ <see cref="Scope"/>) BEFORE key decrypt and FAILS CLOSED — even on a
/// platform-key op, because Grant/FungibleTokenCreate are platform-signed yet
/// tenant-driven on a user's behalf.</param>
/// <param name="GrantorAvatarId">tenant-consent-delegation AC4: the USER on whose
/// behalf a tenant-driven action is taken — the grantor the consent grant is looked
/// up against. For a per-user op this equals <see cref="AvatarId"/>; empty when
/// there is no acting tenant.</param>
/// <param name="Scope">tenant-consent-delegation AC4: the signing scope the op
/// requires (e.g. <c>swap:sign</c>, <c>nft:mint</c>). The live grant must cover it.
/// Null/empty when there is no acting tenant.</param>
public readonly record struct SigningContext(
    Guid AvatarId,
    Guid WalletId,
    bool IsPlatform,
    Guid ActingTenantId = default,
    Guid GrantorAvatarId = default,
    string? Scope = null)
{
    /// <summary>An ASA-admin / platform-authority signing context.</summary>
    public static SigningContext Platform { get; } =
        new(Guid.Empty, Guid.Empty, IsPlatform: true);

    /// <summary>
    /// A per-user custodial signing context. Both ids must be non-empty; the
    /// provider routes this through the custody IDOR-guarded resolver.
    /// </summary>
    public static SigningContext ForUser(Guid avatarId, Guid walletId) =>
        new(avatarId, walletId, IsPlatform: false);

    /// <summary>
    /// True when this is a per-user context that actually carries a resolvable
    /// wallet + avatar. A user context missing either id is unresolvable and the
    /// provider must fail closed rather than fall back to the platform key.
    /// </summary>
    public bool IsResolvableUserContext =>
        !IsPlatform && WalletId != Guid.Empty && AvatarId != Guid.Empty;

    /// <summary>
    /// tenant-consent-delegation C1/AC4: true when a tenant drove this action via a
    /// child credential — the custody seam MUST consult a live consent grant before
    /// any key decrypt. False = user-driven or platform-internal (no grant needed).
    /// </summary>
    public bool IsTenantDriven => ActingTenantId != Guid.Empty;

    /// <summary>
    /// tenant-consent-delegation AC4: stamp the acting-tenant + grantor + scope onto
    /// a signing context so the custody seam does the live grant check.
    /// <paramref name="grantorAvatarId"/> defaults to <see cref="AvatarId"/> (the
    /// wallet owner) — the user on whose behalf the tenant acts.
    /// </summary>
    public SigningContext ActingAs(Guid actingTenantId, string scope, Guid grantorAvatarId = default) =>
        this with
        {
            ActingTenantId = actingTenantId,
            GrantorAvatarId = grantorAvatarId != Guid.Empty
                ? grantorAvatarId
                : (AvatarId != Guid.Empty ? AvatarId : GrantorAvatarId),
            Scope = scope,
        };
}
