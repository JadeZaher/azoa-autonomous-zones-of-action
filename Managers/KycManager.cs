// SPDX-License-Identifier: UNLICENSED

using SurrealForge.Client;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using AZOA.WebAPI.Interfaces.HostedSessions;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;

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
    private readonly IKycProviderService _provider;
    private readonly IKycProviderRegistry? _registry;
    private readonly KycSettings _settings;
    private readonly IHostEnvironment _environment;

    public KycManager(
        IKycStore store,
        IKycProviderService provider,
        IOptions<KycSettings> settings,
        IHostEnvironment environment)
    {
        _store = store;
        _provider = provider;
        _registry = null;
        _settings = settings.Value;
        _environment = environment;
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public KycManager(
        IKycStore store,
        IKycProviderService provider,
        IKycProviderRegistry registry,
        IOptions<KycSettings> settings,
        IHostEnvironment environment)
    {
        _store = store;
        _provider = provider;
        _registry = registry;
        _settings = settings.Value;
        _environment = environment;
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

    public Task<AZOAResult<KycSessionStartModel>> BeginAsync(
        Guid userId,
        Guid tenantId,
        CancellationToken ct = default)
        => BeginCoreAsync(userId, tenantId, ct);

    private async Task<AZOAResult<KycSessionStartModel>> BeginCoreAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken ct)
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
            return Ok(ToSessionStart(active.Result));
        }
        if (active.Result is not null)
        {
            active.Result.Status = KycStatus.EXPIRED;
            active.Result.ModifiedDate = now;
            var expired = await _store.UpsertSubmissionAsync(active.Result, ct);
            if (expired.IsError)
                return Error<KycSessionStartModel>(expired.Message, expired.Exception, expired.Code);
        }

        var latest = await GetLatestAsync(userId, tenantId, ct);
        if (latest.IsError)
            return Error<KycSessionStartModel>(latest.Message, latest.Exception, latest.Code);
        if (latest.Result is not null
            && MatchesAuthority(latest.Result, resolution, profile)
            && KycApprovalTrust.IsCurrentApproval(latest.Result, profile, now, out _))
        {
            return Error<KycSessionStartModel>(
                "KYC_ALREADY_APPROVED: This identity already has an active approval.");
        }

        // A hosted provider stays refused unless it implements the durable
        // admission seam (IHostedSessionProvider / HostedSessionFlow, D11). This
        // gates on the SEAM being used, not merely on the HostedVerification flag:
        // GenericHostedKycProviderService and VeriffKycProviderService both report
        // HostedVerification=true while remaining fail-closed stubs that do not
        // implement IHostedSessionProvider, so they stay refused here regardless of
        // whatever their own Available value happens to be. IdentomatKycProviderService
        // implements the seam and internally routes its BeginSessionAsync through
        // IHostedSessionFlow.BeginAsync, which claims durable admission (keyed by a
        // stable per-(user, tenant) admission key) BEFORE touching the vendor -
        // see Services/HostedSessions/AGENTS.md. That is what makes it safe for two
        // concurrent begins to converge on one provider session instead of two: the
        // manual flow has no external side effect, and any seam-implementing hosted
        // provider claims its own durable row before any external side effect.
        if (capabilities.HostedVerification && resolution.Provider is not IHostedSessionProvider)
        {
            return Error<KycSessionStartModel>(
                "KYC provider is unavailable until durable hosted-session admission is installed.");
        }

        // Ordering note (why this call site does not itself persist-before-call):
        // for a provider gated above, the durable-admission-before-external-call
        // invariant that Providers/Kyc/AGENTS.md requires ("persist the hosted
        // session before returning a redirect") is enforced INSIDE this call by
        // HostedSessionFlow.BeginAsync, not by statement order here. By the time
        // BeginSessionAsync returns, a hosted provider has already durably claimed
        // its hosted_session row (and, on success, attached the vendor's session id
        // to it) - the KycSubmission this method persists afterward is a downstream
        // ledger projection with no external side effect of its own, so writing it
        // after the provider call is safe rather than a repeat of the ordering bug.
        // Routing every hosted provider through HostedSessionFlow (enforced by the
        // IHostedSessionProvider gate above) is therefore the enforced path that
        // makes the original ordering trap structurally impossible to hit again.
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
        var created = await _store.CreateSubmissionAsync(attempt, Array.Empty<KycDocument>(), ct);
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

    public Task<AZOAResult<KycSubmissionModel>> SubmitAsync(
        SubmitKycModel model,
        Guid userId,
        CancellationToken ct = default)
        => SubmitCoreAsync(model, userId, null, ct);

    public Task<AZOAResult<KycSubmissionModel>> SubmitAsync(
        SubmitKycModel model,
        Guid userId,
        Guid tenantId,
        CancellationToken ct = default)
        => SubmitCoreAsync(model, userId, tenantId, ct);

    private async Task<AZOAResult<KycSubmissionModel>> SubmitCoreAsync(
        SubmitKycModel model,
        Guid userId,
        Guid? tenantId,
        CancellationToken ct)
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
                var attached = await _store.AttachDocumentsIfAbsentAsync(
                    active.Result,
                    attemptDocuments,
                    ct);
                if (attached.IsError || attached.Result is null)
                    return Error<KycSubmissionModel>(attached.Message, attached.Exception, attached.Code);

                return await WithDocuments(attached.Result, tenantId, ct);
            }

            active.Result.Status = KycStatus.EXPIRED;
            active.Result.ModifiedDate = now;
            var expired = await _store.UpsertSubmissionAsync(active.Result, ct);
            if (expired.IsError)
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

        var savedSubmission = await _store.CreateSubmissionAsync(submission, documents, ct);
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

    // ── Admin surface ─────────────────────────────────────────────────────────

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

    // ── Helpers ─────────────────────────────────────────────────────────────

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
                && !KycApprovalTrust.IsCurrentApproval(submission, profile, now, out _));
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
