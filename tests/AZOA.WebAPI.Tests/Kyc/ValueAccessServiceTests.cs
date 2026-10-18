using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Fiat.Reversal;
using AZOA.WebAPI.Services.Kyc;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.Kyc;

/// <summary>
/// Every value path (allocation, exchange, hold, fungible, NFT, deployment, governance) asks
/// <see cref="ValueAccessService"/>; it must see the reversal risk hold, not just identity (RV-L2 L2-8 / RV-X X-2).
/// </summary>
public sealed class ValueAccessServiceTests
{
    public static TheoryData<bool, bool?, Guid?, bool> Cases => new()
    {
        // identity approved, account flag (null = ledger unreadable), tenant, value access allowed
        { true, false, null, true },
        { true, false, Guid.NewGuid(), true },
        { true, true, null, false },
        { true, true, Guid.NewGuid(), false },
        { true, null, Guid.NewGuid(), false },
        { false, false, Guid.NewGuid(), false },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Value_access_requires_identity_approval_and_no_reversal_risk_hold(
        bool identityApproved, bool? flagged, Guid? tenant, bool allowed)
    {
        var participant = Guid.NewGuid();
        var service = new ValueAccessService(RiskHeldGate(participant, identityApproved, flagged));

        var required = await service.RequireValueAccessAsync(participant, tenant);
        var decision = await service.GetDecisionAsync(participant, tenant);

        required.IsError.Should().Be(!allowed);
        decision.AllowsValueActions.Should().Be(allowed);
        if (flagged == true && identityApproved)
            required.Message.Should().Be(RiskHoldValueGate.RiskHoldMessage);
    }

    [Fact]
    public async Task An_empty_participant_is_refused_without_consulting_the_gate()
    {
        var service = new ValueAccessService(RiskHeldGate(Guid.Empty, identityApproved: true, flagged: false));

        var required = await service.RequireValueAccessAsync(Guid.Empty);

        required.IsError.Should().BeTrue();
        required.Message.Should().StartWith(KycAuthorizationError.Forbidden);
    }

    /// <summary>The production decorator over an identity verdict stub and a ledger stub.</summary>
    private static IRealValueKycGate RiskHeldGate(Guid participant, bool identityApproved, bool? flagged)
    {
        var identity = identityApproved
            ? AZOAResult<bool>.Success(true)
            : AZOAResult<bool>.Failure(KycAuthorizationError.Forbidden + KycAuthorizationError.VerificationRequiredMessage);
        var inner = new Mock<IRealValueKycGate>();
        inner.Setup(gate => gate.RequireCurrentApprovalAsync(participant, It.IsAny<CancellationToken>())).ReturnsAsync(identity);
        inner.Setup(gate => gate.RequireCurrentApprovalAsync(participant, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(identity);
        var ledger = new Mock<IFiatReversalLedger>();
        ledger.Setup(item => item.HasActiveFlagAsync(participant, It.IsAny<CancellationToken>()))
            .ReturnsAsync(flagged is { } value ? AZOAResult<bool>.Success(value) : AZOAResult<bool>.Failure("ledger unreadable"));
        return new RiskHoldValueGate(inner.Object, ledger.Object, NullLogger<RiskHoldValueGate>.Instance);
    }
}
