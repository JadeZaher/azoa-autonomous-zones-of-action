// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Auth;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>
/// A verified assertion from an identity provider, already validated by the framework
/// OAuth handler (state, PKCE, and the code-for-token exchange all happen upstream).
/// </summary>
/// <param name="Provider">Which provider asserted this identity.</param>
/// <param name="Subject">
/// The provider's STABLE account id — Google's <c>sub</c>, GitHub's numeric <c>id</c>.
/// Never the email: emails are mutable and reassignable, so keying on one lets a
/// recycled mailbox inherit an account.
/// </param>
/// <param name="Email">Audit-only snapshot. Never used to find an existing account.</param>
/// <param name="DisplayName">Audit-only snapshot of the provider's display name.</param>
/// <param name="FirstName">Optional given name, used only to prefill the new profile.</param>
/// <param name="LastName">Optional family name, used only to prefill the new profile.</param>
public sealed record ExternalIdentity(
    ExternalAuthProvider Provider,
    string Subject,
    string? Email = null,
    string? DisplayName = null,
    string? FirstName = null,
    string? LastName = null);

/// <summary>Maps verified federated identities onto AZOA avatars.</summary>
public interface IExternalLoginService
{
    /// <summary>
    /// Resolves the avatar bound to this federated identity within the tenant scope,
    /// creating a NEW avatar when no binding exists.
    /// </summary>
    /// <remarks>
    /// Never merges into an existing account by email. See the implementation's remarks
    /// for why that absence is the security control.
    /// </remarks>
    Task<AZOAResult<IAvatar>> ResolveOrCreateAsync(
        ExternalIdentity identity,
        Guid? tenantId,
        CancellationToken ct = default);

    /// <summary>
    /// Attaches a federated identity to an ALREADY-AUTHENTICATED avatar. This is the
    /// only path that can ever associate a provider with a pre-existing account.
    /// </summary>
    Task<AZOAResult<AvatarExternalLogin>> LinkToAvatarAsync(
        ExternalIdentity identity,
        Guid avatarId,
        Guid? tenantId,
        CancellationToken ct = default);

    /// <summary>Providers currently attached to an avatar.</summary>
    Task<AZOAResult<IReadOnlyList<AvatarExternalLogin>>> ListLinksAsync(
        Guid avatarId,
        CancellationToken ct = default);

    /// <summary>
    /// Loads an avatar that is still allowed to sign in, re-checking its active state.
    /// </summary>
    /// <remarks>
    /// Called at code-redemption time rather than trusting the avatar snapshot taken at
    /// callback time, so a deactivation in the seconds between the two takes effect.
    /// </remarks>
    Task<AZOAResult<IAvatar>> ResolveBoundAvatarAsync(
        Guid avatarId,
        CancellationToken ct = default);

    /// <summary>
    /// Detaches a provider, refusing when it is the avatar's last remaining way in.
    /// Removes the binding only — the avatar row is never cascaded.
    /// </summary>
    Task<AZOAResult<bool>> UnlinkAsync(
        ExternalAuthProvider provider,
        Guid avatarId,
        Guid? tenantId,
        CancellationToken ct = default);
}
