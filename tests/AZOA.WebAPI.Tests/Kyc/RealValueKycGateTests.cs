using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Bridge;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class RealValueKycGateTests
{
    private readonly Mock<IKycStore> _store = new();
    private readonly Mock<IKycProviderService> _provider = ReadyProvider(
        KycProvider.VERIFF,
        "veriff");

    [Fact]
    public async Task CurrentFiniteApproval_Succeeds()
    {
        var avatarId = Guid.NewGuid();
        SetupLatest(avatarId, KycStatus.APPROVED, DateTimeOffset.UtcNow.AddDays(1));

        var result = await Gate()
            .RequireCurrentApprovalAsync(avatarId);

        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
    }

    [Theory]
    [InlineData(KycStatus.APPROVED, null)]
    [InlineData(KycStatus.APPROVED, -1)]
    [InlineData(KycStatus.REJECTED, 1)]
    [InlineData(KycStatus.PENDING, 1)]
    public async Task NonCurrentApproval_FailsClosed(KycStatus status, int? expiryOffsetDays)
    {
        var avatarId = Guid.NewGuid();
        SetupLatest(
            avatarId,
            status,
            expiryOffsetDays.HasValue
                ? DateTimeOffset.UtcNow.AddDays(expiryOffsetDays.Value)
                : null);

        var result = await Gate()
            .RequireCurrentApprovalAsync(avatarId);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
        result.Message.Should().StartWith(KycAuthorizationError.Forbidden);
    }

    [Fact]
    public async Task AuthorityReadFailure_FailsClosedWithoutLeakingStoreMessage()
    {
        var avatarId = Guid.NewGuid();
        var exception = new InvalidOperationException("internal storage detail");
        _store.Setup(item => item.GetLatestSubmissionByAvatarAsync(
                avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<KycSubmission>
            {
                IsError = true,
                Message = "sensitive store failure",
                Exception = exception,
            });

        var result = await Gate()
            .RequireCurrentApprovalAsync(avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().NotContain("store failure");
        result.Exception.Should().BeSameAs(exception);
    }

    [Fact]
    public async Task RetiredPolicyApproval_FailsClosed()
    {
        var avatarId = Guid.NewGuid();
        SetupLatest(avatarId, KycStatus.APPROVED, DateTimeOffset.UtcNow.AddDays(1));
        _store.Setup(item => item.GetLatestSubmissionByAvatarAsync(
                avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(Submission(
                avatarId,
                KycStatus.APPROVED,
                DateTimeOffset.UtcNow.AddDays(1),
                new KycApprovalProfile(KycProvider.VERIFF, "veriff", "retired-v1", "substantial"))));

        var result = await Gate().RequireCurrentApprovalAsync(avatarId);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ManualApprovalInProduction_FailsClosedDespiteAllowList()
    {
        var avatarId = Guid.NewGuid();
        var provider = ReadyProvider(KycProvider.MANUAL, "manual");
        var profile = new KycApprovalProfile(
            KycProvider.MANUAL,
            "manual",
            "manual-v1",
            "development-manual");
        _store.Setup(item => item.GetLatestSubmissionByAvatarAsync(
                avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(Submission(
                avatarId,
                KycStatus.APPROVED,
                DateTimeOffset.UtcNow.AddDays(1),
                profile)));
        var settings = TrustedSettings("manual-v1", "development-manual", "manual");
        settings.ApprovalPolicy.AllowManualInDevelopment = true;
        var gate = new RealValueKycGate(
            _store.Object,
            provider.Object,
            Options.Create(settings),
            HostEnvironment(Environments.Production));

        var result = await gate.RequireCurrentApprovalAsync(avatarId);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ManualApprovalInDevelopment_StillCannotAuthorizeRealValue()
    {
        var avatarId = Guid.NewGuid();
        var provider = ReadyProvider(KycProvider.MANUAL, "manual");
        var profile = new KycApprovalProfile(
            KycProvider.MANUAL,
            "manual",
            "manual-v1",
            "development-manual");
        _store.Setup(item => item.GetLatestSubmissionByAvatarAsync(
                avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(Submission(
                avatarId,
                KycStatus.APPROVED,
                DateTimeOffset.UtcNow.AddDays(1),
                profile)));
        var settings = TrustedSettings("manual-v1", "development-manual", "manual");
        settings.ApprovalPolicy.AllowManualInDevelopment = true;
        var gate = new RealValueKycGate(
            _store.Object,
            provider.Object,
            Options.Create(settings),
            HostEnvironment(Environments.Development));

        var result = await gate.RequireCurrentApprovalAsync(avatarId);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
    }

    private void SetupLatest(Guid avatarId, KycStatus status, DateTimeOffset? expiresAt)
    {
        _store.Setup(item => item.GetLatestSubmissionByAvatarAsync(
                avatarId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(Submission(
                avatarId,
                status,
                expiresAt,
                CurrentProfile())));
    }

    private RealValueKycGate Gate() => new(
        _store.Object,
        _provider.Object,
        Options.Create(TrustedSettings("production-v1", "substantial", "veriff")),
        HostEnvironment(Environments.Production));

    private static KycSubmission Submission(
        Guid avatarId,
        KycStatus status,
        DateTimeOffset? expiresAt,
        KycApprovalProfile profile) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        AvatarId = avatarId.ToString("N"),
        Provider = profile.Provider,
        ProviderKey = profile.ProviderKey,
        ProviderResult = KycApprovalTrust.CreateEnvelope(profile),
        Status = status,
        ExpiresAt = expiresAt,
    };

    private static Mock<IKycProviderService> ReadyProvider(KycProvider kind, string key)
    {
        var provider = new Mock<IKycProviderService>();
        provider.SetupGet(item => item.Provider).Returns(kind);
        provider.SetupGet(item => item.ProviderKey).Returns(key);
        provider.Setup(item => item.GetCapabilities()).Returns(new KycProviderCapabilitiesModel
        {
            Provider = kind,
            ProviderKey = key,
            Available = true,
        });
        return provider;
    }

    private static KycSettings TrustedSettings(string policyVersion, string assurance, string providerKey) => new()
    {
        Provider = providerKey,
        ApprovalPolicy = new KycApprovalPolicySettings
        {
            PolicyVersion = policyVersion,
            AssuranceLevel = assurance,
            TrustedProviderKeys = [providerKey],
        },
    };

    private static KycApprovalProfile CurrentProfile() => new(
        KycProvider.VERIFF,
        "veriff",
        "production-v1",
        "substantial");

    private static IHostEnvironment HostEnvironment(string name)
        => Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == name);
}
