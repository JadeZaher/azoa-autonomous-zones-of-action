using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class KycGateServiceRealValueTests
{
    private readonly Mock<IKycStore> _store = new();
    private readonly InMemoryKycScreeningStore _screening = new();
    private readonly Mock<IKycProviderService> _provider = ReadyProvider(
        KycProvider.VERIFF,
        "veriff");

    [Fact]
    public async Task CurrentFiniteApproval_Succeeds()
    {
        var userId = Guid.NewGuid();
        SetupLatest(userId, KycStatus.APPROVED, DateTimeOffset.UtcNow.AddDays(1));
        _screening.SeedResult(userId, null, KycScreeningDecision.NO_HIT, lineageKey: "fixture-lineage");

        var result = await Gate()
            .RequireCurrentApprovalAsync(userId);

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
        var userId = Guid.NewGuid();
        SetupLatest(
            userId,
            status,
            expiryOffsetDays.HasValue
                ? DateTimeOffset.UtcNow.AddDays(expiryOffsetDays.Value)
                : null);

        var result = await Gate()
            .RequireCurrentApprovalAsync(userId);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
        result.Message.Should().StartWith(KycAuthorizationError.Forbidden);
    }

    [Fact]
    public async Task AuthorityReadFailure_FailsClosedWithoutLeakingStoreMessage()
    {
        var userId = Guid.NewGuid();
        var exception = new InvalidOperationException("internal storage detail");
        _store.Setup(item => item.GetLatestSubmissionByUserAsync(
                userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<KycSubmission>
            {
                IsError = true,
                Message = "sensitive store failure",
                Exception = exception,
            });

        var result = await Gate()
            .RequireCurrentApprovalAsync(userId);

        result.IsError.Should().BeTrue();
        result.Message.Should().NotContain("store failure");
        result.Exception.Should().BeSameAs(exception);
    }

    [Fact]
    public async Task RetiredPolicyApproval_FailsClosed()
    {
        var userId = Guid.NewGuid();
        SetupLatest(userId, KycStatus.APPROVED, DateTimeOffset.UtcNow.AddDays(1));
        _store.Setup(item => item.GetLatestSubmissionByUserAsync(
                userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(Submission(
                userId,
                KycStatus.APPROVED,
                DateTimeOffset.UtcNow.AddDays(1),
                new KycApprovalProfile(KycProvider.VERIFF, "veriff", "retired-v1", "substantial"))));

        var result = await Gate().RequireCurrentApprovalAsync(userId);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ManualApprovalInProduction_FailsClosedDespiteAllowList()
    {
        var userId = Guid.NewGuid();
        var provider = ReadyProvider(KycProvider.MANUAL, "manual");
        var profile = new KycApprovalProfile(
            KycProvider.MANUAL,
            "manual",
            "manual-v1",
            "development-manual");
        _store.Setup(item => item.GetLatestSubmissionByUserAsync(
                userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(Submission(
                userId,
                KycStatus.APPROVED,
                DateTimeOffset.UtcNow.AddDays(1),
                profile)));
        var settings = TrustedSettings("manual-v1", "development-manual", "manual");
        settings.ApprovalPolicy.AllowManualInDevelopment = true;
        var gate = new KycGateService(
            _store.Object,
            _screening,
            provider.Object,
            Options.Create(settings),
            HostEnvironment(Environments.Production));

        var result = await gate.RequireCurrentApprovalAsync(userId);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ManualApprovalInDevelopment_StillCannotAuthorizeRealValue()
    {
        var userId = Guid.NewGuid();
        var provider = ReadyProvider(KycProvider.MANUAL, "manual");
        var profile = new KycApprovalProfile(
            KycProvider.MANUAL,
            "manual",
            "manual-v1",
            "development-manual");
        _store.Setup(item => item.GetLatestSubmissionByUserAsync(
                userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(Submission(
                userId,
                KycStatus.APPROVED,
                DateTimeOffset.UtcNow.AddDays(1),
                profile)));
        var settings = TrustedSettings("manual-v1", "development-manual", "manual");
        settings.ApprovalPolicy.AllowManualInDevelopment = true;
        var gate = new KycGateService(
            _store.Object,
            _screening,
            provider.Object,
            Options.Create(settings),
            HostEnvironment(Environments.Development));

        var result = await gate.RequireCurrentApprovalAsync(userId);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
    }

    private void SetupLatest(Guid userId, KycStatus status, DateTimeOffset? expiresAt)
    {
        _store.Setup(item => item.GetLatestSubmissionByUserAsync(
                userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<KycSubmission>.Success(Submission(
                userId,
                status,
                expiresAt,
                CurrentProfile())));
    }

    private KycGateService Gate() => new(
        _store.Object,
        _screening,
        _provider.Object,
        Options.Create(TrustedSettings("production-v1", "substantial", "veriff")),
        HostEnvironment(Environments.Production));

    private static KycSubmission Submission(
        Guid userId,
        KycStatus status,
        DateTimeOffset? expiresAt,
        KycApprovalProfile profile) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        UserId = userId.ToString("N"),
        AmlLineageKey = "fixture-lineage",
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
