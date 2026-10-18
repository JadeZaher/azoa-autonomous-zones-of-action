using System.Text.Json;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Kyb;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Kyb;

/// <inheritdoc/>
public sealed class KybHostedSessionOutcomeHandler(IKybVerificationStore store, IdentomatKybProvider provider,
    IOptions<KybSettings> options, TimeProvider clock, IUserStore users, IApiKeyStore apiKeys) : IHostedSessionOutcomeHandler
{
    /// <inheritdoc/>
    public async Task<AZOAResult<string?>> ResumeAsync(HostedSession session, HostedSessionOutcome outcome, CancellationToken ct = default)
    {
        if (session.FlowKind != HostedSessionFlowKinds.Kyb || session.ProviderKey != provider.ProviderKey
            || !Guid.TryParse(session.TenantId, out var tenantId) || tenantId == Guid.Empty
            || session.SubjectUserId != session.TenantId || !outcome.IsTerminal
            || outcome.Outcome is not ("APPROVED" or "REJECTED")
            || outcome.IsApproved != (outcome.Outcome == "APPROVED")
            || (outcome.IsApproved && outcome.Business is not { Beneficiaries.Count: > 0 }))
            return AZOAResult<string?>.Failure("Business verification outcome cannot be applied.");
        var current = await store.GetAsync(tenantId, ct);
        var now = clock.GetUtcNow();
        var authority = await TenantAuthority.CheckAsync(tenantId, users, apiKeys, now, ct);
        if (authority.IsError || !authority.Result)
            return AZOAResult<string?>.Failure("Tenant business verification is not authorized.");
        if (current is null || current.AdmissionKey != session.AdmissionKey
            || current.AuthorityRevision != provider.AuthorityRevision || !provider.GetHostedCapabilities().Available
            || current.ExpiresAt <= now)
            return AZOAResult<string?>.Failure("Business verification authority changed or expired.");
        if (current.Status == outcome.Outcome)
            return AZOAResult<string?>.Success(current.AttemptId);
        if (current.Status != "PENDING")
            return AZOAResult<string?>.Failure("Business verification already has a different outcome.");
        var expiresAt = outcome.IsApproved
            ? now.AddDays(Math.Clamp(options.Value.ApprovalLifetimeDays, 1, 365)) : current.ExpiresAt;
        var evidence = outcome.Business is null ? null : JsonSerializer.Serialize(outcome.Business);
        var saved = await store.TryDecideAsync(current, session, outcome.Outcome, evidence, now, expiresAt, ct);
        return saved is null ? AZOAResult<string?>.Failure("Business verification changed concurrently.")
            : AZOAResult<string?>.Success(saved.AttemptId);
    }

    /// <summary>Live only while this session's attempt is the tenant's current, unexpired PENDING verification.</summary>
    public async Task<HostedOutcomeRelevance> ProbeAsync(HostedSession session, DateTimeOffset now, CancellationToken ct = default)
    {
        if (!Guid.TryParse(session.TenantId, out var tenantId) || tenantId == Guid.Empty)
            return HostedOutcomeRelevance.Unknown;
        var current = await store.GetAsync(tenantId, ct);
        return current is { Status: "PENDING" } && current.AdmissionKey == session.AdmissionKey
            && current.ExpiresAt > now && current.AuthorityRevision == provider.AuthorityRevision
                ? HostedOutcomeRelevance.Live
                : HostedOutcomeRelevance.Dead;
    }
}
