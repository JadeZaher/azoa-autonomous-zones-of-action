// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.Kyc;

namespace AZOA.WebAPI.Tests.Kyc;

/// <summary>
/// Pins the two lists that must never drift apart: the adapter keys
/// <see cref="KycProviderRegistry.NormalizeAdapter"/> accepts, and the arms
/// <see cref="KycApprovalTrust.MatchesConfiguredProvider"/> maps.
///
/// <para>A key present in the first list and absent from the second produces a
/// silent, total failure: the adapter is selectable, reports itself available, and
/// still never resolves a trust profile on EITHER resolution path, so it can never
/// approve anyone. Identomat shipped in exactly that state.</para>
/// </summary>
public sealed class KycRegisteredAdapterCoverageTests
{
    /// <summary>Every adapter key the registry accepts, with the enum it must map to.</summary>
    private static readonly (string AdapterKey, KycProvider Provider)[] AcceptedAdapters =
    [
        ("manual", KycProvider.MANUAL),
        ("veriff", KycProvider.VERIFF),
        ("generic-hosted", KycProvider.GENERIC_HOSTED),
        ("hosted", KycProvider.GENERIC_HOSTED),
        (IdentomatKycProviderService.ProviderKeyValue, KycProvider.IDENTOMAT),
    ];

    public static TheoryData<string, KycProvider> RegisteredAdapters()
    {
        var data = new TheoryData<string, KycProvider>();
        foreach (var (adapterKey, provider) in AcceptedAdapters)
            data.Add(adapterKey, provider);
        return data;
    }

    [Theory]
    [MemberData(nameof(RegisteredAdapters))]
    public void EveryAdapterKeyTheRegistryAccepts_MapsToItsProviderEnum(
        string adapterKey,
        KycProvider expected)
    {
        KycProviderRegistry.NormalizeAdapter(adapterKey).Should().NotBeNull(
            "this key is part of the registry's accepted set");
        KycApprovalTrust.MatchesConfiguredProvider(adapterKey, expected).Should().BeTrue(
            "an unmapped adapter key fails trust resolution silently, so the adapter is "
            + "configurable, available, and unable to approve anyone");
    }

    [Fact]
    public void EveryRealAdapterKey_IsCoveredByThisTheory()
    {
        // Guards the guard: a NEW adapter added to RealKeys without a row above would
        // otherwise ship with the same defect and no failing test.
        var covered = AcceptedAdapters
            .Select(adapter => adapter.AdapterKey)
            .ToHashSet(StringComparer.Ordinal);

        KycRegisteredAdapters.RealKeys.Should().OnlyContain(key => covered.Contains(key));
    }

    [Fact]
    public void AnUnknownAdapterKey_MapsToNothing()
        => KycApprovalTrust.MatchesConfiguredProvider("not-an-adapter", KycProvider.IDENTOMAT)
            .Should().BeFalse();

    [Fact]
    public void TheIdentomatAdapter_ImplementsTheHostedSeamTheManagerGateChecksFor()
        => typeof(IdentomatKycProviderService).Should().BeAssignableTo<IHostedSessionProvider>();
}
