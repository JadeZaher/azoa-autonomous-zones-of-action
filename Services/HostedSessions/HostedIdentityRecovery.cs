using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Services.Kyc;

namespace AZOA.WebAPI.Services.HostedSessions;

/// <summary>Recovers persisted provider requests and dispatches authenticated callback triggers.</summary>
public sealed class HostedIdentityRecovery(IHostedProviderAttemptStore attempts, IHostedSessionStore sessions,
    IHostedSessionFlow flow, VeriffKycProviderService veriff, GenericHostedKycProviderService generic, IKycProviderRegistry registry)
{
    public PreparedHostedKycProvider? Resolve(string adapter) => adapter switch
    {
        "veriff" => veriff,
        "generic-hosted" => generic,
        _ => null,
    };

    public async Task<HostedIdentityCallbackDisposition> CallbackAsync(string adapter, string body, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var provider = Resolve(adapter);
        if (provider is null) return HostedIdentityCallbackDisposition.Ignored;
        var verified = provider.VerifyCallback(body, headers);
        if (verified.IsError || verified.Result is null || !HostedIdentityProtocol.SafeToken(verified.Result.EventId)) return HostedIdentityCallbackDisposition.Ignored;
        var attempt = await provider.FindCallbackAttemptAsync(verified.Result, ct);
        if (attempt is null) return HostedIdentityCallbackDisposition.Retry;
        var receipt = await attempts.AdmitCallbackAsync(new()
        {
            Id = HostedIdentityProtocol.Digest($"{adapter}:{attempt.Origin}:{verified.Result.EventId}"),
            AttemptId = attempt.Id, BodyDigest = HostedIdentityProtocol.Digest(body), ReceivedAt = DateTimeOffset.UtcNow,
        }, ct);
        if (receipt is null) return HostedIdentityCallbackDisposition.Retry;
        if (receipt.Processed) return HostedIdentityCallbackDisposition.Processed;
        var result = await RecoverAsync(attempt, verified.Result.ProviderSessionId, ct);
        if (!result.IsError) await attempts.CompleteCallbackAsync(receipt.Id, ct);
        return result.IsError ? HostedIdentityCallbackDisposition.Retry : HostedIdentityCallbackDisposition.Processed;
    }

    public async Task<AZOAResult<HostedSession>> RecoverAsync(HostedProviderAttempt attempt, string? candidate, CancellationToken ct,
        Guid? operatorId = null, string? reason = null)
    {
        await attempts.ScheduleAsync(attempt.Id, DateTimeOffset.UtcNow.AddMinutes(2), false, ct);
        var located = await sessions.GetByIdAsync(Guid.ParseExact(attempt.Id, "N"), ct);
        if (located.IsError || located.Result is null) return AZOAResult<HostedSession>.Failure("The hosted admission is not available.");
        var session = located.Result;
        if (session.FlowKind != HostedSessionFlowKinds.Kyc || session.ProviderKey != attempt.ProviderKey
            || session.SubjectUserId != attempt.SubjectId || session.TenantId != attempt.TenantId)
            return AZOAResult<HostedSession>.Failure("The hosted admission authority changed.");
        var provider = Resolve(attempt.AdapterKey);
        if (provider is null) return AZOAResult<HostedSession>.Failure("The saved provider adapter is not registered.");
        if (session.State is HostedSessionStates.Completed or HostedSessionStates.Failed or HostedSessionStates.Abandoned)
        {
            if (candidate is not null && session.ProviderSessionId is not null)
            {
                if (candidate != session.ProviderSessionId) return AZOAResult<HostedSession>.Failure("The callback identifies another session.");
                var verified = await provider.FetchAuthoritativeOutcomeAsync(candidate, ct);
                if (verified.IsError) return AZOAResult<HostedSession>.Failure(verified.Message);
            }
            await attempts.ScheduleAsync(attempt.Id, DateTimeOffset.UtcNow, true, ct);
            return AZOAResult<HostedSession>.Success(session);
        }
        if (session.ProviderSessionId is null)
        {
            var current = session.TenantId is null ? registry.ResolveNodeDefault()
                : await registry.ResolveTenantAsync(Guid.ParseExact(session.TenantId, "N"), ct);
            if (current.IsError || current.Result is null || current.Result.Provider.ProviderKey != attempt.ProviderKey
                || current.Result.Provider.Provider != provider.Provider
                || current.Result.Settings.ApprovalPolicy.PolicyVersion != attempt.PolicyRevision)
                return AZOAResult<HostedSession>.Failure("The original identity attempt no longer has current provider and policy authority.");
            var created = candidate is null ? await provider.RecoverCreationAsync(session, ct)
                : await provider.RecoverCandidateAsync(session, candidate, ct);
            if (created.IsError || created.Result is null) return AZOAResult<HostedSession>.Failure(created.Message);
            if (candidate is not null && candidate != created.Result.ProviderSessionId)
                return AZOAResult<HostedSession>.Failure("The callback session does not match the authoritative attempt lookup.");
            located = await sessions.GetByIdAsync(Guid.ParseExact(attempt.Id, "N"), ct);
            if (located.IsError || located.Result is null) return AZOAResult<HostedSession>.Failure("The admission changed during recovery.");
            session = located.Result;
            if (session.ProviderSessionId is null)
            {
                var attached = operatorId is { } actor && !string.IsNullOrWhiteSpace(reason)
                    ? await sessions.ReconcileAdmissionAsync(session, actor, reason, created.Result, DateTimeOffset.UtcNow, ct)
                    : await sessions.TryAttachProviderSessionAsync(Guid.ParseExact(attempt.Id, "N"), session.StateVersion,
                    created.Result.ProviderSessionId, created.Result.ProviderCorrelationId, created.Result.RedirectUrl,
                    created.Result.ExpiresAt, session.CreationToken!, ct);
                if (attached.IsError || attached.Result is null) return AZOAResult<HostedSession>.Failure("The admission changed before attachment.");
                session = attached.Result;
            }
        }
        if (candidate is not null && candidate != session.ProviderSessionId)
            return AZOAResult<HostedSession>.Failure("The callback does not identify the saved provider session.");
        var refreshed = await flow.RefreshAsync(KycProviderRegistry.BindHostedProfile(attempt.ProviderKey, provider), Guid.ParseExact(attempt.Id, "N"), ct);
        if (refreshed.IsError) return AZOAResult<HostedSession>.Failure(refreshed.Message);
        return await sessions.GetByIdAsync(Guid.ParseExact(attempt.Id, "N"), ct);
    }
}
