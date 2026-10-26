// SPDX-License-Identifier: UNLICENSED

using Microsoft.Extensions.Options;
using Moq;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>
/// Construction helpers for the Identomat adapter under test. The adapter has four
/// collaborators, so a shared factory keeps every test from re-stating the same
/// wiring and drifting when the constructor changes.
/// </summary>
public static class IdentomatTestFactory
{
    /// <summary>A fully-populated, valid Identomat configuration.</summary>
    public static IdentomatKycSettings CompleteSettings() => new()
    {
        BeginBaseUrl = "https://external-api.identomat.com",
        ResultBaseUrl = "https://widget.identomat.com",
        CompanyKey = "company-key-value",
        ConfigId = "config-id-value",
        // >= KycPersonKeyDeriver.MinimumSecretLength: the adapter now refuses a short
        // callback secret, because it is the only authentication on an anonymous endpoint.
        CallbackApiKey = "callback-api-key-value-0123456789abcdef",
    };

    /// <summary>Wraps Identomat settings in the full KYC options object.</summary>
    public static IOptions<KycSettings> Options(
        IdentomatKycSettings? identomat = null,
        KycIdentitySettings? identity = null)
        => Microsoft.Extensions.Options.Options.Create(new KycSettings
        {
            Provider = IdentomatKycProviderService.ProviderKeyValue,
            Identomat = identomat ?? CompleteSettings(),
            Identity = identity ?? new KycIdentitySettings
            {
                PersonKeySecret = new string('k', 48),
            },
        });

    /// <summary>Builds the adapter with mocked collaborators unless overridden.</summary>
    public static IdentomatKycProviderService Create(
        IOptions<KycSettings>? options = null,
        IIdentomatHttpTransport? transport = null,
        IKycTenantQuota? quota = null,
        IHostedSessionFlow? flow = null,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
        Microsoft.Extensions.Hosting.IHostEnvironment? environment = null)
        => new(
            options ?? Options(),
            transport ?? Mock.Of<IIdentomatHttpTransport>(),
            quota ?? AlwaysAllowQuota(),
            flow ?? Mock.Of<IHostedSessionFlow>(),
            configuration: configuration,
            environment: environment);

    /// <summary>A quota that never refuses; use a strict mock to test refusal.</summary>
    public static IKycTenantQuota AlwaysAllowQuota()
    {
        var quota = new Mock<IKycTenantQuota>();
        quota.Setup(item => item.TryConsume(It.IsAny<Guid?>(), It.IsAny<KycQuotaOperation>()))
            .Returns(true);
        quota.SetupGet(item => item.RetryAfterSeconds).Returns(60);
        return quota.Object;
    }
}
