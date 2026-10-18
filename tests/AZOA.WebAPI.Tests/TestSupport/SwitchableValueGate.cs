using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Services.Fiat.Reversal;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>
/// The production value gate shape: the real <see cref="RiskHoldValueGate"/> over an approved identity gate, with the reversal
/// ledger (the only process edge) faked so a test can open or clear the account pause.
/// </summary>
internal sealed class SwitchableValueGate : IRealValueKycGate
{
    private readonly RiskHoldValueGate _gate;

    internal SwitchableValueGate()
    {
        var ledger = new Mock<IFiatReversalLedger>(MockBehavior.Strict);
        ledger.Setup(value => value.HasActiveFlagAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => AZOAResult<bool>.Success(Paused));
        _gate = new RiskHoldValueGate(new ApprovedRealValueKycGate(), ledger.Object, NullLogger<RiskHoldValueGate>.Instance);
    }

    /// <summary>True while an unresolved reversal is flagged against every account.</summary>
    internal bool Paused { get; set; }

    public Task<AZOAResult<bool>> RequireCurrentApprovalAsync(Guid userId, CancellationToken ct = default)
        => _gate.RequireCurrentApprovalAsync(userId, ct);

    public Task<AZOAResult<bool>> RequireCurrentApprovalAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
        => _gate.RequireCurrentApprovalAsync(userId, tenantId, ct);
}
