// SPDX-License-Identifier: UNLICENSED

using SurrealForge.Client;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using AZOA.WebAPI.Services.HostedSessions;
using AZOA.WebAPI.Models.HostedSessions;
using AZOA.WebAPI.Models.Signing;

namespace AZOA.WebAPI.Managers;

/// <summary>
/// Provider-agnostic KYC manager. User-scoped, returns
/// <see cref="AZOAResult{T}"/>, and flags authorisation failures with the
/// <see cref="KycAuthorizationError"/> message-prefix discriminator so the
/// controller can translate to 403/404. The submission ledger is the sole
/// verification authority; no user projection is updated.
/// </summary>
public sealed class KycManager : IKycManager
{
    private readonly IKycStore _store;
    private readonly IHostedSessionFlow? _hostedFlow;
    private readonly IKycProviderService _provider;
    private readonly IKycProviderRegistry? _registry;
    private readonly KycSettings _settings;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration? _configuration;
    private readonly IHostedSessionStore? _hostedSessions;
    private readonly IHostedSessionRecoveryStore? _recovery;
    private readonly TimeProvider _clock;

    public KycManager(
        IKycStore store,
        IKycProviderService provider,
        IOptions<KycSettings> settings,
        IHostEnvironment environment,
        IConfiguration? configuration = null, IHostedSessionFlow? hostedFlow = null)
    {
        _clock = TimeProvider.System;
        _store = store;
        _provider = provider;
        _registry = null;
        _settings = settings.Value;
        _environment = environment;
        _configuration = configuration;
        _hostedFlow = hostedFlow;
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public KycManager(
        IKycStore store,
        IKycProviderService provider,
        IKycProviderRegistry registry,
        IOptions<KycSettings> settings,
        IHostEnvironment environment,
        IConfiguration? configuration = null, IHostedSessionFlow? hostedFlow = null,
        IHostedSessionStore? hostedSessions = null, IHostedSessionRecoveryStore? recovery = null,
        TimeProvider? clock = null)
    {
        _hostedSessions = hostedSessions;
        _recovery = recovery;
        _clock = clock ?? TimeProvider.System;
        _store = store;
        _provider = provider;
        _registry = registry;
        _settings = settings.Value;
        _environment = environment;
        _configuration = configuration;
        _hostedFlow = hostedFlow;
    }

    public KycProviderCapabilitiesModel GetCapabilities()
    {
        if (_registry is not null)
        {
            var resolved = _registry.ResolveNodeDefault();
            if (!resolved.IsError && resolved.Result is not null)
                return RuntimeCapabilities(resolved.Result.Provider.GetCapabilities());
        }

        if (KycApprovalTrust.TryResolveCurrentProfile(
                _provider,
                _settings,
                _environment,
                out var capabilities,
                out _,
                out var policyFailure))
        {
            return RuntimeCapabilities(capabilities);
        }

        return RuntimeCapabilities(new KycProviderCapabilitiesModel
        {
            Provider = capabilities.Provider,
            ProviderKey = capabilities.ProviderKey,
            Available = false,
            HostedVerification = capabilities.HostedVerification,
            AcceptsDocumentReferences = capabilities.AcceptsDocumentReferences,
            UnavailableReason = policyFailure,
        });
    }

    public async Task<AZOAResult<KycProviderCapabilitiesModel>> GetCapabilitiesAsync(
        Guid tenantId,
        CancellationToken ct = default)
    {
        var resolved = await ResolveAuthorityAsync(tenantId, ct);
        return resolved.IsError || resolved.Result is null
            ? AZOAResult<KycProviderCapabilitiesModel>.Failure(resolved.Message)
            : AZOAResult<KycProviderCapabilitiesModel>.Success(
                RuntimeCapabilities(resolved.Result.Provider.GetCapabilities()));
    }

    public Task<AZOAResult<KycSessionStartModel>> BeginAsync(
        Guid userId,
        CancellationToken ct = default)
        => BeginCoreAsync(userId, null, ct);

    /// <inheritdoc/>
    public async Task<AZOAResult<KycSubmissionModel>> RefreshAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty)
            return Error<KycSubmissionModel>("An authenticated user is required.");
        var active = await GetActiveAsync(userId, null, ct);
        if (active.IsError)
            return Error<KycSubmissionModel>(active.Message, active.Exception, active.Code);
        if (active.Result is { HostedVerification: true } attempt && _hostedFlow is not null
            && string.Equals(attempt.Id, attempt.ProviderSessionId, StringComparison.Ordinal)
            && Guid.TryParseExact(attempt.ProviderSessionId, "N", out var sessionId))
        {
            var authority = await ResolveAuthorityAsync(null, ct);
            if (!authority.IsError && authority.Result?.Provider is IHostedSessionProvider hosted)
            {
                // An unattached or mismatched session refreshes to an opaque no-op; the status read below is authoritative.
                var refreshed = await _hostedFlow.RefreshAsync(hosted, sessionId, ct);
                if (refreshed.IsError && refreshed.Code is HostedSessionFlow.RequestNotSentCode or HostedSessionFlow.ProviderRefusedCode)
                    return Error<KycSubmissionModel>(refreshed.Message, code: AzoaErrorCodes.DependencyUnavailable);
            }
        }

