// SPDX-License-Identifier: UNLICENSED

using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Kyc;

/// <summary>
/// The two resolution paths must admit the same providers. <c>KycManager</c> gates
/// hosted verification on the type test <c>resolution.Provider is
/// IHostedSessionProvider</c>, so if <c>ResolveTenantAsync</c>'s profile-bound
/// wrapper hides the seam while <c>ResolveNodeDefault</c> returns the raw adapter,
/// the same provider is admitted or refused purely by route — and the tenant-scoped
/// route is the acceptance path.
/// </summary>
public sealed class KycProviderRegistrySeamTests
{
    private const string ProfileKey = "tenant-identomat";

    [Fact]
    public async Task ResolveTenant_ReturnsAProviderThatStillImplementsTheHostedSeam()
    {
        var tenantId = Guid.NewGuid();
        var registry = NewRegistry(tenantId);

        var resolved = await registry.ResolveTenantAsync(tenantId);

        resolved.IsError.Should().BeFalse(resolved.Message);
        resolved.Result!.Provider.Should().BeAssignableTo<IHostedSessionProvider>(
            "the wrapper must FORWARD the seam; hiding it refuses tenant-scoped "
            + "Identomat while node-default resolution admits the same adapter");
    }

    [Fact]
    public async Task ResolveTenant_StillRebindsThePublicProviderKeyToTheProfile()
    {
        // Forwarding must not become unwrapping: the wrapper exists to rebind the
        // adapter's public key to the operator profile, and that binding is what the
        // approval envelope is validated against.
        var tenantId = Guid.NewGuid();
        var registry = NewRegistry(tenantId);

        var resolved = await registry.ResolveTenantAsync(tenantId);

        resolved.Result!.Provider.ProviderKey.Should().Be(ProfileKey);
        ((IHostedSessionProvider)resolved.Result.Provider).GetHostedCapabilities()
            .ProviderKey.Should().Be(ProfileKey,
                "the hosted capability report carries the profile key too, not the "
                + "adapter's own");
    }

    [Fact]
    public async Task ResolveTenant_AndResolveNodeDefault_AgreeOnTheSameAdapter()
    {
        var tenantId = Guid.NewGuid();
        var registry = NewRegistry(tenantId);

        var tenant = await registry.ResolveTenantAsync(tenantId);
        var nodeDefault = registry.ResolveNodeDefault();

        tenant.IsError.Should().BeFalse(tenant.Message);
        nodeDefault.IsError.Should().BeFalse(nodeDefault.Message);
        (tenant.Result!.Provider is IHostedSessionProvider)
            .Should().Be(nodeDefault.Result!.Provider is IHostedSessionProvider,
                "one provider, one answer, whichever route reached it");
    }

    private static KycProviderRegistry NewRegistry(Guid tenantId)
    {
        var settings = IdentomatTestFactory.Options();
        settings.Value.ApprovalPolicy = new KycApprovalPolicySettings
        {
            PolicyVersion = "identomat-v1",
            AssuranceLevel = "substantial",
            TrustedProviderKeys = [IdentomatKycProviderService.ProviderKeyValue],
        };

        var control = new Mock<IKycControlStore>();
        control.Setup(item => item.GetSelectionAsync(tenantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<TenantKycProviderSelection?>
            {
                Result = new TenantKycProviderSelection
                {
                    Id = tenantId.ToString("N"),
                    TenantId = tenantId.ToString("N"),
                    ProviderKey = ProfileKey,
                    SelectionVersion = 1,
                },
            });
        control.Setup(item => item.GetProfileAsync(ProfileKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<KycProviderProfile?>
            {
                Result = new KycProviderProfile
                {
                    Id = ProfileKey,
                    DisplayName = "Tenant Identomat",
                    AdapterKey = IdentomatKycProviderService.ProviderKeyValue,
                    Enabled = true,
                    PolicyVersion = "identomat-v1",
                    AssuranceLevel = "substantial",
                    Version = 1,
                    TrustRevision = 1,
                },
            });

        return new KycProviderRegistry(
            control.Object,
            new ManualKycProviderService(settings),
            AZOA.WebAPI.Tests.TestSupport.HostedIdentityTestFactory.Veriff(),
            AZOA.WebAPI.Tests.TestSupport.HostedIdentityTestFactory.Generic(settings),
            IdentomatTestFactory.Create(settings),
            settings,
            Mock.Of<IHostEnvironment>(item => item.EnvironmentName == Environments.Production),
            new ConfigurationBuilder().Build());
    }
}
