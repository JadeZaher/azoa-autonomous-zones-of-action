// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class KycQuotaAndBootGuardTests
{
    // ── Per-tenant quota: the cross-tenant DoS defence ──────────────────────

    [Fact]
    public void TenantQuota_ExhaustsPerTenantWithoutAffectingOtherTenants()
    {
        // The provider's per-IP cap is ONE node-wide budget, so without this a single
        // consumer's storm locks every other tenant out of KYC entirely.
        var quota = NewQuota(beginPermits: 3);
        var noisyTenant = Guid.NewGuid();
        var quietTenant = Guid.NewGuid();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            quota.TryConsume(noisyTenant, KycQuotaOperation.BeginSession)
                .Should().BeTrue();
        }

        quota.TryConsume(noisyTenant, KycQuotaOperation.BeginSession)
            .Should().BeFalse("the noisy tenant has spent its own allowance");
        quota.TryConsume(quietTenant, KycQuotaOperation.BeginSession)
            .Should().BeTrue("another tenant must be unaffected by the first one's storm");
    }

    [Fact]
    public void TenantQuota_KeepsBeginAndScreeningBudgetsSeparate()
    {
        var quota = NewQuota(beginPermits: 1, screeningPermits: 1);
        var tenantId = Guid.NewGuid();

        quota.TryConsume(tenantId, KycQuotaOperation.BeginSession).Should().BeTrue();
        quota.TryConsume(tenantId, KycQuotaOperation.BeginSession).Should().BeFalse();
        quota.TryConsume(tenantId, KycQuotaOperation.Screening).Should().BeTrue(
            "exhausting begin-session must not also disable screening");
    }

    [Fact]
    public void TenantQuota_PartitionsNodeDirectFlowsRatherThanExemptingThem()
    {
        var quota = NewQuota(beginPermits: 1);

        quota.TryConsume(null, KycQuotaOperation.BeginSession).Should().BeTrue();
        quota.TryConsume(null, KycQuotaOperation.BeginSession).Should().BeFalse();
        quota.TryConsume(Guid.Empty, KycQuotaOperation.BeginSession).Should().BeFalse(
            "an empty tenant id must share the node-direct partition, not bypass it");
    }

    [Fact]
    public void TenantQuota_AdvertisesARetryWindow()
        => NewQuota().RetryAfterSeconds.Should().Be(60);

    // ── Shared per-IP budget ────────────────────────────────────────────────

    [Fact]
    public void RateBudget_RefusesPastTheConfiguredCeiling()
    {
        var budget = NewBudget(perMinute: 2);

        budget.TryAcquire("begin").Should().BeTrue();
        budget.TryAcquire("screening-search").Should().BeTrue();
        budget.TryAcquire("monitor-list").Should().BeFalse(
            "KYC, screening, and monitoring draw on ONE shared budget");
    }

    [Fact]
    public void RateBudget_ClampsAnOverAmbitiousCeilingToTheVendorLimit()
    {
        var budget = NewBudget(perMinute: 100_000);

        var acquired = 0;
        while (budget.TryAcquire("begin"))
        {
            acquired++;
            if (acquired > 700)
                break;
        }

        acquired.Should().Be(600, "the vendor bans the IP for an hour past 600/min");
    }

    [Fact]
    public void RateBudget_CapsResultRefetchesSoBeginAndScreeningKeepHeadroom()
    {
        var budget = NewBudget(perMinute: 10);

        var results = 0;
        while (budget.TryAcquire(results % 2 == 0 ? "result" : "kyb-result") && results < 20)
            results++;

        results.Should().Be(5, "result re-fetches (poll, refresh, callbacks) may use only the default 50% share (RV-L2 L2-3)");
        budget.TryAcquire("begin").Should().BeTrue("a refresh storm must not lock every tenant out of starting verification");
        budget.TryAcquire("screening-search").Should().BeTrue("approval-time AML screening shares the same window");
    }

    [Fact]
    public void RateBudget_OpensACircuitAfterAnObservedBan()
    {
        var budget = NewBudget(perMinute: 100);

        budget.TryAcquire("begin").Should().BeTrue();
        budget.NoteRateLimited();

        budget.IsCircuitOpen.Should().BeTrue();
        budget.CircuitOpenUntil.Should().NotBeNull();
        budget.TryAcquire("begin").Should().BeFalse(
            "calling into an active ban only extends it");
    }

    // ── Production boot guard (FR-5 AC3) ────────────────────────────────────

    [Theory]
    [InlineData("manual")]
    [InlineData("mock")]
    [InlineData("unavailable")]
    [InlineData("generic-hosted")]
    [InlineData("hosted")]
    [InlineData("")]
    [InlineData(null)]
    public void ProductionBootGuard_RejectsANonRealProvider(string? configured)
    {
        var configuration = Configuration(configured);

        var act = () => KycRuntimeSafety.GuardProductionProviderIsReal(
            Environment(Environments.Production),
            configuration,
            KycRegisteredAdapters.RealKeys);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Production*");
    }

    [Fact]
    public void ProductionBootGuard_AcceptsIdentomat()
    {
        var act = () => KycRuntimeSafety.GuardProductionProviderIsReal(
            Environment(Environments.Production),
            Configuration(IdentomatKycProviderService.ProviderKeyValue),
            KycRegisteredAdapters.RealKeys);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("IntegrationTest")]
    [InlineData("Testnet")]
    public void ProductionBootGuard_DoesNotConstrainNonProductionHosts(string environmentName)
    {
        var act = () => KycRuntimeSafety.GuardProductionProviderIsReal(
            Environment(environmentName),
            Configuration("manual"),
            KycRegisteredAdapters.RealKeys);

        act.Should().NotThrow();
    }

    [Fact]
    public void ProductionBootGuard_MessageNamesTheAcceptableProviders()
    {
        var act = () => KycRuntimeSafety.GuardProductionProviderIsReal(
            Environment(Environments.Production),
            Configuration("manual"),
            KycRegisteredAdapters.RealKeys);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{IdentomatKycProviderService.ProviderKeyValue}*");
    }

    [Fact]
    public void RegisteredAdapterList_ContainsEveryRealKey()
        => KycRegisteredAdapters.Keys.Should().Contain(KycRegisteredAdapters.RealKeys);

    // ── Strict provider-key selection ───────────────────────────────────────

    [Theory]
    [InlineData("identomat", "identomat")]
    [InlineData("IDENTOMAT", "identomat")]
    [InlineData("  Identomat  ", "identomat")]
    [InlineData("manual", "manual")]
    [InlineData("hosted", "generic-hosted")]
    public void NormalizeAdapter_ResolvesKnownKeys(string configured, string expected)
        => KycProviderRegistry.NormalizeAdapter(configured).Should().Be(expected);

    [Theory]
    [InlineData("identomatt")]
    [InlineData("ident-o-mat")]
    [InlineData("onfido")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeAdapter_UnknownKeysFallThroughToUnavailable(string? configured)
        => KycProviderRegistry.NormalizeAdapter(configured).Should().BeNull(
            "an unrecognised provider key must resolve to unavailable, never silently "
            + "inherit manual review");

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static KycTenantQuota NewQuota(int beginPermits = 20, int screeningPermits = 20)
        => new(Options.Create(new KycSettings
        {
            Identomat = new IdentomatKycSettings
            {
                TenantQuota = new KycTenantQuotaSettings
                {
                    BeginSessionsPerWindow = beginPermits,
                    ScreeningCallsPerWindow = screeningPermits,
                    WindowSeconds = 60,
                },
            },
        }));

    private static IdentomatRateBudget NewBudget(int perMinute)
        => new(Options.Create(new KycSettings
        {
            Identomat = new IdentomatKycSettings
            {
                SharedRequestsPerMinute = perMinute,
            },
        }));

    private static IHostEnvironment Environment(string environmentName)
        => Mock.Of<IHostEnvironment>(item => item.EnvironmentName == environmentName);

    private static IConfiguration Configuration(string? provider)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kyc:Provider"] = provider,
            })
            .Build();
}
