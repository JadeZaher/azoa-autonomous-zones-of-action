using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Options;
using Moq;

namespace AZOA.WebAPI.Tests.TestSupport;

internal static class HostedIdentityTestFactory
{
    public static VeriffKycProviderService Veriff(IOptions<KycSettings>? settings = null)
        => new(settings ?? Options.Create(new KycSettings()), Mock.Of<IHostedProviderAttemptStore>(),
            Mock.Of<IHostedSessionStore>(), new HostedIdentityHttp(Mock.Of<IHttpClientFactory>()));

    public static GenericHostedKycProviderService Generic(IOptions<KycSettings> settings)
        => new(settings, Mock.Of<IHostedProviderAttemptStore>(), Mock.Of<IHostedSessionStore>(),
            new HostedIdentityHttp(Mock.Of<IHttpClientFactory>()));
}
