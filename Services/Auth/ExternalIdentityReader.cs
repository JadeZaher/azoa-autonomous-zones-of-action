// SPDX-License-Identifier: UNLICENSED

using System.Security.Claims;
using AZOA.WebAPI.Models.Auth;

namespace AZOA.WebAPI.Services.Auth;

/// <summary>
/// Projects the provider's validated principal onto an <see cref="ExternalIdentity"/>.
/// </summary>
/// <remarks>
/// The principal reaching this type has already been produced by the framework OAuth
/// handler, which validated <c>state</c>, verified PKCE, and performed the token
/// exchange. This type only decides WHICH claim is the stable subject.
/// </remarks>
public static class ExternalIdentityReader
{
    /// <summary>
    /// Reads the identity, or null when the provider asserted no usable stable subject.
    /// </summary>
    public static ExternalIdentity? Read(ExternalAuthProvider provider, ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var subject = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(subject))
            return null;

        // Defence in depth for D17's central rule. Google's `sub` and GitHub's `id` are
        // both opaque numeric strings; an '@' here would mean a provider (or a future
        // claim-action edit) had substituted the EMAIL as the subject, which would make
        // a reassigned mailbox inherit the account. Refuse rather than bind.
        if (subject.Contains('@', StringComparison.Ordinal))
            return null;

        var email = principal.FindFirst(ClaimTypes.Email)?.Value?.Trim();

        return new ExternalIdentity(
            provider,
            subject,
            Email: string.IsNullOrWhiteSpace(email) ? null : email,
            DisplayName: principal.FindFirst(ClaimTypes.Name)?.Value?.Trim(),
            FirstName: principal.FindFirst(ClaimTypes.GivenName)?.Value?.Trim(),
            LastName: principal.FindFirst(ClaimTypes.Surname)?.Value?.Trim());
    }
}
