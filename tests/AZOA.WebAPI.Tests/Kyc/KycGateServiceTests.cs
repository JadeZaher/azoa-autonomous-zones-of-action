using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Tests.Kyc;

public class KycGateServiceTests
{
    private readonly Mock<IKycStore> _store = new();
    private readonly Mock<IKycProviderService> _provider = new();
    private readonly KycGateService _gate;

    public KycGateServiceTests()
    {
        ConfigureManualProvider(_provider);
        _gate = new KycGateService(
            _store.Object,
            _provider.Object,
            Options.Create(TrustedManualSettings()),
            Environment(Environments.Development));
    }

    private void SetupLatest(Guid avatarId, KycSubmission? submission)
        => _store.Setup(s => s.GetLatestSubmissionByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<KycSubmission> { Result = submission });

    private static KycSubmission Submission(KycStatus status) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Provider = KycProvider.MANUAL,
        ProviderKey = "manual",
        ProviderResult = KycApprovalTrust.CreateEnvelope(ManualProfile()),
        Status = status,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
    };

    [Fact]
    public async Task RequireVerified_ManualSimulationNeverUnlocksValueOperations()
    {
        var avatarId = Guid.NewGuid();
        SetupLatest(avatarId, Submission(KycStatus.APPROVED));

        var result = await _gate.RequireVerifiedAsync(avatarId);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
        result.Message.Should().StartWith(KycAuthorizationError.Forbidden);
    }

    [Fact]
    public async Task RequireVerified_ExpiredApproval_FailsClosed()
    {
        var avatarId = Guid.NewGuid();
        var submission = Submission(KycStatus.APPROVED);
        submission.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        SetupLatest(avatarId, submission);

        var result = await _gate.RequireVerifiedAsync(avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(KycAuthorizationError.Forbidden);
    }

    [Fact]
    public async Task RequireVerified_ApprovalWithoutExplicitExpiry_FailsClosed()
    {
        var avatarId = Guid.NewGuid();
        var submission = Submission(KycStatus.APPROVED);
        submission.ExpiresAt = null;
        SetupLatest(avatarId, submission);

        var result = await _gate.RequireVerifiedAsync(avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(KycAuthorizationError.Forbidden);
    }

    [Fact]
    public async Task RequireVerified_RetiredPolicyApproval_FailsClosed()
    {
        var avatarId = Guid.NewGuid();
        var submission = Submission(KycStatus.APPROVED);
        submission.ProviderResult = KycApprovalTrust.CreateEnvelope(new KycApprovalProfile(
            KycProvider.MANUAL,
            "manual",
            "retired-policy",
            "development-manual"));
        SetupLatest(avatarId, submission);

        var result = await _gate.RequireVerifiedAsync(avatarId);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task RequireVerified_ManualApprovalInProduction_FailsClosed()
    {
        var avatarId = Guid.NewGuid();
        SetupLatest(avatarId, Submission(KycStatus.APPROVED));
        var productionGate = new KycGateService(
            _store.Object,
            _provider.Object,
            Options.Create(TrustedManualSettings()),
            Environment(Environments.Production));

        var result = await productionGate.RequireVerifiedAsync(avatarId);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task RequireVerified_NoSubmission_ForbiddenWithPrefixAndGenericMessage()
    {
        var avatarId = Guid.NewGuid();
        SetupLatest(avatarId, null);

        var result = await _gate.RequireVerifiedAsync(avatarId);

        result.IsError.Should().BeTrue();
        result.Result.Should().BeFalse();
        result.Message.Should().StartWith(KycAuthorizationError.Forbidden);
        result.Message.Should().Contain(KycAuthorizationError.VerificationRequiredMessage);
    }

    [Theory]
    [InlineData(KycStatus.PENDING)]
    [InlineData(KycStatus.IN_REVIEW)]
    [InlineData(KycStatus.REJECTED)]
    [InlineData(KycStatus.EXPIRED)]
    public async Task RequireVerified_NonApprovedLatest_Forbidden(KycStatus status)
    {
        var avatarId = Guid.NewGuid();
        SetupLatest(avatarId, Submission(status));

        var result = await _gate.RequireVerifiedAsync(avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(KycAuthorizationError.Forbidden);
    }

    [Fact]
    public async Task RequireVerified_MessageIsExactlyTheGenericForbiddenMessage()
    {
        var avatarId = Guid.NewGuid();
        SetupLatest(avatarId, Submission(KycStatus.REJECTED));

        var result = await _gate.RequireVerifiedAsync(avatarId);

        // Frozen wire contract: Forbidden prefix + the brand-free generic message,
        // and nothing else. Guards against a vendor URL / product string sneaking in.
        result.Message.Should().Be(
            KycAuthorizationError.Forbidden + KycAuthorizationError.VerificationRequiredMessage);
    }

    [Fact]
    public async Task GetKycStatus_ReturnsLatestStatus()
    {
        var avatarId = Guid.NewGuid();
        SetupLatest(avatarId, Submission(KycStatus.IN_REVIEW));

        var result = await _gate.GetKycStatusAsync(avatarId);

        result.IsError.Should().BeFalse();
        result.Result.Should().Be(KycStatus.IN_REVIEW);
    }

    [Fact]
    public async Task GetKycStatus_NoSubmission_NotFound()
    {
        var avatarId = Guid.NewGuid();
        SetupLatest(avatarId, null);

        var result = await _gate.GetKycStatusAsync(avatarId);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(KycAuthorizationError.NotFound);
    }

    [Fact]
    public async Task GetKycStatus_RetiredProviderApprovalProjectsExpired()
    {
        var avatarId = Guid.NewGuid();
        var submission = Submission(KycStatus.APPROVED);
        submission.Provider = KycProvider.VERIFF;
        submission.ProviderKey = "veriff";
        SetupLatest(avatarId, submission);

        var result = await _gate.GetKycStatusAsync(avatarId);

        result.IsError.Should().BeFalse();
        result.Result.Should().Be(KycStatus.EXPIRED);
    }

    private static void ConfigureManualProvider(Mock<IKycProviderService> provider)
    {
        provider.SetupGet(item => item.Provider).Returns(KycProvider.MANUAL);
        provider.SetupGet(item => item.ProviderKey).Returns("manual");
        provider.Setup(item => item.GetCapabilities()).Returns(new KycProviderCapabilitiesModel
        {
            Provider = KycProvider.MANUAL,
            ProviderKey = "manual",
            Available = true,
        });
    }

    private static KycSettings TrustedManualSettings() => new()
    {
        Provider = "manual",
        ApprovalPolicy = new KycApprovalPolicySettings
        {
            PolicyVersion = "dev-manual-v1",
            AssuranceLevel = "development-manual",
            TrustedProviderKeys = ["manual"],
            AllowManualInDevelopment = true,
        },
    };

    private static KycApprovalProfile ManualProfile() => new(
        KycProvider.MANUAL,
        "manual",
        "dev-manual-v1",
        "development-manual");

    private static IHostEnvironment Environment(string name)
        => Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == name);
}
