// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Auth;

/// <summary>Redeems the single-use code the OAuth callback handed the browser.</summary>
public sealed class ExternalAuthExchangeRequest
{
    /// <summary>The opaque code from the callback redirect's query string.</summary>
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// The federated sign-in result. Deliberately the same shape the password path returns,
/// so the frontend stores one kind of session regardless of how it was obtained.
/// </summary>
public sealed class ExternalAuthTokenResponse
{
    /// <summary>The standard AZOA login JWT — identical in claims to the password path's.</summary>
    public string Token { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public string Username { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    /// <summary>Which provider completed the sign-in.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Validated same-site relative path to land on, when one was requested.</summary>
    public string? ReturnTo { get; set; }
}

/// <summary>Authorises attaching a provider to the already-authenticated user.</summary>
public sealed class ExternalAuthLinkTicketResponse
{
    /// <summary>Single-use ticket to pass as <c>?ticket=</c> on the start redirect.</summary>
    public string Ticket { get; set; } = string.Empty;

    /// <summary>Relative start path the browser should navigate to with the ticket.</summary>
    public string StartUrl { get; set; } = string.Empty;
}

/// <summary>A provider currently attached to a user.</summary>
public sealed class ExternalAuthLinkResponse
{
    public string Provider { get; set; } = string.Empty;

    /// <summary>Audit snapshot of the address the provider asserted when linked.</summary>
    public string? EmailAtLink { get; set; }

    public string? DisplayNameAtLink { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}
