// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Models.Auth;

/// <summary>Federated sign-in providers this node can speak to (Phase 1B, D18).</summary>
/// <remarks>
/// ONE enum, shared by the domain model and the SurrealDB POCO. A second
/// POCO-local copy is exactly how a durable park state ends up missing from an
/// <c>[Inside]</c> constraint — see Persistence/SurrealDb/Models/AGENTS.md.
/// </remarks>
public enum ExternalAuthProvider
{
    /// <summary>Google, via Microsoft.AspNetCore.Authentication.Google.</summary>
    GOOGLE,

    /// <summary>GitHub, via the generic OAuth handler.</summary>
    GITHUB
}

/// <summary>Canonical wire names for <see cref="ExternalAuthProvider"/> route segments.</summary>
public static class ExternalAuthProviderNames
{
    public const string Google = "google";
    public const string GitHub = "github";

    /// <summary>Maps a route segment to a provider; false for anything unrecognised.</summary>
    /// <remarks>
    /// Fail-closed by construction: an unknown segment never falls through to a
    /// default provider, because a silently-substituted provider is the defect the
    /// KYC boot guard exists to catch (Services/Kyc/KycRuntimeSafety.cs).
    /// </remarks>
    public static bool TryParse(string? routeSegment, out ExternalAuthProvider provider)
    {
        switch (routeSegment?.Trim().ToLowerInvariant())
        {
            case Google:
                provider = ExternalAuthProvider.GOOGLE;
                return true;
            case GitHub:
                provider = ExternalAuthProvider.GITHUB;
                return true;
            default:
                provider = default;
                return false;
        }
    }

    /// <summary>The route segment / authentication scheme name for a provider.</summary>
    public static string ToRouteSegment(ExternalAuthProvider provider) => provider switch
    {
        ExternalAuthProvider.GOOGLE => Google,
        ExternalAuthProvider.GITHUB => GitHub,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown external auth provider.")
    };
}
