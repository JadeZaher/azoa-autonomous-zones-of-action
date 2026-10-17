using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.HostedSessions;

namespace AZOA.WebAPI.Providers.Kyc;

/// <summary>Shared durable request admission for reviewed hosted identity protocols.</summary>
public abstract class PreparedHostedKycProvider(IHostedProviderAttemptStore attempts, IHostedSessionStore sessions,
    HostedIdentityHttp transport) : IKycProviderService, IPreparedHostedSessionProvider
{
    protected IHostedProviderAttemptStore Attempts { get; } = attempts;
    protected IHostedSessionStore Sessions { get; } = sessions;
    protected HostedIdentityHttp Transport { get; } = transport;
    public abstract KycProvider Provider { get; }
    public abstract string ProviderKey { get; }
    protected abstract string AdapterKey { get; }
    protected abstract string Origin { get; }
    protected abstract string ProtocolVersion { get; }
    protected abstract bool PermanentIdempotency { get; }
    public string RecoveryAdapterKey => AdapterKey;
    public string FlowKind => HostedSessionFlowKinds.Kyc;
    public abstract HostedSessionCapabilities GetHostedCapabilities();
    protected abstract string RequestBody(HostedSessionRequest request);
    protected abstract Task<AZOAResult<HostedSessionCreation>> SendCreationAsync(HostedProviderAttempt request, bool recovery, CancellationToken ct);
    public abstract Task<AZOAResult<HostedSessionOutcome>> FetchAuthoritativeOutcomeAsync(string providerSessionId, CancellationToken ct = default);
    public abstract AZOAResult<HostedCallbackTrigger> VerifyCallback(string rawBody, IReadOnlyDictionary<string, string> headers);

    /// <inheritdoc/>
    public KycProviderCapabilitiesModel GetCapabilities()
    {
        var capabilities = GetHostedCapabilities();
        return new() { Provider = Provider, ProviderKey = ProviderKey, HostedVerification = true,
            Available = capabilities.Available, AcceptsDocumentReferences = false, UnavailableReason = capabilities.UnavailableReason };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> PrepareCreationAsync(HostedSessionRequest request, CancellationToken ct)
    {
        if (!GetHostedCapabilities().Available) return AZOAResult<bool>.Failure("Hosted identity configuration is incomplete.");
        if (request.SessionId is null || request.SubjectUserId == Guid.Empty || request.FlowKind != FlowKind
            || !HostedIdentityProtocol.SafeToken(request.ProviderKey, 64) || !HostedIdentityProtocol.SafeToken(request.PolicyRevision, 128))
            return AZOAResult<bool>.Failure("A durable identity attempt and policy revision are required.");
        var body = RequestBody(request);
        var candidate = new HostedProviderAttempt
        {
            Id = request.SessionId.Value.ToString("N"), AdapterKey = AdapterKey, ProviderKey = request.ProviderKey,
            SubjectId = request.SubjectUserId.ToString("N"), TenantId = request.TenantId?.ToString("N"),
            Origin = Origin, ProtocolVersion = ProtocolVersion, PolicyRevision = request.PolicyRevision,
            RequestJson = body, RequestDigest = HostedIdentityProtocol.Digest(body), CreatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(2),
        };
        var stored = await Attempts.EnsureAsync(candidate, ct);
        return stored.AdapterKey == candidate.AdapterKey && stored.ProviderKey == candidate.ProviderKey
            && stored.SubjectId == candidate.SubjectId && stored.TenantId == candidate.TenantId
            && stored.PolicyRevision == candidate.PolicyRevision && stored.ProtocolVersion == candidate.ProtocolVersion
            ? AZOAResult<bool>.Success(true) : AZOAResult<bool>.Failure("The immutable hosted identity request belongs to another authority.");
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<HostedSessionCreation>> CreateHostedSessionAsync(HostedSessionRequest request, CancellationToken ct = default)
    {
        if (request.SessionId is null) return Unsent("A prepared identity request is required.");
        var located = await Sessions.GetByIdAsync(request.SessionId.Value, ct);
        return located.Result is null ? Unsent("The durable hosted admission was not found.")
            : await SendPreparedAsync(located.Result, false, ct);
    }

    /// <inheritdoc/>
    public Task<AZOAResult<HostedSessionCreation>> RecoverCreationAsync(HostedSession session, CancellationToken ct)
        => SendPreparedAsync(session, true, ct);

    private async Task<AZOAResult<HostedSessionCreation>> SendPreparedAsync(HostedSession session, bool recovery, CancellationToken ct)
    {
        var stored = await Attempts.GetAsync(session.Id, ct);
        if (stored is null || stored.AdapterKey != AdapterKey || stored.ProviderKey != session.ProviderKey
            || stored.SubjectId != session.SubjectUserId || stored.TenantId != session.TenantId
            || stored.Origin != Origin || stored.ProtocolVersion != ProtocolVersion
            || stored.RequestDigest != HostedIdentityProtocol.Digest(stored.RequestJson))
            return CreationRefused(stored, "The prepared identity request does not match the configured provider.");
        if (stored.RemoteSessionId is not null && stored.RedirectUrl is not null)
            return AZOAResult<HostedSessionCreation>.Success(new() { ProviderSessionId = stored.RemoteSessionId,
                RedirectUrl = stored.RedirectUrl, ExpiresAt = stored.ExpiresAt });
        if (stored.SendStartedAt is not null && !PermanentIdempotency)
            return AZOAResult<HostedSessionCreation>.FailureWithCode("NeedsOperator: ambiguous provider creation requires verified attachment.", AzoaErrorCodes.Conflict);
        if (!GetHostedCapabilities().Available) return CreationRefused(stored, "Hosted identity configuration is incomplete.");
        var lease = await Attempts.ClaimAsync(session, PermanentIdempotency && recovery, DateTimeOffset.UtcNow, ct);
        if (lease is null) return AZOAResult<HostedSessionCreation>.FailureWithCode("Identity creation is owned by another worker or its authority changed.", AzoaErrorCodes.Conflict);
        var created = await SendCreationAsync(lease, recovery, ct);
        if (created.IsError || created.Result is null) return created;
        var saved = await Attempts.SaveCreationAsync(lease, created.Result, ct);
        return saved is null ? AZOAResult<HostedSessionCreation>.FailureWithCode("The provider response requires reconciliation with its original request.", AzoaErrorCodes.Conflict) : created;
    }

    /// <inheritdoc/>
    public AZOAResult<HostedCallbackTrigger> VerifyCallback(string rawBody)
        => AZOAResult<HostedCallbackTrigger>.Failure("Authenticated callback headers are required.");

    /// <inheritdoc/>
    public Task<AZOAResult<KycSessionStartModel>> BeginSessionAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<KycSessionStartModel>.Failure("Begin through the KYC submission endpoint so the durable attempt and trust policy are reserved first."));

    /// <inheritdoc/>
    public Task<AZOAResult<string>> CreateSessionAsync(Guid userId, IReadOnlyList<KycDocumentModel> documents, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<string>.Failure("This provider accepts documents in its hosted verification flow."));

    /// <inheritdoc/>
    public Task<AZOAResult<bool>> ValidateDocumentsAsync(IReadOnlyList<SubmitKycDocumentModel> documents, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<bool>.Failure("This provider accepts documents in its hosted verification flow."));

    /// <inheritdoc/>
    public Task<AZOAResult<KycStatus>> HandleWebhookAsync(string payload, CancellationToken ct = default)
        => Task.FromResult(AZOAResult<KycStatus>.Failure("Use the authenticated callback endpoint; a payload alone cannot authorize a decision."));

    /// <inheritdoc/>
    public async Task<AZOAResult<KycStatus>> GetSessionStatusAsync(string providerSessionId, CancellationToken ct = default)
    {
        var result = await FetchAuthoritativeOutcomeAsync(providerSessionId, ct);
        return result.IsError || result.Result is null ? AZOAResult<KycStatus>.Failure(result.Message)
            : AZOAResult<KycStatus>.Success(!result.Result.IsTerminal ? KycStatus.IN_REVIEW
                : result.Result.IsApproved ? KycStatus.APPROVED : KycStatus.REJECTED);
    }

    protected static AZOAResult<T> Invalid<T>() => AZOAResult<T>.Failure("The authoritative identity response did not satisfy the configured protocol.");
    public async Task<HostedProviderAttempt?> FindCallbackAttemptAsync(HostedCallbackTrigger trigger, CancellationToken ct)
    {
        var row = trigger.AttemptId is { } id ? await Attempts.GetAsync(id.ToString("N"), ct)
            : await Attempts.GetByRemoteAsync(AdapterKey, Origin, trigger.ProviderSessionId, ct);
        return row is not null && row.AdapterKey == AdapterKey && row.Origin == Origin
            && (row.RemoteSessionId is null || row.RemoteSessionId == trigger.ProviderSessionId) ? row : null;
    }

    public virtual Task<AZOAResult<HostedSessionCreation>> RecoverCandidateAsync(HostedSession session, string remoteId, CancellationToken ct)
        => RecoverCreationAsync(session, ct);

    private static AZOAResult<HostedSessionCreation> CreationRefused(HostedProviderAttempt? attempt, string message)
        => attempt?.SendStartedAt is null ? Unsent(message)
            : AZOAResult<HostedSessionCreation>.FailureWithCode(message, AzoaErrorCodes.Conflict);
    private static AZOAResult<HostedSessionCreation> Unsent(string message)
        => AZOAResult<HostedSessionCreation>.FailureWithCode(message, HostedSessionFlow.RequestNotSentCode);
}
