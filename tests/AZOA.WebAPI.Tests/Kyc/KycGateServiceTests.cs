using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Tests.Kyc;

public class KycGateServiceTests
{
    private readonly Mock<IKycStore> _store = new();
    private readonly KycGateService _gate;

    public KycGateServiceTests()
    {
        _gate = new KycGateService(_store.Object);
    }

    private void SetupLatest(Guid avatarId, KycSubmission? submission)
        => _store.Setup(s => s.GetLatestSubmissionByAvatarAsync(avatarId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<KycSubmission> { Result = submission });

    private static KycSubmission Submission(KycStatus status) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Status = status
    };

    [Fact]
    public async Task RequireVerified_ApprovedSubmission_Succeeds()
    {
        var avatarId = Guid.NewGuid();
        SetupLatest(avatarId, Submission(KycStatus.APPROVED));

        var result = await _gate.RequireVerifiedAsync(avatarId);

        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
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
}