        return await GetStatusCoreAsync(userId, null, ct);
    }

    /// <inheritdoc/>
    public Task<AZOAResult<KycSessionStartModel>> BeginCustodialAsync(TenantCustodialWitness witness, CancellationToken ct = default)
        => BeginCoreAsync(witness.UserId, witness.Authority.Credential.ActorUserId, ct, witness);

    /// <inheritdoc/>
    public Task<AZOAResult<KycSubmissionModel>> SubmitCustodialAsync(SubmitKycModel model, TenantCustodialWitness witness, CancellationToken ct = default)
        => SubmitCoreAsync(model, witness.UserId, witness.Authority.Credential.ActorUserId, ct, witness);

    private async Task<AZOAResult<KycSessionStartModel>> BeginCoreAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken ct, TenantCustodialWitness? custody = null)
    {
        if (userId == Guid.Empty)
            return AZOAResult<KycSessionStartModel>.Failure("An authenticated user is required.");

        var authority = await ResolveAuthorityAsync(tenantId, ct);
        if (authority.IsError || authority.Result is null)
            return AZOAResult<KycSessionStartModel>.Failure(
                $"KYC_POLICY_UNAVAILABLE: {authority.Message}");
        var resolution = authority.Result;
        if (!KycApprovalTrust.TryResolveCurrentProfile(
                resolution.Provider,
                resolution.Settings,
                _environment,
                out var capabilities,
                out var profile,
                out var policyFailure))
        {
            return AZOAResult<KycSessionStartModel>.Failure(
                $"KYC_POLICY_UNAVAILABLE: {policyFailure}");
        }

        var now = DateTimeOffset.UtcNow;
        var active = await GetActiveAsync(userId, tenantId, ct);
        if (active.IsError)
            return Error<KycSessionStartModel>(active.Message, active.Exception, active.Code);
        if (active.Result is not null
            && !IsExpired(active.Result, now)
            && MatchesAuthority(active.Result, resolution, profile))
        {
            return capabilities.HostedVerification
                ? await StartHostedAttemptAsync(active.Result, resolution.Provider, userId, tenantId, ct, custody)
                : Ok(ToSessionStart(active.Result));
        }
        if (active.Result is not null)
        {
            var expired = await RetireAttemptAsync(active.Result, now, custody, ct);
            if (expired.IsError || expired.Result is null)
                return Error<KycSessionStartModel>("The verification changed; retry.", expired.Exception, expired.Code ?? AzoaErrorCodes.Conflict);
        }

        var latest = await GetLatestAsync(userId, tenantId, ct);
        if (latest.IsError)
            return Error<KycSessionStartModel>(latest.Message, latest.Exception, latest.Code);
        if (latest.Result is not null
            && MatchesAuthority(latest.Result, resolution, profile)
            && KycApprovalTrust.IsCurrentApproval(latest.Result, profile, now, out _)
            && (!capabilities.HostedVerification || !string.IsNullOrWhiteSpace(latest.Result.AmlLineageKey)))
        {
            return Error<KycSessionStartModel>(
                "KYC_ALREADY_APPROVED: This identity already has an active approval.");
        }

        if (capabilities.HostedVerification)
        {
            if (resolution.Provider is not IHostedSessionProvider || _hostedFlow is null)
                return Error<KycSessionStartModel>("The durable hosted verification service is unavailable.");
            var hostedAttemptId = Guid.NewGuid().ToString("N");
            var hostedAttempt = new KycSubmission
            {
                Id = hostedAttemptId, UserId = userId.ToString("N"), TenantId = tenantId?.ToString("N"),
                ProviderSelectionVersion = resolution.SelectionVersion, ProviderTrustRevision = resolution.TrustRevision,
                Provider = profile.Provider, ProviderKey = profile.ProviderKey, Status = KycStatus.PENDING,
                ProviderSessionId = hostedAttemptId, HostedVerification = true, AcceptsDocumentReferences = false,
                ProviderResult = KycApprovalTrust.CreateEnvelope(profile), SubmittedAt = now,
                ExpiresAt = now.AddMinutes(Math.Clamp(resolution.Settings.SessionExpiryMinutes, 1, 1440)),
                CreatedDate = now, ModifiedDate = now,
            };
            var claimed = await CreateAttemptAsync(hostedAttempt, Array.Empty<KycDocument>(), custody, ct);
            if (claimed.IsError || claimed.Result is null)
            {
                var raced = await GetActiveAsync(userId, tenantId, ct);
                if (raced.IsError || raced.Result is null || IsExpired(raced.Result, DateTimeOffset.UtcNow)
                    || !MatchesAuthority(raced.Result, resolution, profile))
                    return Error<KycSessionStartModel>(claimed.Message, claimed.Exception, claimed.Code);
                hostedAttempt = raced.Result;
            }
            else hostedAttempt = claimed.Result;
            return await StartHostedAttemptAsync(hostedAttempt, resolution.Provider, userId, tenantId, ct, custody);
        }
        var started = await resolution.Provider.BeginSessionAsync(userId, ct);
        if (started.IsError || started.Result is null)
            return Error<KycSessionStartModel>(started.Message, started.Exception, started.Code);
        if (started.Result.Provider != profile.Provider
            || !string.Equals(started.Result.ProviderKey?.Trim(), profile.ProviderKey, StringComparison.OrdinalIgnoreCase))
        {
            return Error<KycSessionStartModel>(
                "KYC provider returned a session for a different verification authority.");
        }

        var sessionExpiresAt = started.Result.ExpiresAt.HasValue
            ? new DateTimeOffset(started.Result.ExpiresAt.Value.ToUniversalTime())
            : now.AddMinutes(Math.Clamp(resolution.Settings.SessionExpiryMinutes, 1, 1440));
        if (sessionExpiresAt <= now)
            return Error<KycSessionStartModel>("KYC provider returned an expired verification session.");

        var attemptId = Guid.NewGuid();
        var attempt = new KycSubmission
        {
            Id = SurrealId.ToSurrealId(attemptId),
            UserId = SurrealId.ToSurrealId(userId),
            TenantId = tenantId.HasValue ? SurrealId.ToSurrealId(tenantId.Value) : null,
            ProviderSelectionVersion = resolution.SelectionVersion,
            ProviderTrustRevision = resolution.TrustRevision,
            Provider = profile.Provider,
            ProviderKey = profile.ProviderKey,
            Status = KycStatus.PENDING,
            ProviderSessionId = started.Result.ProviderSessionId,
            HostedVerification = started.Result.HostedVerification,
            AcceptsDocumentReferences = started.Result.AcceptsDocumentReferences,
            VerificationUrl = started.Result.VerificationUrl,
            SessionInstructions = started.Result.Instructions,
            ProviderResult = KycApprovalTrust.CreateEnvelope(profile),
            SubmittedAt = now,
            ExpiresAt = sessionExpiresAt,
            CreatedDate = now,
            ModifiedDate = now
        };
        var created = await CreateAttemptAsync(attempt, Array.Empty<KycDocument>(), custody, ct);
        if (created.IsError || created.Result is null)
        {
            var concurrent = await GetActiveAsync(userId, tenantId, ct);
            if (!concurrent.IsError
                && concurrent.Result is not null
                && !IsExpired(concurrent.Result, DateTimeOffset.UtcNow)
                && MatchesAuthority(concurrent.Result, resolution, profile))
            {
                return Ok(ToSessionStart(concurrent.Result));
            }

            return Error<KycSessionStartModel>(created.Message, created.Exception, created.Code);
        }

        return Ok(ToSessionStart(created.Result));
    }

    private Task<AZOAResult<KycSubmission>> CreateAttemptAsync(KycSubmission submission,
        IReadOnlyList<KycDocument> documents, TenantCustodialWitness? custody, CancellationToken ct)
        => custody is null ? _store.CreateSubmissionAsync(submission, documents, ct)
            : _store is IKycCustodialAdmissionStore store ? store.CreateCustodialSubmissionAsync(submission, documents, custody, ct)
            : Task.FromResult(Error<KycSubmission>("Custodial KYC admission is unavailable."));

    private Task<AZOAResult<KycSubmission>> RetireAttemptAsync(KycSubmission expected,
        DateTimeOffset now, TenantCustodialWitness? custody, CancellationToken ct)
        => custody is null ? _store.TryRetireAttemptAsync(expected, now, ct)
            : _store is IKycCustodialAdmissionStore store ? store.RetireCustodialAttemptAsync(expected, now, custody, ct)
            : Task.FromResult(Error<KycSubmission>("Custodial KYC admission is unavailable."));

    private async Task<AZOAResult<KycSessionStartModel>> StartHostedAttemptAsync(
        KycSubmission attempt, IKycProviderService provider, Guid userId, Guid? tenantId, CancellationToken ct, TenantCustodialWitness? custody = null)
    {
        if (provider is not IHostedSessionProvider hosted || _hostedFlow is null)
            return Error<KycSessionStartModel>("The durable hosted verification service is unavailable.");
        if (!Guid.TryParseExact(attempt.ProviderSessionId, "N", out var sessionId)
            || !string.Equals(attempt.Id, attempt.ProviderSessionId, StringComparison.Ordinal))
            return string.IsNullOrWhiteSpace(attempt.VerificationUrl)
                ? Error<KycSessionStartModel>("NeedsOperator: this legacy verification requires correlation reconciliation.", code: AzoaErrorCodes.Conflict)
                : Ok(ToSessionStart(attempt));
        if (provider is not IPreparedHostedSessionProvider && _hostedSessions is not null && _recovery is not null)
        {
            var admission = await _hostedSessions.GetByIdAsync(sessionId, ct);
            if (!admission.IsError && admission.Result is { State: HostedSessionStates.Abandoned } closed
                && closed.OutcomeDetail == HostedSessionRecoveryOutcomes.SupersededOrphan)
                return await RetireAndRestartAsync(attempt, userId, tenantId, ct, custody);
            if (!admission.IsError && admission.Result is { } existing && IsOrphan(existing, _clock.GetUtcNow()))
                return await SupersedeOrphanAsync(attempt, existing, userId, tenantId, ct, custody);
        }
        var started = await _hostedFlow.BeginAsync(hosted, new HostedSessionRequest
        {
            FlowKind = HostedSessionFlowKinds.Kyc, SubjectUserId = userId, TenantId = tenantId,
            SessionId = sessionId, AdmissionKey = "kyc-attempt:" + attempt.Id,
            CustodialAuthority = custody,
            PolicyRevision = KycApprovalTrust.ReadPolicyVersion(attempt.ProviderResult) ?? string.Empty,
        }, ct);
        if (started.IsError || started.Result is null)
            return Error<KycSessionStartModel>(started.Message, started.Exception, started.Code);
        var attached = await _store.TryAttachHostedRedirectAsync(attempt, started.Result.RedirectUrl, started.Result.ExpiresAt, ct);
        if (attached.IsError) return Error<KycSessionStartModel>(attached.Message, attached.Exception, attached.Code);
        if (attached.Result is null)
            attached = await _store.GetSubmissionByIdAsync(Guid.ParseExact(attempt.Id, "N"), ct);
        return attached.Result is null || attached.IsError
            ? Error<KycSessionStartModel>("The verification changed; reload its status.")
            : Ok(ToSessionStart(attached.Result));
    }

    /// <summary>A sent create with no attached vendor session past the bounded wait (or quarantined).</summary>
    private bool IsOrphan(HostedSession admission, DateTimeOffset now)
        => admission.State == HostedSessionStates.Claimed
            && admission.ProviderSessionId is null
            && admission.CreationStartedAt is not null
            && (admission.CreationQuarantinedAt is not null
                || (admission.CreationClaimedAt ?? admission.CreatedDate)
                    <= now.AddMinutes(-Math.Clamp(_settings.Identomat.OrphanWaitMinutes, 1, 1440)));

    /// <summary>
    /// IDENTOMAT-RECOVERY: the vendor offers no lookup for a lost create, and an unattached vendor session is
    /// inert, so a bounded number of orphans per subject are superseded by a fresh attempt. A late original
    /// response is deleted by the flow. See Services/HostedSessions/AGENTS.md §recovery.
    /// </summary>
    private async Task<AZOAResult<KycSessionStartModel>> SupersedeOrphanAsync(KycSubmission attempt, HostedSession orphan,
        Guid userId, Guid? tenantId, CancellationToken ct, TenantCustodialWitness? custody)
    {
        var now = _clock.GetUtcNow();
        var used = await _recovery!.CountSupersededSinceAsync(HostedSessionFlowKinds.Kyc, userId, now.AddDays(-1), ct);
        if (used >= Math.Clamp(_settings.Identomat.OrphanSupersedesPerDay, 0, 10))
            return Error<KycSessionStartModel>(
                "NeedsOperator: the previous verification request is unresolved; retry tomorrow or contact support.",
                code: AzoaErrorCodes.Conflict);
        var superseded = await _recovery.TrySupersedeOrphanAsync(orphan, now, ct);
        if (superseded.IsError || superseded.Result is null)
            return Error<KycSessionStartModel>("The verification changed; retry.", code: AzoaErrorCodes.Conflict);
        return await RetireAndRestartAsync(attempt, userId, tenantId, ct, custody);
    }

    /// <summary>
    /// Second half of a supersede, also reached when a crash interrupted it: the attempt whose admission
    /// was superseded is retired and a fresh attempt begins (its session id is the attempt id, so it cannot be reused).
    /// </summary>
    private async Task<AZOAResult<KycSessionStartModel>> RetireAndRestartAsync(KycSubmission attempt,
        Guid userId, Guid? tenantId, CancellationToken ct, TenantCustodialWitness? custody)
    {
        var retired = await RetireAttemptAsync(attempt, _clock.GetUtcNow(), custody, ct);
        if (retired.IsError || retired.Result is null)
            return Error<KycSessionStartModel>("The verification changed; retry.", retired.Exception, retired.Code ?? AzoaErrorCodes.Conflict);
        return await BeginCoreAsync(userId, tenantId, ct, custody);
    }

    public Task<AZOAResult<KycSubmissionModel>> SubmitAsync(
        SubmitKycModel model,
        Guid userId,
        CancellationToken ct = default)
        => SubmitCoreAsync(model, userId, null, ct);

    private async Task<AZOAResult<KycSubmissionModel>> SubmitCoreAsync(
        SubmitKycModel model,
        Guid userId,
        Guid? tenantId,
        CancellationToken ct, TenantCustodialWitness? custody = null)
    {
        if (userId == Guid.Empty)
            return Error<KycSubmissionModel>("An authenticated user is required.");
        if (model?.Documents is null)
            return Error<KycSubmissionModel>("A document reference list is required.");
        if (model.Documents.Count > KycDocumentRequestLimits.MaxDocuments)
            return Error<KycSubmissionModel>(
                $"At most {KycDocumentRequestLimits.MaxDocuments} KYC document references are allowed.");
        if (model.Documents.Any(document => document is null))
            return Error<KycSubmissionModel>("KYC document references must not contain null entries.");
        if (model.Documents.Any(document =>
                document.Metadata?.Length > KycDocumentRequestLimits.MaxMetadataCharacters))
        {
            return Error<KycSubmissionModel>(
                $"KYC document metadata must not exceed {KycDocumentRequestLimits.MaxMetadataCharacters} characters.");
        }

        var now = DateTimeOffset.UtcNow;
        var authority = await ResolveAuthorityAsync(tenantId, ct);
        if (authority.IsError || authority.Result is null)
            return Error<KycSubmissionModel>(
                $"KYC_POLICY_UNAVAILABLE: {authority.Message}",
                code: authority.Code);
        var resolution = authority.Result;
        if (!KycApprovalTrust.TryResolveCurrentProfile(
                resolution.Provider,
                resolution.Settings,
                _environment,
                out var capabilities,
                out var profile,
                out var policyFailure))
        {
            return Error<KycSubmissionModel>($"KYC_POLICY_UNAVAILABLE: {policyFailure}");
        }

        var active = await GetActiveAsync(userId, tenantId, ct);
        if (active.IsError)
            return Error<KycSubmissionModel>(active.Message, active.Exception, active.Code);
        if (active.Result is not null)
        {
            if (!IsExpired(active.Result, now))
            {
                if (!KycApprovalTrust.IsProviderMatch(active.Result, profile))
                {
                    return Error<KycSubmissionModel>(
                        "KYC_PROVIDER_CHANGED: Start a new verification attempt with the active provider.");
                }
                if (!MatchesAuthority(active.Result, resolution, profile))
                {
                    return Error<KycSubmissionModel>(
                        "KYC_POLICY_CHANGED: Start a new verification attempt under the active policy.");
                }

                var existingDocuments = await _store.GetDocumentsBySubmissionAsync(
                    Guid.ParseExact(active.Result.Id, "N"),
                    ct);
                if (existingDocuments.IsError)
                    return Error<KycSubmissionModel>(
                        existingDocuments.Message,
                        existingDocuments.Exception,
                        existingDocuments.Code);
                if (existingDocuments.Result?.Any() == true)
                    return await WithDocuments(active.Result, tenantId, ct);

                var attachValidation = await resolution.Provider.ValidateDocumentsAsync(model.Documents, ct);
                if (attachValidation.IsError)
                    return Error<KycSubmissionModel>(
                        attachValidation.Message,
                        attachValidation.Exception,
                        attachValidation.Code);

                var attemptId = Guid.ParseExact(active.Result.Id, "N");
                var attemptDocuments = CreateDocuments(model.Documents, attemptId, now);
                active.Result.ExpiresAt = now.AddDays(
                    Math.Clamp(resolution.Settings.SubmissionExpiryDays, 1, 365));
                active.Result.ModifiedDate = now;
                var attached = custody is null
                    ? await _store.AttachDocumentsIfAbsentAsync(active.Result, attemptDocuments, ct)
                    : _store is IKycCustodialAdmissionStore custodialStore
                        ? await custodialStore.AttachCustodialDocumentsAsync(active.Result, attemptDocuments, custody, ct)
                        : Error<KycSubmission>("Custodial KYC admission is unavailable.");
                if (attached.IsError || attached.Result is null)
                    return Error<KycSubmissionModel>(attached.Message, attached.Exception, attached.Code);

                return await WithDocuments(attached.Result, tenantId, ct);
            }

            var expired = await RetireAttemptAsync(active.Result, now, custody, ct);
            if (expired.IsError || expired.Result is null)
                return Error<KycSubmissionModel>(expired.Message, expired.Exception, expired.Code);
        }

        // Validate documents via the active provider before any row is written.
        var validation = await resolution.Provider.ValidateDocumentsAsync(model.Documents, ct);
        if (validation.IsError)
            return Error<KycSubmissionModel>(validation.Message, validation.Exception, validation.Code);

        var submissionId = Guid.NewGuid();

        var documents = CreateDocuments(model.Documents, submissionId, now);

        // Establish provider availability before writing an active submission. A
        // configured-but-unavailable hosted adapter must not leave a zombie row.
        var docModels = documents.Select(ToDocumentModel).ToList();
        var session = await resolution.Provider.CreateSessionAsync(userId, docModels, ct);
        if (session.IsError || string.IsNullOrWhiteSpace(session.Result))
            return Error<KycSubmissionModel>(
                session.Message ?? "KYC provider did not create a session.",
                session.Exception,
                session.Code);

        var submission = new KycSubmission
        {
            Id                = SurrealId.ToSurrealId(submissionId),
            UserId          = SurrealId.ToSurrealId(userId),
            TenantId          = tenantId.HasValue ? SurrealId.ToSurrealId(tenantId.Value) : null,
            ProviderSelectionVersion = resolution.SelectionVersion,
            ProviderTrustRevision = resolution.TrustRevision,
            Provider          = profile.Provider,
            ProviderKey       = profile.ProviderKey,
            Status            = KycStatus.PENDING,
            ProviderSessionId = session.Result,
            HostedVerification = capabilities.HostedVerification,
            AcceptsDocumentReferences = capabilities.AcceptsDocumentReferences,
            ProviderResult    = KycApprovalTrust.CreateEnvelope(profile),
            SubmittedAt       = now,
            ExpiresAt         = now.AddDays(
                Math.Clamp(resolution.Settings.SubmissionExpiryDays, 1, 365)),
            CreatedDate       = now,
            ModifiedDate      = now
        };

        var savedSubmission = await CreateAttemptAsync(submission, documents, custody, ct);
        if (savedSubmission.IsError || savedSubmission.Result is null)
            return Error<KycSubmissionModel>(
                savedSubmission.Message,
                savedSubmission.Exception,
                savedSubmission.Code);

        var result = ToSubmissionModel(submission);
        result.Documents = docModels;
        return Ok(result);
    }

    public Task<AZOAResult<KycSubmissionModel>> GetStatusAsync(
        Guid userId,
        CancellationToken ct = default)
        => GetStatusCoreAsync(userId, null, ct);

    public Task<AZOAResult<KycSubmissionModel>> GetStatusAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken ct = default)
        => GetStatusCoreAsync(userId, tenantId, ct);

    private async Task<AZOAResult<KycSubmissionModel>> GetStatusCoreAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken ct)
    {
        var latest = await GetLatestAsync(userId, tenantId, ct);
        if (latest.IsError)
            return Error<KycSubmissionModel>(latest.Message, latest.Exception, latest.Code);
        if (latest.Result is null)
            return Error<KycSubmissionModel>($"{KycAuthorizationError.NotFound}No KYC submission found for this user.");

        return await WithDocuments(latest.Result, tenantId, ct);
    }

    public async Task<AZOAResult<KycSubmissionModel>> GetByIdAsync(Guid submissionId, Guid userId, CancellationToken ct = default)
    {
        var loaded = await LoadOwned(submissionId, userId, ct);
        if (loaded.IsError || loaded.Result is null)
            return Error<KycSubmissionModel>(loaded.Message, loaded.Exception, loaded.Code);

        return await WithDocuments(loaded.Result, null, ct);
    }

    public async Task<AZOAResult<IEnumerable<KycDocumentModel>>> ListDocumentsAsync(Guid submissionId, Guid userId, CancellationToken ct = default)
    {
        var loaded = await LoadOwned(submissionId, userId, ct);
        if (loaded.IsError || loaded.Result is null)
            return Error<IEnumerable<KycDocumentModel>>(loaded.Message, loaded.Exception, loaded.Code);

        var docs = await _store.GetDocumentsBySubmissionAsync(submissionId, ct);
        if (docs.IsError)
            return Error<IEnumerable<KycDocumentModel>>(docs.Message, docs.Exception, docs.Code);

        return Ok<IEnumerable<KycDocumentModel>>(
            (docs.Result ?? Enumerable.Empty<KycDocument>()).Select(ToDocumentModel).ToList());
    }

    // â”€â”€ Admin surface â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public async Task<AZOAResult<IEnumerable<KycSubmissionModel>>> GetPendingAsync(CancellationToken ct = default)
    {
        var pending = await _store.GetPendingAsync(ct);
        if (pending.IsError)
            return Error<IEnumerable<KycSubmissionModel>>(pending.Message, pending.Exception, pending.Code);

        var models = new List<KycSubmissionModel>();
        foreach (var submission in pending.Result ?? Enumerable.Empty<KycSubmission>())
        {
            var withDocs = await WithDocuments(submission, ParseTenantId(submission), ct);
            if (withDocs.IsError || withDocs.Result is null)
                return Error<IEnumerable<KycSubmissionModel>>(
                    withDocs.Message,
                    withDocs.Exception,
                    withDocs.Code);
            if (withDocs.Result.Status != KycStatus.EXPIRED)
                models.Add(withDocs.Result);
        }

        return Ok<IEnumerable<KycSubmissionModel>>(models);
    }

    public async Task<AZOAResult<KycSubmissionModel>> ApproveAsync(Guid submissionId, Guid reviewerUserId, string? notes, CancellationToken ct = default)
    {
        var loaded = await _store.GetSubmissionByIdAsync(submissionId, ct);
        if (loaded.IsError)
            return Error<KycSubmissionModel>(loaded.Message, loaded.Exception, loaded.Code);
        if (loaded.Result is null)
            return Error<KycSubmissionModel>($"{KycAuthorizationError.NotFound}KYC submission not found.");

        var submission = loaded.Result;
        if (submission.Provider != KycProvider.MANUAL)
            return Error<KycSubmissionModel>(
                "External-provider submissions must be decided by their verified provider event.");
        if (!_environment.IsDevelopment())
            return Error<KycSubmissionModel>("Manual KYC review is available only in Development.");
        var tenantId = ParseTenantId(submission);
        if (!string.IsNullOrWhiteSpace(submission.TenantId) && tenantId is null)
            return Error<KycSubmissionModel>("KYC submission tenant provenance is invalid.");
        var authority = await ResolveAuthorityAsync(tenantId, ct);
        if (authority.IsError || authority.Result is null)
            return Error<KycSubmissionModel>(
                $"KYC_POLICY_UNAVAILABLE: {authority.Message}",
                code: authority.Code);
        if (!KycApprovalTrust.TryResolveCurrentProfile(
                authority.Result.Provider,
                authority.Result.Settings,
                _environment,
                out _,
                out var profile,
                out var policyFailure))
        {
            return Error<KycSubmissionModel>($"KYC_POLICY_UNAVAILABLE: {policyFailure}");
        }
        if (!MatchesAuthority(submission, authority.Result, profile))
        {
            return Error<KycSubmissionModel>(
                "KYC_POLICY_CHANGED: Restart verification under the active provider policy.");
        }
        if (IsExpired(submission, DateTimeOffset.UtcNow))
            return Error<KycSubmissionModel>("Cannot approve an expired KYC submission.");
        if (submission.Status is not (KycStatus.PENDING or KycStatus.IN_REVIEW))
            return Error<KycSubmissionModel>(
                $"Cannot approve a submission with status {submission.Status}. Only PENDING or IN_REVIEW submissions can be approved.");

        var documents = await _store.GetDocumentsBySubmissionAsync(submissionId, ct);
        if (documents.IsError)
            return Error<KycSubmissionModel>(documents.Message, documents.Exception, documents.Code);
        if (!(documents.Result?.Any() ?? false))
            return Error<KycSubmissionModel>(
                "Cannot approve a KYC attempt before document references are attached.");

        var now = DateTimeOffset.UtcNow;
        submission.Status       = KycStatus.APPROVED;
        submission.ReviewerId   = SurrealId.ToSurrealId(reviewerUserId);
        submission.ReviewNotes  = notes;
        submission.ReviewedAt   = now;
        // An APPROVAL lives on Kyc:ApprovalValidityDays, never on the far shorter
        // Kyc:SubmissionExpiryDays the PENDING attempt was stamped with. Without
        // this restamp the approval inherits the attempt's expiry and every
        // verified sponsor is forced back through identity check a month later.
        submission.ExpiresAt    = KycApprovalValidity.ResolveExpiry(_configuration, now);
        submission.ModifiedDate = now;

        var saved = await _store.TryReviewAsync(submission, ct);
        if (saved.IsError || saved.Result is null)
            return Error<KycSubmissionModel>(
                saved.Result is null && !saved.IsError
                    ? "This KYC submission has already been decided by another reviewer."
                    : saved.Message,
                saved.Exception,
                saved.Code);

        return await WithDocuments(saved.Result, tenantId, ct);
    }

    public async Task<AZOAResult<KycSubmissionModel>> RejectAsync(Guid submissionId, Guid reviewerUserId, string? notes, string? rejectionReason, CancellationToken ct = default)
    {
        var loaded = await _store.GetSubmissionByIdAsync(submissionId, ct);
        if (loaded.IsError)
            return Error<KycSubmissionModel>(loaded.Message, loaded.Exception, loaded.Code);
        if (loaded.Result is null)
            return Error<KycSubmissionModel>($"{KycAuthorizationError.NotFound}KYC submission not found.");

        var submission = loaded.Result;
        if (submission.Provider != KycProvider.MANUAL)
            return Error<KycSubmissionModel>(
                "External-provider submissions must be decided by their verified provider event.");
        if (!_environment.IsDevelopment())
            return Error<KycSubmissionModel>("Manual KYC review is available only in Development.");
        var tenantId = ParseTenantId(submission);
        if (!string.IsNullOrWhiteSpace(submission.TenantId) && tenantId is null)
            return Error<KycSubmissionModel>("KYC submission tenant provenance is invalid.");
        var authority = await ResolveAuthorityAsync(tenantId, ct);
        if (authority.IsError || authority.Result is null
            || !KycApprovalTrust.TryResolveCurrentProfile(
                authority.Result.Provider,
                authority.Result.Settings,
                _environment,
                out _,
                out var profile,
                out _)
            || !MatchesAuthority(submission, authority.Result, profile))
        {
            return Error<KycSubmissionModel>(
                "KYC_POLICY_CHANGED: Restart verification under the active provider policy.");
        }
        if (IsExpired(submission, DateTimeOffset.UtcNow))
            return Error<KycSubmissionModel>("Cannot reject an expired KYC submission.");
        if (submission.Status is not (KycStatus.PENDING or KycStatus.IN_REVIEW))
            return Error<KycSubmissionModel>(
                $"Cannot reject a submission with status {submission.Status}. Only PENDING or IN_REVIEW submissions can be rejected.");
        if (string.IsNullOrWhiteSpace(rejectionReason) || rejectionReason.Trim().Length > 500)
            return Error<KycSubmissionModel>("A bounded rejection reason is required.");

        var now = DateTimeOffset.UtcNow;
        submission.Status          = KycStatus.REJECTED;
        submission.ReviewerId      = SurrealId.ToSurrealId(reviewerUserId);
        submission.ReviewNotes     = notes;
        submission.RejectionReason = rejectionReason;
        submission.ReviewedAt      = now;
        submission.ModifiedDate    = now;

        var saved = await _store.TryReviewAsync(submission, ct);
        if (saved.IsError || saved.Result is null)
            return Error<KycSubmissionModel>(
                saved.Result is null && !saved.IsError
                    ? "This KYC submission has already been decided by another reviewer."
                    : saved.Message,
                saved.Exception,
                saved.Code);

        return await WithDocuments(saved.Result, tenantId, ct);
    }

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Loads a submission by id and asserts the authenticated user owns it.
    /// Returns <see cref="KycAuthorizationError.NotFound"/> when the id is unknown
    /// and <see cref="KycAuthorizationError.Forbidden"/> when it is owned by a
    /// different user (the IDOR hardening vs the unscoped source).
    /// </summary>
    private async Task<AZOAResult<KycSubmission>> LoadOwned(Guid submissionId, Guid userId, CancellationToken ct)
    {
        var loaded = await _store.GetSubmissionByIdAsync(submissionId, ct);
        if (loaded.IsError)
            return Error<KycSubmission>(loaded.Message, loaded.Exception, loaded.Code);
        if (loaded.Result is null)
            return Error<KycSubmission>($"{KycAuthorizationError.NotFound}KYC submission not found.");

        if (!OwnedBy(loaded.Result, userId)
            || !string.IsNullOrWhiteSpace(loaded.Result.TenantId))
            return Error<KycSubmission>($"{KycAuthorizationError.Forbidden}{KycAuthorizationError.VerificationRequiredMessage}");

        return Ok(loaded.Result);
    }

    private static bool OwnedBy(KycSubmission submission, Guid userId)
        => Guid.TryParse(submission.UserId, out var owner)
           && owner == userId;

    private async Task<AZOAResult<KycSubmissionModel>> WithDocuments(
        KycSubmission submission,
        Guid? tenantId,
        CancellationToken ct)
    {
        var stampedTenant = ParseTenantId(submission);
        if ((!string.IsNullOrWhiteSpace(submission.TenantId) && stampedTenant is null)
            || stampedTenant != tenantId)
        {
            return Error<KycSubmissionModel>(
                "KYC submission authority provenance is invalid for this request.");
        }
        if (!Guid.TryParse(submission.Id, out var submissionId))
            return Error<KycSubmissionModel>("KYC submission has an unparseable id.");

        var docs = await _store.GetDocumentsBySubmissionAsync(submissionId, ct);
        if (docs.IsError)
            return Error<KycSubmissionModel>(docs.Message, docs.Exception, docs.Code);

        var model = ToSubmissionModel(submission);
        if (await IsEffectivelyStaleAsync(submission, tenantId, DateTimeOffset.UtcNow, ct))
            model.Status = KycStatus.EXPIRED;
        model.Documents = (docs.Result ?? Enumerable.Empty<KycDocument>()).Select(ToDocumentModel).ToList();
        return Ok(model);
    }

    private static bool IsExpired(KycSubmission submission, DateTimeOffset now)
        => submission.ExpiresAt is not { } expiresAt || expiresAt <= now;

    private async Task<bool> IsEffectivelyStaleAsync(
        KycSubmission submission,
        Guid? tenantId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (IsExpired(submission, now))
            return true;
        if (submission.Status is not (KycStatus.PENDING or KycStatus.IN_REVIEW or KycStatus.APPROVED))
            return false;
        var authority = await ResolveAuthorityAsync(tenantId, ct);
        if (authority.IsError || authority.Result is null
            || !KycApprovalTrust.TryResolveCurrentProfile(
                authority.Result.Provider,
                authority.Result.Settings,
                _environment,
                out _,
                out var profile,
                out _))
        {
            return true;
        }

        return !MatchesAuthority(submission, authority.Result, profile)
            || (submission.Status == KycStatus.APPROVED
                && (!KycApprovalTrust.IsCurrentApproval(submission, profile, now, out _)
                    || (submission.HostedVerification && string.IsNullOrWhiteSpace(submission.AmlLineageKey))));
    }

    private async Task<AZOAResult<KycProviderResolution>> ResolveAuthorityAsync(
        Guid? tenantId,
        CancellationToken ct)
    {
        if (tenantId.HasValue)
        {
            if (_registry is null)
                return AZOAResult<KycProviderResolution>.Failure(
                    KycProviderReadinessCodes.SelectionRequired);
            return await _registry.ResolveTenantAsync(tenantId.Value, ct);
        }

        return _registry?.ResolveNodeDefault()
            ?? AZOAResult<KycProviderResolution>.Success(new KycProviderResolution(
                _provider,
                _settings,
                null,
                0,
                0,
                _provider.GetCapabilities().ProviderKey));
    }

    private Task<AZOAResult<KycSubmission>> GetLatestAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken ct)
        => tenantId.HasValue || _registry is not null
            ? _store.GetLatestSubmissionAsync(userId, tenantId, ct)
            : _store.GetLatestSubmissionByUserAsync(userId, ct);

    private Task<AZOAResult<KycSubmission>> GetActiveAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken ct)
        => tenantId.HasValue || _registry is not null
            ? _store.GetActiveSubmissionAsync(userId, tenantId, ct)
            : _store.GetActiveSubmissionByUserAsync(userId, ct);

    private static bool MatchesAuthority(
        KycSubmission submission,
        KycProviderResolution resolution,
        KycApprovalProfile profile)
        => string.Equals(
                submission.ProviderKey,
                resolution.Provider.ProviderKey,
                StringComparison.Ordinal)
            && submission.ProviderSelectionVersion == resolution.SelectionVersion
            && submission.ProviderTrustRevision == resolution.TrustRevision
            && KycApprovalTrust.MatchesCurrentAttempt(submission, profile, out _);

    private static Guid? ParseTenantId(KycSubmission submission)
        => Guid.TryParse(submission.TenantId, out var tenantId) ? tenantId : null;

    private static KycDocumentModel ToDocumentModel(KycDocument d) => new()
    {
        Id            = Guid.TryParse(d.Id, out var id) ? id : Guid.Empty,
        SubmissionId  = Guid.TryParse(d.SubmissionId, out var sid) ? sid : Guid.Empty,
        Type          = d.Type,
        FileUrl       = d.FileUrl,
        FileName      = d.FileName,
        MimeType      = d.MimeType,
        FileSizeBytes = d.FileSizeBytes,
        Metadata      = d.Metadata,
        CreatedDate   = d.CreatedDate.UtcDateTime
    };

    private static KycSubmissionModel ToSubmissionModel(KycSubmission s) => new()
    {
        Id                = Guid.TryParse(s.Id, out var id) ? id : Guid.Empty,
        UserId          = Guid.TryParse(s.UserId, out var aid) ? aid : Guid.Empty,
        TenantId          = Guid.TryParse(s.TenantId, out var tenantId) ? tenantId : null,
        ProviderSelectionVersion = s.ProviderSelectionVersion,
        ProviderTrustRevision = s.ProviderTrustRevision,
        Provider          = s.Provider,
        ProviderKey       = s.ProviderKey,
        Status            = s.Status,
        ReviewerId        = s.ReviewerId,
        ReviewNotes       = s.ReviewNotes,
        RejectionReason   = s.RejectionReason,
        ProviderSessionId = s.ProviderSessionId,
        AmlLineageKey = s.AmlLineageKey,
        HostedVerification = s.HostedVerification,
        AcceptsDocumentReferences = s.AcceptsDocumentReferences,
        VerificationUrl    = s.VerificationUrl,
        SessionInstructions = s.SessionInstructions,
        ProviderResult    = s.ProviderResult,
        SubmittedAt       = s.SubmittedAt.UtcDateTime,
        ReviewedAt        = s.ReviewedAt?.UtcDateTime,
        ExpiresAt         = s.ExpiresAt?.UtcDateTime,
        CreatedDate       = s.CreatedDate.UtcDateTime,
        ModifiedDate      = s.ModifiedDate?.UtcDateTime
    };

    private KycSessionStartModel ToSessionStart(KycSubmission submission) => new()
    {
        Provider = submission.Provider,
        ProviderKey = string.IsNullOrWhiteSpace(submission.ProviderKey)
            ? submission.Provider.ToString().ToLowerInvariant().Replace('_', '-')
            : submission.ProviderKey,
        HostedVerification = submission.HostedVerification,
        AcceptsDocumentReferences = submission.AcceptsDocumentReferences,
        DevelopmentSimulation = _environment.IsDevelopment()
            && submission.Provider == KycProvider.MANUAL,
        ProviderSessionId = submission.ProviderSessionId,
        VerificationUrl = submission.VerificationUrl,
        ExpiresAt = submission.ExpiresAt?.UtcDateTime,
        Instructions = submission.SessionInstructions
    };

    private KycProviderCapabilitiesModel RuntimeCapabilities(KycProviderCapabilitiesModel capabilities)
    {
        capabilities.DevelopmentSimulation = _environment.IsDevelopment()
            && capabilities.Available
            && capabilities.Provider == KycProvider.MANUAL;
        return capabilities;
    }

    private static List<KycDocument> CreateDocuments(
        IEnumerable<SubmitKycDocumentModel> source,
        Guid submissionId,
        DateTimeOffset now)
        => source.Select(document => new KycDocument
        {
            Id = SurrealId.ToSurrealId(Guid.NewGuid()),
            SubmissionId = SurrealId.ToSurrealId(submissionId),
            Type = document.Type,
            FileUrl = document.FileUrl,
            FileName = document.FileName,
            MimeType = document.MimeType,
            FileSizeBytes = document.FileSizeBytes,
            Metadata = document.Metadata,
            CreatedDate = now
        }).ToList();

    private static AZOAResult<T> Ok<T>(T result) => new() { Result = result, Message = "Success" };

    private static AZOAResult<T> Error<T>(
        string message,
        Exception? ex = null,
        string? code = null)
    {
        var r = new AZOAResult<T> { IsError = true, Message = message, Code = code };
        if (ex is not null) r.Exception = ex;
        return r;
    }
}

