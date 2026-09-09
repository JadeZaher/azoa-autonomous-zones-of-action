using TextEncoding = System.Text.Encoding;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Admin;
using AZOA.WebAPI.Services.Kyc;
using SurrealForge.Client;

namespace AZOA.WebAPI.Managers;

/// <summary>Coordinates safe operator and tenant KYC configuration actions.</summary>
public sealed class KycControlPlaneManager : IKycControlPlaneManager
{
    private const int MaxAuditCursorLength = 256;

    /// <summary>Risk-policy stamp on a screening row a human decided, not the scoring engine.</summary>
    public const string ManualReviewPolicyVersion = "azoa-manual-review-v1";

    private const int MaxReviewReasonLength = 2000;
    private const int MaxScreeningIdLength = 128;

    /// <summary>Machine-readable refusal when a non-operator principal reaches the review desk.</summary>
    public const string OperatorOnlyReason = "AML_REVIEW_IS_NODE_OPERATOR_ONLY";
    private static readonly IReadOnlySet<string> AuditActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "profile.trust-change",
        "profile.metadata-change",
        "tenant.provider-selection",
    };
    private readonly IKycControlStore _control;
    private readonly IKycStore _kycStore;
    private readonly IKycProviderRegistry _registry;
    private readonly IKycManager _kyc;
    private readonly IApiKeyStore _apiKeys;
    private readonly IUserStore _users;
    private readonly IAdminBootstrapStateStore _operatorState;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    /// <summary>AML screening ledger; absent means the review desk REFUSES, never that the queue is empty.</summary>
    private readonly IKycScreeningStore? _screening;

    /// <summary>Carries the operator reason for an AML decision, which the screening row has no column for.</summary>
    private readonly ILogger<KycControlPlaneManager>? _logger;

    public KycControlPlaneManager(
        IKycControlStore control,
        IKycStore kycStore,
        IKycProviderRegistry registry,
        IKycManager kyc,
        IApiKeyStore apiKeys,
        IUserStore users,
        IAdminBootstrapStateStore operatorState,
        IHostEnvironment environment,
        IConfiguration configuration,
        IKycScreeningStore? screening = null,
        ILogger<KycControlPlaneManager>? logger = null)
    {
        _control = control;
        _kycStore = kycStore;
        _registry = registry;
        _kyc = kyc;
        _apiKeys = apiKeys;
        _users = users;
        _operatorState = operatorState;
        _environment = environment;
        _configuration = configuration;
        _screening = screening;
        _logger = logger;
    }

    public Task<AZOAResult<IReadOnlyList<KycProviderProfileResponse>>> ListProfilesAsync(
        CancellationToken ct = default)
        => _registry.ListProfilesAsync(ct);

    public async Task<AZOAResult<KycProviderProfileResponse>> UpdateProfileAsync(
        string providerKey,
        UpdateKycProviderProfileRequest request,
        Guid operatorUserId,
        CancellationToken ct = default)
    {
        providerKey = KycProviderRegistry.NormalizeProviderKey(providerKey);
        var adapterKey = KycProviderRegistry.NormalizeAdapter(request.AdapterKey);
        if (!KycProviderRegistry.IsSafeProviderKey(providerKey)
            || adapterKey is null
            || !string.Equals(providerKey, adapterKey, StringComparison.Ordinal))
        {
            return Invalid<KycProviderProfileResponse>(
                "Provider keys must identify one allowlisted adapter: manual, veriff, or generic-hosted.");
        }
        if (!IsSafe(request.DisplayName, 80)
            || !IsSafe(request.PolicyVersion, 128)
            || !IsSafe(request.AssuranceLevel, 128))
        {
            return Invalid<KycProviderProfileResponse>(
                "Display name, policy version, and assurance level are required and bounded.");
        }

        var currentResult = await _control.GetProfileAsync(providerKey, ct);
        if (currentResult.IsError)
            return CopyFailure<KycProviderProfile?, KycProviderProfileResponse>(currentResult);
        var current = currentResult.Result;
        if (current is null && request.ExpectedVersion is not (null or 0))
            return Conflict<KycProviderProfileResponse>("KYC provider profile version conflict.");
        if (current is not null
            && (request.ExpectedVersion is null || request.ExpectedVersion != current.Version))
        {
            return Conflict<KycProviderProfileResponse>("KYC provider profile version conflict.");
        }
        if (current is not null
            && !string.Equals(current.AdapterKey, adapterKey, StringComparison.Ordinal))
        {
            return Conflict<KycProviderProfileResponse>("A provider profile adapter is immutable.");
        }

        var trustChanged = current is null
            || current.Enabled != request.Enabled
            || !string.Equals(current.AdapterKey, adapterKey, StringComparison.Ordinal)
            || !string.Equals(current.PolicyVersion, request.PolicyVersion.Trim(), StringComparison.Ordinal)
            || !string.Equals(current.AssuranceLevel, request.AssuranceLevel.Trim(), StringComparison.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var profile = new KycProviderProfile
        {
            Id = providerKey,
            DisplayName = request.DisplayName.Trim(),
            AdapterKey = adapterKey,
            Enabled = request.Enabled,
            PolicyVersion = request.PolicyVersion.Trim(),
            AssuranceLevel = request.AssuranceLevel.Trim(),
            Version = (current?.Version ?? 0) + 1,
            TrustRevision = current is null
                ? 1
                : current.TrustRevision + (trustChanged ? 1 : 0),
            UpdatedByUserId = SurrealId.ToSurrealId(operatorUserId),
            CreatedAt = current?.CreatedAt ?? now,
            UpdatedAt = now,
        };
        var evaluated = _registry.EvaluateCandidate(profile);
        if (request.Enabled && evaluated.ReadinessCode != KycProviderReadinessCodes.Ready)
            return PolicyUnavailable<KycProviderProfileResponse>(evaluated.ReadinessCode);

        var audit = new KycControlAudit
        {
            Id = SurrealId.ToSurrealId(Guid.NewGuid()),
            Action = trustChanged ? "profile.trust-change" : "profile.metadata-change",
            ProviderKey = providerKey,
            Version = profile.Version,
            PreviousDisplayName = current?.DisplayName,
            DisplayName = profile.DisplayName,
            PreviousAdapterKey = current?.AdapterKey,
            AdapterKey = profile.AdapterKey,
            PreviousEnabled = current?.Enabled,
            Enabled = profile.Enabled,
            PreviousPolicyVersion = current?.PolicyVersion,
            PolicyVersion = profile.PolicyVersion,
            PreviousAssuranceLevel = current?.AssuranceLevel,
            AssuranceLevel = profile.AssuranceLevel,
            PreviousTrustRevision = current?.TrustRevision,
            TrustRevision = profile.TrustRevision,
            ActorUserId = SurrealId.ToSurrealId(operatorUserId),
            OccurredAt = now,
        };
        var saved = await _control.SaveProfileAsync(
            profile,
            audit,
            current is null ? null : current.Version,
            trustChanged,
            ct);
        return saved.IsError
            ? CopyFailure<KycProviderProfile, KycProviderProfileResponse>(saved)
            : AZOAResult<KycProviderProfileResponse>.Success(_registry.EvaluateCandidate(saved.Result!));
    }

    public async Task<AZOAResult<IReadOnlyList<TenantKycProviderChoiceResponse>>> ListTenantChoicesAsync(
        Guid tenantId,
        CancellationToken ct = default)
    {
        var authority = await HasCurrentTenantAuthorityAsync(tenantId, ct);
        if (authority.IsError)
            return CopyFailure<bool, IReadOnlyList<TenantKycProviderChoiceResponse>>(authority);
        if (!authority.Result)
            return Forbidden<IReadOnlyList<TenantKycProviderChoiceResponse>>("TENANT_CONFIGURATION_FORBIDDEN");
        // The read mirrors the write gate: a tenant the operator has not admitted to
        // the override allow-list is never shown a provider menu it cannot act on.
        if (!TenantKycProviderOverridePolicy.IsAllowedForTenant(_configuration, tenantId))
            return AZOAResult<IReadOnlyList<TenantKycProviderChoiceResponse>>.Success(
                Array.Empty<TenantKycProviderChoiceResponse>());
        return await _registry.ListTenantChoicesAsync(ct);
    }

    public async Task<AZOAResult<TenantKycSelectionResponse>> GetTenantSelectionAsync(
        Guid tenantId,
        bool requireTenantAuthority,
        CancellationToken ct = default)
    {
        if (requireTenantAuthority)
        {
            var authority = await HasCurrentTenantAuthorityAsync(tenantId, ct);
            if (authority.IsError)
                return CopyFailure<bool, TenantKycSelectionResponse>(authority);
            if (!authority.Result)
                return Forbidden<TenantKycSelectionResponse>("TENANT_CONFIGURATION_FORBIDDEN");
        }
        var selection = await _control.GetSelectionAsync(tenantId, ct);
        if (selection.IsError)
            return CopyFailure<TenantKycProviderSelection?, TenantKycSelectionResponse>(selection);
        return await ToSelectionResponseAsync(tenantId, selection.Result, ct);
    }

    public async Task<AZOAResult<TenantKycSelectionResponse>> SelectTenantProviderAsync(
        Guid tenantId,
        SelectTenantKycProviderRequest request,
        Guid actorUserId,
        bool requireTenantAuthority,
        CancellationToken ct = default)
    {
        var authority = await HasCurrentTenantAuthorityAsync(tenantId, ct);
        if (authority.IsError)
            return CopyFailure<bool, TenantKycSelectionResponse>(authority);
        if (!authority.Result)
            return requireTenantAuthority
                ? Forbidden<TenantKycSelectionResponse>("TENANT_CONFIGURATION_FORBIDDEN")
                : NotFound<TenantKycSelectionResponse>("Tenant not found.");
        // D48: the KYC provider IS the compliance policy, so a tenant may only
        // repoint its own provider when the OPERATOR has admitted that tenant to the
        // node-config allow-list. requireTenantAuthority == false is the operator's
        // own route (NodeOperatorController), which is the custodian and not gated.
        if (requireTenantAuthority
            && !TenantKycProviderOverridePolicy.IsAllowedForTenant(_configuration, tenantId))
        {
            return Forbidden<TenantKycSelectionResponse>(
                TenantKycProviderOverridePolicy.ForbiddenReason);
        }
        var tenant = await _users.GetByIdAsync(tenantId, ct);
        if (tenant.IsError)
            return string.Equals(tenant.Code, AzoaErrorCodes.NotFound, StringComparison.Ordinal)
                ? NotFound<TenantKycSelectionResponse>("Tenant not found.")
                : DependencyUnavailable<TenantKycSelectionResponse>();
        if (tenant.Result is null || !tenant.Result.IsActive || tenantId == NodeOperatorIdentity.UserId)
        {
            return NotFound<TenantKycSelectionResponse>("Tenant not found.");
        }

        var providerKey = KycProviderRegistry.NormalizeProviderKey(request.ProviderKey);
        var evaluated = await _registry.EvaluateProfileAsync(providerKey, ct);
        if (evaluated.IsError)
            return string.Equals(evaluated.Code, AzoaErrorCodes.DependencyUnavailable, StringComparison.Ordinal)
                ? CopyFailure<KycProviderProfileResponse, TenantKycSelectionResponse>(evaluated)
                : PolicyUnavailable<TenantKycSelectionResponse>(evaluated.Message);
        if (evaluated.Result is null
            || !evaluated.Result.Enabled
            || !evaluated.Result.Available
            || evaluated.Result.ReadinessCode != KycProviderReadinessCodes.Ready)
        {
            return PolicyUnavailable<TenantKycSelectionResponse>(
                evaluated.Result?.ReadinessCode ?? evaluated.Message);
        }

        var currentResult = await _control.GetSelectionAsync(tenantId, ct);
        if (currentResult.IsError)
            return CopyFailure<TenantKycProviderSelection?, TenantKycSelectionResponse>(currentResult);
        var current = currentResult.Result;
        if (current is null && request.ExpectedVersion is not (null or 0))
            return Conflict<TenantKycSelectionResponse>("Tenant KYC provider selection version conflict.");
        if (current is not null
            && (request.ExpectedVersion is null || request.ExpectedVersion != current.SelectionVersion))
        {
            return Conflict<TenantKycSelectionResponse>("Tenant KYC provider selection version conflict.");
        }
        if (current is not null && string.Equals(current.ProviderKey, providerKey, StringComparison.Ordinal))
            return await ToSelectionResponseAsync(tenantId, current, ct);

        var now = DateTimeOffset.UtcNow;
        var selection = new TenantKycProviderSelection
        {
            Id = SurrealId.ToSurrealId(tenantId),
            TenantId = SurrealId.ToSurrealId(tenantId),
            ProviderKey = providerKey,
            SelectionVersion = (current?.SelectionVersion ?? 0) + 1,
            UpdatedByUserId = SurrealId.ToSurrealId(actorUserId),
            CreatedAt = current?.CreatedAt ?? now,
            UpdatedAt = now,
        };
        var audit = new KycControlAudit
        {
            Id = SurrealId.ToSurrealId(Guid.NewGuid()),
            Action = "tenant.provider-selection",
            TenantId = SurrealId.ToSurrealId(tenantId),
            ProviderKey = providerKey,
            PreviousProviderKey = current?.ProviderKey,
            Version = selection.SelectionVersion,
            ActorUserId = SurrealId.ToSurrealId(actorUserId),
            OccurredAt = now,
        };
        var saved = await _control.SaveSelectionAsync(
            selection,
            audit,
            current is null ? null : current.SelectionVersion,
            ct);
        if (saved.IsError)
            return CopyFailure<TenantKycProviderSelection, TenantKycSelectionResponse>(saved);
        return await ToSelectionResponseAsync(tenantId, saved.Result, ct);
    }

    public async Task<AZOAResult<CursorPage<OperatorTenantKycSummaryResponse>>> ListTenantsAsync(
        int limit,
        string? cursor,
        string? search,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > 100 || (search?.Length ?? 0) > 100 || !TryDecodeCursor(cursor, out var offset))
            return Invalid<CursorPage<OperatorTenantKycSummaryResponse>>("Invalid pagination request.");
        var users = await _users.ListTenantPrincipalsPageAsync(offset, limit + 1, search, ct);
        if (users.IsError || users.Result is null)
            return DependencyUnavailable<CursorPage<OperatorTenantKycSummaryResponse>>();

        var selected = users.Result.Take(limit).ToList();
        var items = new List<OperatorTenantKycSummaryResponse>(selected.Count);
        foreach (var user in selected)
        {
            var selection = await _control.GetSelectionAsync(user.Id, ct);
            if (selection.IsError)
                return CopyFailure<TenantKycProviderSelection?, CursorPage<OperatorTenantKycSummaryResponse>>(selection);
            var response = await ToSelectionResponseAsync(user.Id, selection.Result, ct);
            if (response.IsError || response.Result is null)
                return CopyFailure<TenantKycSelectionResponse, CursorPage<OperatorTenantKycSummaryResponse>>(response);
            items.Add(new OperatorTenantKycSummaryResponse
            {
                TenantId = response.Result.TenantId,
                Username = user.Username,
                ProviderKey = response.Result.ProviderKey,
                ProviderDisplayName = response.Result.ProviderDisplayName,
                SelectionVersion = response.Result.SelectionVersion,
                ProviderEnabled = response.Result.ProviderEnabled,
                ProviderAvailable = response.Result.ProviderAvailable,
                ReadinessCode = response.Result.ReadinessCode,
                UpdatedAt = response.Result.UpdatedAt,
            });
        }

        return AZOAResult<CursorPage<OperatorTenantKycSummaryResponse>>.Success(new CursorPage<OperatorTenantKycSummaryResponse>
        {
            Items = items,
            NextCursor = users.Result.Count > limit ? EncodeCursor(offset + limit) : null,
        });
    }

    public async Task<AZOAResult<CursorPage<OperatorKycSubmissionQueueItem>>> ListQueueAsync(
        string status,
        int limit,
        string? cursor,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > 100 || !TryDecodeCursor(cursor, out var offset))
            return Invalid<CursorPage<OperatorKycSubmissionQueueItem>>("Invalid pagination request.");
        var normalizedStatus = status?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedStatus is not ("pending" or "approved" or "rejected"))
            return Invalid<CursorPage<OperatorKycSubmissionQueueItem>>("Unsupported KYC status filter.");
        var rows = await _kycStore.GetEffectiveOperatorPageAsync(
            normalizedStatus, offset, limit + 1, ct);
        if (rows.IsError)
            return CopyFailure<IReadOnlyList<KycSubmission>, CursorPage<OperatorKycSubmissionQueueItem>>(rows);

        var items = new List<OperatorKycSubmissionQueueItem>();
        foreach (var row in rows.Result!.Take(limit))
        {
            var item = await ToQueueItemAsync(row, ct);
            if (item.IsError || item.Result is null)
                return CopyFailure<OperatorKycSubmissionQueueItem, CursorPage<OperatorKycSubmissionQueueItem>>(item);
            items.Add(item.Result);
        }
        return AZOAResult<CursorPage<OperatorKycSubmissionQueueItem>>.Success(new CursorPage<OperatorKycSubmissionQueueItem>
        {
            Items = items,
            NextCursor = rows.Result!.Count > limit ? EncodeCursor(offset + limit) : null,
        });
    }

    public async Task<AZOAResult<OperatorKycSubmissionQueueItem>> DecideAsync(
        Guid submissionId,
        OperatorKycDecisionRequest request,
        Guid operatorUserId,
        CancellationToken ct = default)
    {
        var loaded = await _kycStore.GetSubmissionByIdAsync(submissionId, ct);
        if (loaded.IsError)
            return CopyFailure<KycSubmission, OperatorKycSubmissionQueueItem>(loaded);
        if (loaded.Result is null)
            return NotFound<OperatorKycSubmissionQueueItem>("KYC submission not found.");
        var queueItem = await ToQueueItemAsync(loaded.Result, ct);
        if (queueItem.IsError || queueItem.Result is null)
            return queueItem;
        if (!queueItem.Result.HumanReviewAllowed)
            return OperationNotAllowed<OperatorKycSubmissionQueueItem>("Human review is not allowed for this submission authority.");

        var decision = request.Decision?.Trim().ToLowerInvariant() ?? string.Empty;
        if (decision == "reject" && !IsSafe(request.Reason, 500))
            return Invalid<OperatorKycSubmissionQueueItem>("A bounded rejection reason is required.");
        if (request.Notes is not null && !IsSafe(request.Notes, 2000))
            return Invalid<OperatorKycSubmissionQueueItem>("Review notes are invalid.");

        AZOAResult<KycSubmissionModel> result = decision switch
        {
            "approve" => await _kyc.ApproveAsync(submissionId, operatorUserId, request.Notes, ct),
            "reject" => await _kyc.RejectAsync(submissionId, operatorUserId, request.Notes, request.Reason, ct),
            _ => Invalid<KycSubmissionModel>("Decision must be approve or reject."),
        };
        if (result.IsError || result.Result is null)
            return CopyFailure<KycSubmissionModel, OperatorKycSubmissionQueueItem>(
                result,
                AzoaErrorCodes.OperationNotAllowed);
        var refreshed = await _kycStore.GetSubmissionByIdAsync(submissionId, ct);
        if (refreshed.IsError)
            return CopyFailure<KycSubmission, OperatorKycSubmissionQueueItem>(refreshed);
        if (refreshed.Result is null)
            return DependencyUnavailable<OperatorKycSubmissionQueueItem>(
                "KYC decision was saved but could not be reloaded.");
        return await ToQueueItemAsync(refreshed.Result, ct);
    }

    /// <summary>
    /// The people a human still owes an AML decision: an identity approval that is
    /// APPROVED and unexpired, and an AML verdict that is not clear.
    ///
    /// <para>It is built from the APPROVED submission page rather than from the
    /// screening ledger because that ledger has no "everyone flagged" read, and because
    /// someone whose identity approval expired is closed for a different reason and is
    /// not waiting on this desk.</para>
    /// </summary>
    public async Task<AZOAResult<CursorPage<AmlManualReviewItem>>> ListAmlReviewsAsync(
        int limit,
        string? cursor,
        Guid operatorUserId,
        CancellationToken ct = default)
    {
        if (!IsNodeOperator(operatorUserId))
            return Forbidden<CursorPage<AmlManualReviewItem>>(OperatorOnlyReason);
        if (limit is < 1 or > 100 || !TryDecodeCursor(cursor, out var offset))
            return Invalid<CursorPage<AmlManualReviewItem>>("Invalid pagination request.");
        if (_screening is null)
        {
            // An empty page would read as "nobody is waiting". Nobody is LISTENING.
            return DependencyUnavailable<CursorPage<AmlManualReviewItem>>(
                "The AML screening ledger is unavailable, so the review queue cannot be read.");
        }

        var rows = await _kycStore.GetEffectiveOperatorPageAsync("approved", offset, limit + 1, ct);
        if (rows.IsError || rows.Result is null)
            return CopyFailure<IReadOnlyList<KycSubmission>, CursorPage<AmlManualReviewItem>>(rows);

        var now = DateTimeOffset.UtcNow;
        var sla = KycAmlClearance.ResolveManualReviewSla(_configuration);
        var contact = KycAmlClearance.ResolveManualReviewContact(_configuration);
        var notice = KycAmlClearance.ResolveUnderReviewReason(_configuration);
        var seen = new HashSet<(Guid Subject, Guid? Tenant)>();
        var items = new List<AmlManualReviewItem>();
        foreach (var submission in rows.Result.Take(limit))
        {
            if (EffectiveStatus(submission, now) != KycStatus.APPROVED)
                continue;
            if (!Guid.TryParse(submission.UserId, out var subjectUserId) || subjectUserId == Guid.Empty)
                continue;
            var tenantId = Guid.TryParse(submission.TenantId, out var parsedTenant) && parsedTenant != Guid.Empty
                ? parsedTenant
                : (Guid?)null;
            if (!seen.Add((subjectUserId, tenantId)))
                continue;

            var latest = await _screening.GetLatestResultAsync(subjectUserId, tenantId, ct);
            if (latest.IsError)
            {
                // One unreadable verdict must not silently shrink the queue: a person
                // dropped from this list is a person nobody knows to decide about.
                return CopyFailure<KycScreeningResult, CursorPage<AmlManualReviewItem>>(latest);
            }
            if (KycAmlClearance.IsClear(latest.Result))
                continue;
            items.Add(ToAmlReviewItem(subjectUserId, tenantId, latest.Result, sla, now, contact, notice));
        }

        return AZOAResult<CursorPage<AmlManualReviewItem>>.Success(new CursorPage<AmlManualReviewItem>
        {
            Items = items,
            // The cursor advances by SUBMISSIONS scanned, not by items returned: a page
            // can be short because the people on it were clear and still have a next.
            NextCursor = rows.Result.Count > limit ? EncodeCursor(offset + limit) : null,
        });
    }

    /// <summary>
    /// Records an operator's AML decision as a NEW screening row that supersedes the one
    /// they read, by <c>screened_at</c>. Nothing is edited in place, so the evidence that
    /// was decided over stays readable beside the decision.
    ///
    /// <para>Clearing re-opens both value gates for this person, so it is allowed only
    /// where a screening row exists AND is not already clear. Someone never screened
    /// cannot be cleared by hand: an invented NO_HIT is a clean verdict nobody produced.</para>
    ///
    /// <para>The reason is required and bounded and is written to the operator log.
    /// <c>kyc_screening_result</c> has no column for it, and this manager has no seam
    /// that appends a standalone control-audit row.</para>
    /// </summary>
    public async Task<AZOAResult<AmlManualReviewItem>> DecideAmlReviewAsync(
        Guid subjectUserId,
        AmlManualReviewDecisionRequest request,
        Guid operatorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsNodeOperator(operatorUserId))
            return Forbidden<AmlManualReviewItem>(OperatorOnlyReason);
        if (subjectUserId == Guid.Empty || subjectUserId == NodeOperatorIdentity.UserId)
            return NotFound<AmlManualReviewItem>("No compliance review is open for this person.");
        if (_screening is null)
        {
            return DependencyUnavailable<AmlManualReviewItem>(
                "The AML screening ledger is unavailable, so no decision can be recorded.");
        }

        var decision = request.Decision?.Trim().ToLowerInvariant() ?? string.Empty;
        if (decision is not (AmlManualReviewDecisionRequest.Clear or AmlManualReviewDecisionRequest.Deny))
            return Invalid<AmlManualReviewItem>("A compliance decision must be clear or deny.");
        if (!IsSafe(request.Reason, MaxReviewReasonLength))
            return Invalid<AmlManualReviewItem>("A bounded reason is required with every compliance decision.");
        var expectedScreeningId = request.ExpectedScreeningId?.Trim() ?? string.Empty;
        if (expectedScreeningId.Length is 0 or > MaxScreeningIdLength)
            return Invalid<AmlManualReviewItem>("The screening row being superseded must be named.");

        var tenantId = request.TenantId is { } candidate && candidate != Guid.Empty
            ? candidate
            : (Guid?)null;
        var latest = await _screening.GetLatestResultAsync(subjectUserId, tenantId, ct);
        if (latest.IsError)
            return CopyFailure<KycScreeningResult, AmlManualReviewItem>(latest);
        var current = latest.Result;
        if (current is null)
        {
            return OperationNotAllowed<AmlManualReviewItem>(
                "This person has never been screened, so there is no verdict to supersede. "
                + "Screen them before recording a decision.");
        }
        if (KycAmlClearance.IsClear(current))
            return OperationNotAllowed<AmlManualReviewItem>("No compliance review is open for this person.");
        if (!string.Equals(BareScreeningId(current), expectedScreeningId, StringComparison.Ordinal))
        {
            return Conflict<AmlManualReviewItem>(
                "A newer screening arrived while this review was open. "
                + "Reload the queue and read the current one before deciding.");
        }

        // The superseding row must sort AFTER the row it supersedes or the value gates
        // keep reading the old verdict and the decision is nominal.
        var decidedAt = DateTimeOffset.UtcNow;
        if (decidedAt <= current.ScreenedAt)
            decidedAt = current.ScreenedAt.AddTicks(1);
        var cleared = string.Equals(decision, AmlManualReviewDecisionRequest.Clear, StringComparison.Ordinal);
        var superseding = new KycScreeningResult
        {
            Id = Guid.NewGuid().ToString("N"),
            SubjectUserId = subjectUserId.ToString("N"),
            TenantId = tenantId?.ToString("N"),
            // The query shape is deliberately NOT copied: a decision is not a new search,
            // and duplicating a name and a date of birth spreads retained PII (D25).
            ScoreThreshold = current.ScoreThreshold,
            Decision = cleared ? KycScreeningDecision.NO_HIT : KycScreeningDecision.HIT,
            TopScore = current.TopScore,
            HitCount = current.HitCount,
            Topics = current.Topics,
            PersonIds = current.PersonIds,
            MonitorId = current.MonitorId,
            PolicyVersion = ManualReviewPolicyVersion,
            // A human read the list, however complete it was, and answered for it. The
            // row they answered over keeps its own truncation flag.
            ResultsTruncated = false,
            RetentionExpiresAt = current.RetentionExpiresAt,
            ScreenedAt = decidedAt,
            CreatedDate = decidedAt,
            ModifiedDate = decidedAt,
        };
        var saved = await _screening.SaveResultAsync(superseding, ct);
        if (saved.IsError)
            return CopyFailure<KycScreeningResult, AmlManualReviewItem>(saved);

        _logger?.LogInformation(
            "AML manual review decided by operator {OperatorUserId}: subject {SubjectUserId}, tenant {TenantId}, "
            + "decision {Decision}, superseded screening {SupersededScreeningId} with {SupersedingScreeningId}. "
            + "Reason: {Reason}",
            operatorUserId,
            subjectUserId,
            tenantId,
            cleared ? AmlManualReviewDecisionRequest.Clear : AmlManualReviewDecisionRequest.Deny,
            expectedScreeningId,
            superseding.Id,
            request.Reason.Trim());

        var refreshed = await _screening.GetLatestResultAsync(subjectUserId, tenantId, ct);
        if (refreshed.IsError)
            return CopyFailure<KycScreeningResult, AmlManualReviewItem>(refreshed);
        if (refreshed.Result is null)
        {
            return DependencyUnavailable<AmlManualReviewItem>(
                "The compliance decision was saved but could not be reloaded.");
        }
        return AZOAResult<AmlManualReviewItem>.Success(ToAmlReviewItem(
            subjectUserId,
            tenantId,
            refreshed.Result,
            KycAmlClearance.ResolveManualReviewSla(_configuration),
            DateTimeOffset.UtcNow,
            KycAmlClearance.ResolveManualReviewContact(_configuration),
            KycAmlClearance.ResolveUnderReviewReason(_configuration)));
    }

    public async Task<AZOAResult<NodeOperatorOverviewResponse>> GetOverviewAsync(CancellationToken ct = default)
    {
        var profiles = await _registry.ListProfilesAsync(ct);
        var pending = await _kycStore.CountEffectiveStatusAsync("pending", ct);
        var selections = await _control.CountSelectionsAsync(ct);
        var state = await _operatorState.GetAsync(ct);
        var user = await _users.GetByIdAsync(NodeOperatorIdentity.UserId, ct);
        var persistenceReady = !profiles.IsError && !pending.IsError && !selections.IsError
            && !state.IsError && !user.IsError && user.Result is not null;
        return AZOAResult<NodeOperatorOverviewResponse>.Success(new NodeOperatorOverviewResponse
        {
            Node = new NodeRuntimeSummary
            {
                Environment = _environment.EnvironmentName,
                ServiceVersion = typeof(KycControlPlaneManager).Assembly.GetName().Version?.ToString() ?? "unknown",
                GeneratedAt = DateTimeOffset.UtcNow,
                PersistenceReady = persistenceReady,
            },
            Operator = new NodeOperatorIdentitySummary
            {
                Username = user.Result?.Username ?? string.Empty,
                CredentialRevision = state.Result?.CredentialRevision ?? 0,
                ActivatedAt = state.Result?.ActivatedAt ?? default,
                CredentialUpdatedAt = state.Result?.CredentialUpdatedAt ?? state.Result?.ActivatedAt ?? default,
            },
            Kyc = new KycControlSummary
            {
                ProfileCount = profiles.Result?.Count ?? 0,
                EnabledProfileCount = profiles.Result?.Count(profile => profile.Enabled) ?? 0,
                ReadyProfileCount = profiles.Result?.Count(profile => profile.ReadinessCode == KycProviderReadinessCodes.Ready) ?? 0,
                PendingSubmissionCount = checked((int)Math.Min(pending.Result, int.MaxValue)),
                ConfiguredTenantCount = checked((int)Math.Min(selections.Result, int.MaxValue)),
            },
        });
    }

    public async Task<AZOAResult<CursorPage<KycControlAuditResponse>>> ListAuditAsync(
        int limit,
        string? cursor,
        Guid? tenantId,
        string? providerKey,
        string? action,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > 100
            || !TryDecodeAuditCursor(cursor, out var before)
            || tenantId == Guid.Empty
            || tenantId == NodeOperatorIdentity.UserId)
        {
            return Invalid<CursorPage<KycControlAuditResponse>>("Invalid audit pagination or tenant filter.");
        }

        string? normalizedProvider = null;
        if (!string.IsNullOrWhiteSpace(providerKey))
        {
            normalizedProvider = KycProviderRegistry.NormalizeAdapter(providerKey);
            if (normalizedProvider is null)
                return Invalid<CursorPage<KycControlAuditResponse>>("Unsupported audit provider filter.");
        }
        var normalizedAction = action?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedAction) && !AuditActions.Contains(normalizedAction))
            return Invalid<CursorPage<KycControlAuditResponse>>("Unsupported audit action filter.");

        var page = await _control.ListAuditPageAsync(
            limit + 1,
            before,
            tenantId,
            normalizedProvider,
            normalizedAction,
            ct);
        if (page.IsError || page.Result is null)
            return CopyFailure<IReadOnlyList<KycControlAudit>, CursorPage<KycControlAuditResponse>>(page);

        var rows = page.Result.Take(limit).ToList();
        return AZOAResult<CursorPage<KycControlAuditResponse>>.Success(
            new CursorPage<KycControlAuditResponse>
            {
                Items = rows.Select(ToAuditResponse).ToList(),
                NextCursor = page.Result.Count > limit
                    ? EncodeAuditCursor(rows[^1])
                    : null,
            });
    }

    private async Task<AZOAResult<bool>> HasCurrentTenantAuthorityAsync(Guid tenantId, CancellationToken ct)
    {
        if (tenantId == Guid.Empty || tenantId == NodeOperatorIdentity.UserId)
            return AZOAResult<bool>.Success(false);
        var user = await _users.GetByIdAsync(tenantId, ct);
        if (user.IsError)
            return string.Equals(user.Code, AzoaErrorCodes.NotFound, StringComparison.Ordinal)
                ? AZOAResult<bool>.Success(false)
                : DependencyUnavailable<bool>();
        if (user.Result is null || !user.Result.IsActive)
            return AZOAResult<bool>.Success(false);
        IReadOnlyList<AZOA.WebAPI.Models.ApiKey> keys;
        try
        {
            keys = await _apiKeys.ListByUserAsync(tenantId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DependencyUnavailable<bool>();
        }
        var now = DateTime.UtcNow;
        return AZOAResult<bool>.Success(keys.Any(key => key.IsActive
            && key.RevokedAt is null
            && (key.ExpiresAt is null || key.ExpiresAt > now)
            && (key.Scopes ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(AzoaScopes.TenantProvision, StringComparer.Ordinal)));
    }

    private async Task<AZOAResult<TenantKycSelectionResponse>> ToSelectionResponseAsync(
        Guid tenantId,
        TenantKycProviderSelection? selection,
        CancellationToken ct)
    {
        if (selection is null)
            return AZOAResult<TenantKycSelectionResponse>.Success(new TenantKycSelectionResponse
            {
                TenantId = tenantId.ToString("D"),
                ReadinessCode = KycProviderReadinessCodes.SelectionRequired,
            });
        var profile = await _registry.EvaluateProfileAsync(selection.ProviderKey, ct);
        if (profile.IsError
            && string.Equals(profile.Code, AzoaErrorCodes.DependencyUnavailable, StringComparison.Ordinal))
        {
            return CopyFailure<KycProviderProfileResponse, TenantKycSelectionResponse>(profile);
        }
        return AZOAResult<TenantKycSelectionResponse>.Success(new TenantKycSelectionResponse
        {
            TenantId = tenantId.ToString("D"),
            ProviderKey = selection.ProviderKey,
            ProviderDisplayName = profile.Result?.DisplayName,
            SelectionVersion = selection.SelectionVersion,
            ProviderEnabled = profile.Result?.Enabled == true,
            ProviderAvailable = profile.Result?.Available == true,
            ReadinessCode = profile.Result?.ReadinessCode ?? KycProviderReadinessCodes.ProfileNotConfigured,
            UpdatedAt = selection.UpdatedAt,
        });
    }

    private async Task<AZOAResult<OperatorKycSubmissionQueueItem>> ToQueueItemAsync(
        KycSubmission submission,
        CancellationToken ct)
    {
        var current = await IsCurrentAuthorityAsync(submission, ct);
        if (current.IsError)
            return CopyFailure<bool, OperatorKycSubmissionQueueItem>(current);
        var now = DateTimeOffset.UtcNow;
        var humanReview = current.Result
            && KycRuntimeSafety.IsManualSimulationAllowed(_environment, _configuration)
            && submission.Provider == KycProvider.MANUAL
            && submission.Status is KycStatus.PENDING or KycStatus.IN_REVIEW
            && submission.ExpiresAt is { } expiresAt
            && expiresAt > now;
        var effectiveStatus = EffectiveStatus(submission, now);
        var status = !current.Result
            && effectiveStatus is KycStatus.PENDING or KycStatus.IN_REVIEW or KycStatus.APPROVED
                ? "STALE"
                : effectiveStatus.ToString();
        return AZOAResult<OperatorKycSubmissionQueueItem>.Success(new OperatorKycSubmissionQueueItem
        {
            Id = Guid.TryParse(submission.Id, out var id) ? id : Guid.Empty,
            UserId = Guid.TryParse(submission.UserId, out var userId) ? userId : Guid.Empty,
            TenantId = Guid.TryParse(submission.TenantId, out var tenantId) ? tenantId : null,
            ProviderKey = submission.ProviderKey,
            Status = status,
            HumanReviewAllowed = humanReview,
            ReviewMode = submission.Provider == KycProvider.MANUAL
                ? "development_simulation"
                : "external_provider",
            SubmittedAt = submission.SubmittedAt,
            ExpiresAt = submission.ExpiresAt ?? default,
        });
    }

    private async Task<AZOAResult<bool>> IsCurrentAuthorityAsync(
        KycSubmission submission,
        CancellationToken ct)
    {
        AZOAResult<KycProviderResolution> resolution;
        if (Guid.TryParse(submission.TenantId, out var tenantId))
            resolution = await _registry.ResolveTenantAsync(tenantId, ct);
        else if (!string.IsNullOrWhiteSpace(submission.TenantId))
            return AZOAResult<bool>.Success(false);
        else
            resolution = _registry.ResolveNodeDefault();
        if (resolution.IsError)
            return string.Equals(resolution.Code, AzoaErrorCodes.DependencyUnavailable, StringComparison.Ordinal)
                ? CopyFailure<KycProviderResolution, bool>(resolution)
                : AZOAResult<bool>.Success(false);
        if (resolution.Result is null)
            return AZOAResult<bool>.Success(false);
        return AZOAResult<bool>.Success(string.Equals(
                submission.ProviderKey,
                resolution.Result.Provider.ProviderKey,
                StringComparison.Ordinal)
            && submission.ProviderSelectionVersion == resolution.Result.SelectionVersion
            && submission.ProviderTrustRevision == resolution.Result.TrustRevision
            && KycApprovalTrust.TryResolveCurrentProfile(
                resolution.Result.Provider,
                resolution.Result.Settings,
                _environment,
                out _,
                out var profile,
                out _)
            && KycApprovalTrust.MatchesCurrentAttempt(submission, profile, out _));
    }

    private static KycStatus EffectiveStatus(KycSubmission submission, DateTimeOffset now)
        => (submission.Status is KycStatus.PENDING or KycStatus.IN_REVIEW or KycStatus.APPROVED)
            && (submission.ExpiresAt is null || submission.ExpiresAt <= now)
                ? KycStatus.EXPIRED
                : submission.Status;

    /// <summary>
    /// The AML review desk is node-operator-only, and this is where that is decided.
    /// Both methods hang off an interface a TENANT controller also injects
    /// (<c>TenantKycConfigurationController</c>), so a route-level policy alone would
    /// leave the next caller inheriting nothing.
    /// </summary>
    private static bool IsNodeOperator(Guid actorUserId)
        => actorUserId != Guid.Empty && actorUserId == NodeOperatorIdentity.UserId;

    /// <summary>Bare record id of a screening row; the token a decision must name to supersede it.</summary>
    private static string BareScreeningId(KycScreeningResult result)
        => string.IsNullOrWhiteSpace(result.Id) ? string.Empty : SurrealRecordGuid.BareId(result.Id);

    /// <summary>Projects one subject's current AML standing for the operator review desk.</summary>
    private static AmlManualReviewItem ToAmlReviewItem(
        Guid subjectUserId,
        Guid? tenantId,
        KycScreeningResult? result,
        TimeSpan sla,
        DateTimeOffset now,
        string? contact,
        string notice)
    {
        var reviewDueAt = KycAmlClearance.ReviewDueAt(result, sla);
        return new AmlManualReviewItem
        {
            UserId = subjectUserId,
            TenantId = tenantId,
            ScreeningId = result is null ? null : BareScreeningId(result),
            Decision = result is null ? AmlManualReviewItem.NotScreened : result.Decision.ToString(),
            TopScore = result?.TopScore ?? 0,
            HitCount = result?.HitCount ?? 0,
            Topics = result?.Topics ?? Array.Empty<string>(),
            ResultsTruncated = result?.ResultsTruncated ?? false,
            ScreenedAt = result?.ScreenedAt,
            ReviewDueAt = reviewDueAt,
            Overdue = KycAmlClearance.IsOverdue(reviewDueAt, now),
            PolicyVersion = result?.PolicyVersion ?? string.Empty,
            // A decision needs a row to supersede AND a question still open. This is the
            // same pair DecideAmlReviewAsync enforces; the flag only mirrors it.
            DecisionAllowed = result is not null && KycAmlClearance.RequiresManualReview(result),
            ReviewContact = contact,
            SubjectNotice = notice,
        };
    }

    private static KycControlAuditResponse ToAuditResponse(KycControlAudit audit) => new()
    {
        Id = Guid.TryParse(audit.Id, out var id) ? id : Guid.Empty,
        Action = audit.Action,
        TenantId = Guid.TryParse(audit.TenantId, out var tenantId) ? tenantId : null,
        ProviderKey = audit.ProviderKey,
        PreviousProviderKey = audit.PreviousProviderKey,
        Version = audit.Version,
        PreviousDisplayName = audit.PreviousDisplayName,
        DisplayName = audit.DisplayName,
        PreviousAdapterKey = audit.PreviousAdapterKey,
        AdapterKey = audit.AdapterKey,
        PreviousEnabled = audit.PreviousEnabled,
        Enabled = audit.Enabled,
        PreviousPolicyVersion = audit.PreviousPolicyVersion,
        PolicyVersion = audit.PolicyVersion,
        PreviousAssuranceLevel = audit.PreviousAssuranceLevel,
        AssuranceLevel = audit.AssuranceLevel,
        PreviousTrustRevision = audit.PreviousTrustRevision,
        TrustRevision = audit.TrustRevision,
        ActorUserId = Guid.TryParse(audit.ActorUserId, out var actorId) ? actorId : Guid.Empty,
        OccurredAt = audit.OccurredAt,
    };

    private static bool IsSafe(string? value, int maximum)
        => !string.IsNullOrWhiteSpace(value)
            && value.Trim().Length <= maximum
            && value.All(character => !char.IsControl(character) || character is '\r' or '\n' or '\t');

    private static AZOAResult<T> Invalid<T>(string message)
        => AZOAResult<T>.FailureWithCode(message, AzoaErrorCodes.InvalidRequest);

    private static AZOAResult<T> DependencyUnavailable<T>(
        string message = "KYC control plane is temporarily unavailable. Try again later.")
        => AZOAResult<T>.FailureWithCode(message, AzoaErrorCodes.DependencyUnavailable);

    private static AZOAResult<T> NotFound<T>(string message)
        => AZOAResult<T>.FailureWithCode(message, AzoaErrorCodes.NotFound);

    private static AZOAResult<T> Conflict<T>(string message)
        => AZOAResult<T>.FailureWithCode(message, AzoaErrorCodes.Conflict);

    private static AZOAResult<T> Forbidden<T>(string message)
        => AZOAResult<T>.FailureWithCode(message, AzoaErrorCodes.Forbidden);

    private static AZOAResult<T> PolicyUnavailable<T>(string message)
        => AZOAResult<T>.FailureWithCode(message, AzoaErrorCodes.PolicyUnavailable);

    private static AZOAResult<T> OperationNotAllowed<T>(string message)
        => AZOAResult<T>.FailureWithCode(message, AzoaErrorCodes.OperationNotAllowed);

    private static AZOAResult<TOut> CopyFailure<TIn, TOut>(
        AZOAResult<TIn> source,
        string fallbackCode = AzoaErrorCodes.DependencyUnavailable)
        => AZOAResult<TOut>.FailureWithCode(
            source.Message,
            source.Code ?? fallbackCode);

    private static string EncodeCursor(int offset)
        => Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
            TextEncoding.UTF8.GetBytes($"v1:{offset}"));

    private static bool TryDecodeCursor(string? cursor, out int offset)
    {
        offset = 0;
        if (string.IsNullOrWhiteSpace(cursor))
            return true;
        try
        {
            var value = TextEncoding.UTF8.GetString(
                Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(cursor));
            return value.StartsWith("v1:", StringComparison.Ordinal)
                && int.TryParse(value.AsSpan(3), out offset)
                && offset >= 0
                && offset <= 1_000_000;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string EncodeAuditCursor(KycControlAudit audit)
    {
        var recordId = SurrealRecordGuid.BareId(audit.Id);
        var ticks = audit.OccurredAt.UtcDateTime.Ticks.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        return Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
            TextEncoding.UTF8.GetBytes($"v2:{ticks}:{recordId}"));
    }

    private static bool TryDecodeAuditCursor(
        string? cursor,
        out KycControlAuditCursor? before)
    {
        before = null;
        if (string.IsNullOrWhiteSpace(cursor))
            return true;
        if (cursor.Length > MaxAuditCursorLength)
            return false;

        try
        {
            var value = TextEncoding.UTF8.GetString(
                Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(cursor));
            var parts = value.Split(':', 3, StringSplitOptions.None);
            if (parts.Length != 3
                || !string.Equals(parts[0], "v2", StringComparison.Ordinal)
                || !long.TryParse(
                    parts[1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var ticks)
                || !Guid.TryParseExact(parts[2], "N", out var recordId)
                || recordId == Guid.Empty)
            {
                return false;
            }

            before = new KycControlAuditCursor(
                new DateTimeOffset(ticks, TimeSpan.Zero),
                recordId.ToString("N"));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

/// <summary>
/// D48 node-config gate for the per-tenant KYC provider override
/// (<c>PUT /api/tenant/kyc/provider</c>). OFF for every tenant by default.
/// </summary>
/// <remarks>
/// The provider a tenant verifies through IS that tenant's compliance policy, so
/// letting a tenant repoint it at will is tenant-chosen policy on a node that
/// custodies someone else's real users. Both keys must agree before a tenant may
/// choose: the node-wide switch AND that tenant's id on the allow-list. Operator
/// routes are unaffected - the operator is the custodian. See
/// <c>docs/NODE-HOST.md</c> section 8.5.
/// </remarks>
public static class TenantKycProviderOverridePolicy
{
    /// <summary>Node-wide master switch; absent or false disables every tenant override.</summary>
    public const string EnabledKey = "Kyc:TenantProviderOverride:Enabled";

    /// <summary>Array of tenant ids the operator has admitted to the override.</summary>
    public const string AllowedTenantIdsKey = "Kyc:TenantProviderOverride:AllowedTenantIds";

    /// <summary>Wildcard entry admitting every tenant; deliberately explicit and auditable.</summary>
    public const string AllowAllTenantsEntry = "*";

    /// <summary>Machine-readable refusal surfaced to the tenant.</summary>
    public const string ForbiddenReason = "TENANT_PROVIDER_OVERRIDE_NOT_ENABLED";

    /// <summary>True only when the node switch is on AND this tenant is allow-listed.</summary>
    public static bool IsAllowedForTenant(IConfiguration? configuration, Guid tenantId)
    {
        if (configuration is null || tenantId == Guid.Empty)
            return false;
        // Parsed rather than bound: an unparseable switch reads as OFF, never as ON.
        if (!bool.TryParse(configuration[EnabledKey], out var enabled) || !enabled)
            return false;

        var allowed = false;
        foreach (var entry in configuration.GetSection(AllowedTenantIdsKey).GetChildren())
        {
            var value = entry.Value?.Trim();
            if (string.IsNullOrEmpty(value))
                continue;
            if (string.Equals(value, AllowAllTenantsEntry, StringComparison.Ordinal))
                return true;
            if (Guid.TryParse(value, out var parsed) && parsed == tenantId)
                allowed = true;
        }

        return allowed;
    }
}
