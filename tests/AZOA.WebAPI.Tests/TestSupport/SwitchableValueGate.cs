using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Fiat.Reversal;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>
/// The production value gate shape: the real <see cref="RiskHoldValueGate"/> over a switchable identity gate, with the reversal
/// ledger (the only process edge) faked so a test can open or clear the account pause, or lapse the KYC approval.
/// </summary>
internal sealed class SwitchableValueGate : IRealValueKycGate
{
    private readonly RiskHoldValueGate _gate;

    internal SwitchableValueGate()
    {
        var ledger = new Mock<IFiatReversalLedger>(MockBehavior.Strict);
        ledger.Setup(value => value.HasActiveFlagAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => AZOAResult<bool>.Success(Paused));
        Ledger = ledger.Object;
        _gate = new RiskHoldValueGate(new IdentityGate(this), Ledger, NullLogger<RiskHoldValueGate>.Instance);
    }

    /// <summary>True while an unresolved reversal is flagged against every account.</summary>
    internal bool Paused { get; set; }

    /// <summary>True while every account's KYC approval has lapsed (the identity half of the gate refuses).</summary>
    internal bool KycLapsed { get; set; }

    /// <summary>The same reversal ledger the gate reads: the pause-only predicate workflow paths consult.</summary>
    internal IFiatReversalLedger Ledger { get; }

    /// <summary>Refusal text while <see cref="KycLapsed"/> is set.</summary>
    internal const string KycLapsedMessage = "KYC approval has lapsed; renew verification to move value.";

    public Task<AZOAResult<bool>> RequireCurrentApprovalAsync(Guid userId, CancellationToken ct = default)
        => _gate.RequireCurrentApprovalAsync(userId, ct);

    public Task<AZOAResult<bool>> RequireCurrentApprovalAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
        => _gate.RequireCurrentApprovalAsync(userId, tenantId, ct);

    private sealed class IdentityGate(SwitchableValueGate owner) : IRealValueKycGate
    {
        public Task<AZOAResult<bool>> RequireCurrentApprovalAsync(Guid userId, CancellationToken ct = default) => Verdict();

        public Task<AZOAResult<bool>> RequireCurrentApprovalAsync(Guid userId, Guid tenantId, CancellationToken ct = default) => Verdict();

        private Task<AZOAResult<bool>> Verdict() => Task.FromResult(owner.KycLapsed
            ? AZOAResult<bool>.Failure(KycLapsedMessage)
            : AZOAResult<bool>.Success(true));
    }
}
