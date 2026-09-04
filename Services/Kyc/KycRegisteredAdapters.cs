// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Providers.Kyc;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>
/// The single list of adapter keys the node actually registers. It exists so the
/// boot guard and the registry cannot drift: an adapter added to
/// <see cref="KycProviderRegistry"/> but forgotten here would let a Production host
/// start on a provider the guard does not know is real, and vice versa.
/// </summary>
public static class KycRegisteredAdapters
{
    /// <summary>Every adapter key registered in <see cref="KycProviderRegistry"/>.</summary>
    public static readonly string[] Keys =
    [
        "manual",
        "veriff",
        "generic-hosted",
        IdentomatKycProviderService.ProviderKeyValue,
    ];

    /// <summary>
    /// Adapter keys that represent a real external identity authority. Manual is a
    /// development simulation and the generic hosted adapter is a fail-closed
    /// scaffold, so neither counts.
    /// </summary>
    public static readonly string[] RealKeys =
    [
        "veriff",
        IdentomatKycProviderService.ProviderKeyValue,
    ];
}
