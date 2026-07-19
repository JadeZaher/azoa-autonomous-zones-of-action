using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Kyc;
using FluentAssertions;
using Moq;

namespace AZOA.WebAPI.Tests.Kyc;

public sealed class ValueAccessServiceTests
{
    [Fact]
    public async Task Decision_UsesProviderBoundKycGateForTenantParticipant()
    {
        var participant = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        var gate = new Mock<IKycGateService>();
        gate.Setup(item => item.RequireVerifiedAsync(participant, tenant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(AZOAResult<bool>.Success(true));
        var service = new ValueAccessService(gate.Object);

        var decision = await service.GetDecisionAsync(participant, tenant);

        decision.State.Should().Be(ValueAccessState.Ready);
        decision.AllowsValueActions.Should().BeTrue();
        gate.Verify(item => item.RequireVerifiedAsync(participant, tenant, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Decision_FailsClosedWhenKycGateRejectsParticipant()
    {
        var gate = new Mock<IKycGateService>();
        gate.Setup(item => item.RequireVerifiedAsync(It.IsAny<Guid>()))
            .ReturnsAsync(AZOAResult<bool>.Failure(KycAuthorizationError.Forbidden));
        var service = new ValueAccessService(gate.Object);

        var decision = await service.GetDecisionAsync(Guid.NewGuid());

        decision.State.Should().Be(ValueAccessState.VerificationRequired);
        decision.AllowsValueActions.Should().BeFalse();
    }
}
