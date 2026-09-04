// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Auth;

/// <summary>What a single-use <c>oauth_exchange_code</c> row authorises.</summary>
public enum OAuthExchangeCodePurpose
{
    /// <summary>Redeemable once for an AZOA login JWT by the SPA that started the flow.</summary>
    SESSION,

    /// <summary>
    /// Proves an already-authenticated avatar explicitly asked to attach a provider.
    /// Minted only from a JWT-authenticated request, then presented on the top-level
    /// OAuth redirect, which carries no Authorization header of its own.
    /// </summary>
    LINK
}
