using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyb;
using AZOA.WebAPI.Services.Kyc;

namespace AZOA.WebAPI.Services.HostedSessions;

/// <inheritdoc/>
public sealed class HostedSessionOutcomeRouter(KycHostedSessionOutcomeHandler kyc,
    KybHostedSessionOutcomeHandler kyb) : IHostedSessionOutcomeHandler, IHostedSessionProgressHandler
{
    /// <inheritdoc/>
    public Task<AZOAResult<string?>> ResumeAsync(HostedSession session, HostedSessionOutcome outcome, CancellationToken ct = default)
        => session.FlowKind switch
        {
            HostedSessionFlowKinds.Kyc => kyc.ResumeAsync(session, outcome, ct),
            HostedSessionFlowKinds.Kyb => kyb.ResumeAsync(session, outcome, ct),
            _ => Task.FromResult(AZOAResult<string?>.Failure("Unknown hosted verification flow.")),
        };

    /// <inheritdoc/>
    public Task ObserveProgressAsync(HostedSession session, HostedSessionOutcome outcome, CancellationToken ct = default)
        => session.FlowKind == HostedSessionFlowKinds.Kyc ? kyc.ObserveProgressAsync(session, outcome, ct) : Task.CompletedTask;
}