/// <summary>
/// The single source of truth for how long a KYC APPROVAL stays current, split
/// from <c>Kyc:SubmissionExpiryDays</c> (how long an unfinished ATTEMPT may sit).
/// </summary>
/// <remarks>
/// Read through <see cref="IConfiguration"/> rather than <c>KycSettings</c> so
/// every seam that terminalizes a submission - the manual reviewer path here and
/// the hosted-provider outcome handler - resolves one number from one key. See
/// <c>docs/NODE-HOST.md</c> section 8.5.
/// </remarks>
public static class KycApprovalValidity
{
    /// <summary>Configuration key holding the approval lifetime in days.</summary>
    public const string ConfigurationKey = "Kyc:ApprovalValidityDays";

    /// <summary>Twelve months: the shortest lifetime that is not re-verification theatre.</summary>
    public const int DefaultDays = 365;

    public const int MinimumDays = 1;
    public const int MaximumDays = 3650;

    /// <summary>Clamped approval lifetime; an absent or unparseable value falls back to twelve months.</summary>
    /// <remarks>
    /// Parsed rather than bound: an operator typo must not throw on the approval
    /// path, and a nonsense value must fall back to the safe default instead of
    /// silently becoming zero (an approval that expires the instant it is granted).
    /// </remarks>
    public static int ResolveDays(IConfiguration? configuration)
    {
        var configured = configuration?[ConfigurationKey];
        return int.TryParse(configured, out var days) && days > 0
            ? Math.Clamp(days, MinimumDays, MaximumDays)
            : DefaultDays;
    }

    /// <summary>Absolute expiry to stamp on a submission at the moment it is approved.</summary>
    public static DateTimeOffset ResolveExpiry(IConfiguration? configuration, DateTimeOffset approvedAt)
        => approvedAt.AddDays(ResolveDays(configuration));
}
