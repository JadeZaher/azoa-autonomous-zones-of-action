// SPDX-License-Identifier: UNLICENSED

using SurrealForge.Client;
using AZOA.WebAPI.Models.Signing;
using SurrealForge.Client.Query;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Core.Surreal;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc/>
public sealed partial class SurrealKycStore : IKycStore, IKycCustodialAdmissionStore
{
    private const string PublicStoreError =
        "KYC_STORE_UNAVAILABLE: KYC persistence is temporarily unavailable.";
    private readonly ISurrealExecutor _executor;

    public SurrealKycStore(
        ISurrealExecutor executor,
        ILogger<SurrealKycStore>? logger = null)
    {
        _executor = executor;
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<KycSubmission>> GetSubmissionByIdAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _executor.QuerySingleAsync<KycSubmission>(SurrealQuery<KycSubmission>.Key(id.ToString("N")), ct);
        return AZOAResult<KycSubmission>.Success(row is null ? null! : FromStorage(row));
    }

    /// <inheritdoc/>
    public Task<AZOAResult<KycSubmission>> GetLatestSubmissionByUserAsync(Guid userId, CancellationToken ct = default)
        => GetLatestSubmissionAsync(userId, null, ct);

    /// <inheritdoc/>
    public async Task<AZOAResult<KycSubmission>> GetLatestSubmissionAsync(Guid userId, Guid? tenantId, CancellationToken ct = default)
    {
        var user = SurrealLink.ToLink("user", userId.ToString("N"));
        var tenant = tenantId is null ? null : SurrealLink.ToLink("user", tenantId.Value.ToString("N"));
        var row = await Submissions().Where(item => item.UserId == user && item.TenantId == tenant)
            .OrderByDescending(item => item.SubmittedAt).FirstOrDefaultAsync(ct);
        return AZOAResult<KycSubmission>.Success(row is null ? null! : FromStorage(row));
    }

    /// <inheritdoc/>
    public Task<AZOAResult<KycSubmission>> GetActiveSubmissionByUserAsync(Guid userId, CancellationToken ct = default)
        => GetActiveSubmissionAsync(userId, null, ct);

    /// <inheritdoc/>
    public async Task<AZOAResult<KycSubmission>> GetActiveSubmissionAsync(Guid userId, Guid? tenantId, CancellationToken ct = default)
    {
        var user = SurrealLink.ToLink("user", userId.ToString("N"));
        var tenant = tenantId is null ? null : SurrealLink.ToLink("user", tenantId.Value.ToString("N"));
        var row = await Submissions().Where(item => item.UserId == user && item.TenantId == tenant
            && (item.Status == KycStatus.PENDING || item.Status == KycStatus.IN_REVIEW))
            .OrderByDescending(item => item.SubmittedAt).FirstOrDefaultAsync(ct);
        return AZOAResult<KycSubmission>.Success(row is null ? null! : FromStorage(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<KycSubmission>>> GetPendingAsync(CancellationToken ct = default)
    {
        var rows = await Submissions().Where(item => item.Status == KycStatus.PENDING || item.Status == KycStatus.IN_REVIEW)
            .OrderBy(item => item.SubmittedAt).ToListAsync(ct);
        return AZOAResult<IEnumerable<KycSubmission>>.Success(rows.Select(FromStorage).ToList());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IReadOnlyList<KycSubmission>>> GetPendingPageAsync(int offset, int limit, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await Submissions().Where(item => (item.Status == KycStatus.PENDING || item.Status == KycStatus.IN_REVIEW)
            && item.ExpiresAt != null && item.ExpiresAt > now).OrderBy(item => item.SubmittedAt).ThenBy(item => item.Id)
            .Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 101)).ToListAsync(ct);
        return AZOAResult<IReadOnlyList<KycSubmission>>.Success(rows.Select(FromStorage).ToList());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IReadOnlyList<KycSubmission>>> GetOperatorPageAsync(
        IReadOnlyCollection<KycStatus> statuses, int offset, int limit, CancellationToken ct = default)
    {
        if (statuses.Count == 0) return AZOAResult<IReadOnlyList<KycSubmission>>.Success([]);
        var rows = await Submissions().Where(item => statuses.Contains(item.Status))
            .OrderByDescending(item => item.SubmittedAt).ThenBy(item => item.Id)
            .Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 101)).ToListAsync(ct);
        return AZOAResult<IReadOnlyList<KycSubmission>>.Success(rows.Select(FromStorage).ToList());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<IReadOnlyList<KycSubmission>>> GetEffectiveOperatorPageAsync(
        string effectiveStatus, int offset, int limit, CancellationToken ct = default)
    {
        var query = EffectiveSubmissions(effectiveStatus);
        if (query is null) return AZOAResult<IReadOnlyList<KycSubmission>>.Failure("Unsupported KYC status filter.");
        var rows = await query.OrderByDescending(item => item.SubmittedAt).ThenBy(item => item.Id)
            .Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 101)).ToListAsync(ct);
        return AZOAResult<IReadOnlyList<KycSubmission>>.Success(rows.Select(FromStorage).ToList());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<long>> CountByStatusesAsync(IReadOnlyCollection<KycStatus> statuses, CancellationToken ct = default)
        => AZOAResult<long>.Success(statuses.Count == 0 ? 0 : await Submissions().Where(item => statuses.Contains(item.Status)).CountAsync(ct));

    /// <inheritdoc/>
    public async Task<AZOAResult<long>> CountEffectiveStatusAsync(string effectiveStatus, CancellationToken ct = default)
    {
        var query = EffectiveSubmissions(effectiveStatus);
        return query is null ? AZOAResult<long>.Failure("Unsupported KYC status filter.")
            : AZOAResult<long>.Success(await query.CountAsync(ct));
    }

    private IQueryable<KycSubmission> Submissions() => new SurrealQueryable<KycSubmission>(new SurrealQueryProvider(_executor));

    private IQueryable<KycSubmission>? EffectiveSubmissions(string value)
    {
        var now = DateTimeOffset.UtcNow;
        return value.Trim().ToLowerInvariant() switch
        {
            "pending" => Submissions().Where(item => (item.Status == KycStatus.PENDING || item.Status == KycStatus.IN_REVIEW)
                && item.ExpiresAt != null && item.ExpiresAt > now),
            "approved" => Submissions().Where(item => item.Status == KycStatus.APPROVED && item.ExpiresAt != null && item.ExpiresAt > now),
            "rejected" => Submissions().Where(item => item.Status == KycStatus.REJECTED || item.Status == KycStatus.EXPIRED
                || ((item.Status == KycStatus.PENDING || item.Status == KycStatus.IN_REVIEW || item.Status == KycStatus.APPROVED)
                    && (item.ExpiresAt == null || item.ExpiresAt <= now))),
            _ => null,
        };
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<KycSubmission>> UpsertSubmissionAsync(KycSubmission submission, CancellationToken ct = default)
    {
        var response = await KycEvidenceFence.ExecuteAsync(_executor, KycEvidenceFence.Mutation(submission.UserId, submission.TenantId,
            SurrealWriter.Upsert(ToStorage(submission))), ct);
        response.EnsureAllOk();
        return AZOAResult<KycSubmission>.Success(FromStorage(response.GetValues<KycSubmission>(2).Single()));
    }

    /// <inheritdoc/>
    public Task<AZOAResult<KycSubmission>> TryRetireAttemptAsync(KycSubmission expected, DateTimeOffset now, CancellationToken ct = default)
        => RetireAttemptCoreAsync(expected, now, null, ct);

    private async Task<AZOAResult<KycSubmission>> RetireAttemptCoreAsync(KycSubmission expected, DateTimeOffset now,
        TenantCustodialWitness? custody, CancellationToken ct)
    {
        var query = SurrealWriter.UpdateOnly<KycSubmission>(expected.Id)
            .Where(row => row.Status == expected.Status && row.ModifiedDate == expected.ModifiedDate)
            .Where(row => row.Status == KycStatus.PENDING || row.Status == KycStatus.IN_REVIEW)
            .Set(row => row.Status, KycStatus.EXPIRED).Set(row => row.ModifiedDate, (DateTimeOffset?)now).Build();
        var guards = CustodyGuards(expected, custody);
        if (guards is null) return CustodyRefusal();
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(guards);
        statements.Add(KycEvidenceFence.Create(expected.UserId, expected.TenantId));
        statements.Add(query);
        statements.Add(SurrealQuery.Of("COMMIT"));
        var response = await KycEvidenceFence.ExecuteAsync(_executor, SurrealQuery.Combine(statements.ToArray()), ct);
        var row = response.GetValues<KycSubmission>(statements.Count - 2).SingleOrDefault();
        return AZOAResult<KycSubmission>.Success(row is null ? null! : FromStorage(row));
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<KycSubmission>> TryAttachHostedRedirectAsync(KycSubmission expected, string redirectUrl, DateTimeOffset? expiresAt, CancellationToken ct = default)
    {
        var mutation = SurrealWriter.UpdateOnly<KycSubmission>(expected.Id)
            .Where(row => row.ProviderSessionId == expected.ProviderSessionId)
            .Where(row => row.Status == KycStatus.PENDING || row.Status == KycStatus.IN_REVIEW)
            .Set(row => row.VerificationUrl, redirectUrl)
            .Set(row => row.ModifiedDate, (DateTimeOffset?)DateTimeOffset.UtcNow);
        if (expiresAt is not null && expiresAt < expected.ExpiresAt)
            mutation = mutation.Set(row => row.ExpiresAt, expiresAt);
        var response = await KycEvidenceFence.ExecuteAsync(_executor, KycEvidenceFence.Mutation(expected.UserId, expected.TenantId, mutation.Build()), ct);
        response.EnsureAllOk();
        var row = response.GetValues<KycSubmission>(2).SingleOrDefault();
        return AZOAResult<KycSubmission>.Success(row is null ? null! : FromStorage(row));
    }

    /// <inheritdoc/>
    public Task<AZOAResult<KycSubmission>> CreateSubmissionAsync(
        KycSubmission submission, IReadOnlyList<KycDocument> documents, CancellationToken ct = default)
        => CreateSubmissionCoreAsync(submission, documents, null, ct);

    private async Task<AZOAResult<KycSubmission>> CreateSubmissionCoreAsync(KycSubmission submission,
        IReadOnlyList<KycDocument> documents, TenantCustodialWitness? custody, CancellationToken ct)
    {
        var custodyGuards = CustodyGuards(submission, custody);
        if (custodyGuards is null) return CustodyRefusal();
        try
        {
            var storedSubmission = ToStorage(submission);
            var storedDocuments = documents.Select(ToStorage).ToList();
            var hasTenant = !string.IsNullOrWhiteSpace(submission.TenantId);
            var tenantId = hasTenant ? submission.TenantId : string.Empty;
            var authorityGuardTable = "kyc_authority_guard";
            var loadSelection = SurrealQuery
                .Of("LET $_selection = (SELECT provider_key, selection_version FROM (IF $_has_tenant { [type::record($_selection_table, $_tenant_id)] } ELSE { [] }))")
                .WithParam("_selection_table", TenantKycProviderSelection.SchemaNameConst)
                .WithParam("_tenant_id", tenantId)
                .WithParam("_has_tenant", hasTenant);
            var assertSelection = SurrealQuery
                .Of("IF $_has_tenant AND (array::len($_selection) != 1 OR $_selection[0].provider_key != $_provider_key OR $_selection[0].selection_version != $_selection_version) { THROW 'KYC tenant authority changed' }")
                .WithParam("_has_tenant", hasTenant)
                .WithParam("_provider_key", submission.ProviderKey)
                .WithParam("_selection_version", submission.ProviderSelectionVersion);
            var loadProfile = SurrealQuery
                .Of("LET $_profile = (SELECT enabled, trust_revision FROM type::record($_profile_table, $_provider_key) WHERE $_has_tenant)")
                .WithParam("_profile_table", KycProviderProfile.SchemaNameConst)
                .WithParam("_provider_key", submission.ProviderKey)
                .WithParam("_has_tenant", hasTenant);
            var assertProfile = SurrealQuery
                .Of("IF $_has_tenant AND (array::len($_profile) != 1 OR !$_profile[0].enabled OR $_profile[0].trust_revision != $_trust_revision) { THROW 'KYC provider authority changed' }")
                .WithParam("_has_tenant", hasTenant)
                .WithParam("_trust_revision", submission.ProviderTrustRevision);
            var writeTenantGuard = SurrealQuery
                .Of("IF $_has_tenant { UPSERT type::record($_guard_table, $_tenant_guard_id) CONTENT { kind: 'tenant', authority_revision: $_tenant_revision, provider_key: $_provider, touched_at: time::now() } RETURN NONE }")
                .WithParam("_has_tenant", hasTenant)
                .WithParam("_guard_table", authorityGuardTable)
                .WithParam("_tenant_guard_id", $"tenant-{tenantId}")
                .WithParam("_tenant_revision", submission.ProviderSelectionVersion)
                .WithParam("_provider", submission.ProviderKey);
            var writeProviderGuard = SurrealQuery
                .Of("IF $_has_tenant { UPSERT type::record($_guard_table, $_provider_guard_id) CONTENT { kind: 'provider', authority_revision: $_provider_revision, provider_key: $_provider, touched_at: time::now() } RETURN NONE }")
                .WithParam("_has_tenant", hasTenant)
                .WithParam("_guard_table", authorityGuardTable)
                .WithParam("_provider_guard_id", $"provider-{submission.ProviderKey}")
                .WithParam("_provider_revision", submission.ProviderTrustRevision)
                .WithParam("_provider", submission.ProviderKey);
            var writeAttemptGuard = KycEvidenceFence.Create(submission.UserId, submission.TenantId);
            var findActive = SurrealQuery
                .Of("LET $_active = (SELECT id FROM type::table($_submission_table) WHERE user_id = $_user AND (($_has_tenant AND tenant_id = $_tenant) OR (!$_has_tenant AND tenant_id = NONE)) AND status INSIDE $_active_statuses LIMIT 1)")
                .WithParam("_submission_table", KycSubmission.SchemaNameConst)
                .WithParam("_user", SurrealRecordParam.OfLink(storedSubmission.UserId))
                .WithParam("_has_tenant", !string.IsNullOrWhiteSpace(storedSubmission.TenantId))
                .WithParam("_tenant", SurrealRecordParam.OfLink(storedSubmission.TenantId))
                .WithParam("_active_statuses", new[] { nameof(KycStatus.PENDING), nameof(KycStatus.IN_REVIEW) });
            var rejectActive = SurrealQuery.Of(
                "IF array::len($_active) > 0 { THROW 'An active KYC submission already exists' } ELSE { NONE }");
            // raw: active-attempt admission and scoped document creates share one authority transaction.
            var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
            statements.AddRange(custodyGuards);
            statements.AddRange([loadSelection, assertSelection, loadProfile, assertProfile, writeTenantGuard,
                writeProviderGuard, writeAttemptGuard, findActive, rejectActive]);
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(storedSubmission), "kyc_create"));
            for (var index = 0; index < storedDocuments.Count; index++)
                statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(storedDocuments[index]), "kyc_doc" + index));
            statements.Add(SurrealQuery.Of("COMMIT"));
            var transaction = SurrealQuery.Combine(statements.ToArray());

            var response = await KycEvidenceFence.ExecuteAsync(_executor, transaction, ct);
            response.EnsureAllOk();
            return new AZOAResult<KycSubmission> { Result = submission, Message = "Created." };
        }
        catch (SurrealStatementException exception) when (
            exception.Message.Contains("An active KYC submission already exists", StringComparison.Ordinal)
            || exception.Message.Contains("KYC tenant authority changed", StringComparison.Ordinal)
            || exception.Message.Contains("KYC provider authority changed", StringComparison.Ordinal))
        {
            return AZOAResult<KycSubmission>.FailureWithCode("The KYC attempt or authority changed.", AzoaErrorCodes.Conflict);
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<KycSubmission>> TryCompleteHostedAsync(
        KycSubmission expected,
        HostedSession lease,
        KycStatus status,
        DateTimeOffset reviewedAt,
        DateTimeOffset? expiresAt,
        string? rejectionReason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(lease.ResumeLeaseToken)
            || lease.Id != expected.ProviderSessionId
            || expected.Provider is not (KycProvider.IDENTOMAT or KycProvider.VERIFF or KycProvider.GENERIC_HOSTED)
            || expected.Status is not (KycStatus.PENDING or KycStatus.IN_REVIEW)
            || status is not (KycStatus.APPROVED or KycStatus.REJECTED)
            || string.IsNullOrWhiteSpace(expected.ProviderSessionId)
            || expected.ExpiresAt is null || expected.ExpiresAt <= reviewedAt)
            return AZOAResult<KycSubmission>.Success(null!);

        var storedExpected = ToStorage(expected);
        var query = SurrealWriter.UpdateOnly<KycSubmission>(expected.Id)
            .Where(row => row.UserId == storedExpected.UserId)
            .Where(row => row.Provider == expected.Provider && row.ProviderKey == expected.ProviderKey)
            .Where(row => row.ProviderSessionId == expected.ProviderSessionId)
            .Where(row => row.ProviderSelectionVersion == expected.ProviderSelectionVersion
                && row.ProviderTrustRevision == expected.ProviderTrustRevision)
            .Where(row => row.Status == expected.Status)
            .Where(row => row.ExpiresAt == expected.ExpiresAt && row.ExpiresAt > reviewedAt)
            .Set(row => row.Status, status)
            .Set(row => row.ReviewedAt, (DateTimeOffset?)reviewedAt)
            .Set(row => row.ModifiedDate, (DateTimeOffset?)reviewedAt);
        query = storedExpected.TenantId is null ? query.Where(row => row.TenantId == null) : query.Where(row => row.TenantId == storedExpected.TenantId);
        query = expected.ProviderResult is null ? query.Where(row => row.ProviderResult == null) : query.Where(row => row.ProviderResult == expected.ProviderResult);
        query = expected.ModifiedDate is null ? query.Where(row => row.ModifiedDate == null) : query.Where(row => row.ModifiedDate == expected.ModifiedDate);
        query = expiresAt is null ? query.Unset(row => row.ExpiresAt) : query.Set(row => row.ExpiresAt, expiresAt);
        query = expected.AmlLineageKey is null ? query.Unset(row => row.AmlLineageKey) : query.Set(row => row.AmlLineageKey, expected.AmlLineageKey);
        query = rejectionReason is null ? query.Unset(row => row.RejectionReason) : query.Set(row => row.RejectionReason, rejectionReason);
        // raw: atomic hosted-lease fencing and KYC completion; see AGENTS.md.
        var fence = HostedSessionLeaseFence.CreateGuard(lease);
        var transaction = SurrealQuery.Combine(
            SurrealQuery.Of("BEGIN"), KycEvidenceFence.Create(expected.UserId, expected.TenantId), fence,
            HostedSessionLeaseFence.AssertCurrent(),
            SurrealQuery.Of("IF $_attempt_expiry <= time::now() { THROW 'HOSTED_LEASE_LOST' }")
                .WithParam("_attempt_expiry", expected.ExpiresAt),
            query.Build(), SurrealQuery.Of("COMMIT"));
        try
        {
            var response = await KycEvidenceFence.ExecuteAsync(_executor, transaction, ct);
            response.EnsureAllOk();
            var saved = response.GetValues<KycSubmission>(5).SingleOrDefault();
            return AZOAResult<KycSubmission>.Success(saved is null ? null! : FromStorage(saved));
        }
        catch (SurrealStatementException exception) when (
            exception.Message.Contains(HostedSessionLeaseFence.FailureMarker, StringComparison.Ordinal))
        {
            return AZOAResult<KycSubmission>.Success(null!);
        }
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<KycSubmission>> TryReviewAsync(KycSubmission decision, CancellationToken ct = default)
    {
        var stored = ToStorage(decision);
        var update = SurrealWriter.UpdateOnly<KycSubmission>(stored.Id)
            .Where(row => row.Provider == KycProvider.MANUAL && row.ProviderKey == stored.ProviderKey
                && row.UserId == stored.UserId
                && row.ProviderSelectionVersion == stored.ProviderSelectionVersion
                && row.ProviderTrustRevision == stored.ProviderTrustRevision && row.ProviderResult == stored.ProviderResult)
            .Where(row => (row.Status == KycStatus.PENDING || row.Status == KycStatus.IN_REVIEW)
                && row.ExpiresAt != null && row.ExpiresAt > stored.ReviewedAt)
            .Set(row => row.Status, stored.Status);
        update = stored.TenantId is null ? update.Where(row => row.TenantId == null) : update.Where(row => row.TenantId == stored.TenantId);
        update = stored.ReviewerId is null ? update.Unset(row => row.ReviewerId) : update.Set(row => row.ReviewerId, stored.ReviewerId);
        update = stored.ReviewNotes is null ? update.Unset(row => row.ReviewNotes) : update.Set(row => row.ReviewNotes, stored.ReviewNotes);
        update = stored.RejectionReason is null ? update.Unset(row => row.RejectionReason) : update.Set(row => row.RejectionReason, stored.RejectionReason);
        update = stored.ReviewedAt is null ? update.Unset(row => row.ReviewedAt) : update.Set(row => row.ReviewedAt, stored.ReviewedAt);
        update = stored.ModifiedDate is null ? update.Unset(row => row.ModifiedDate) : update.Set(row => row.ModifiedDate, stored.ModifiedDate);
        var response = await KycEvidenceFence.ExecuteAsync(_executor, KycEvidenceFence.Mutation(decision.UserId, decision.TenantId, update.Build()), ct);
        response.EnsureAllOk();
        var saved = response.GetValues<KycSubmission>(2).SingleOrDefault();
        return AZOAResult<KycSubmission>.Success(saved is null ? null! : FromStorage(saved));
    }

    /// <inheritdoc/>
    public Task<AZOAResult<KycSubmission>> AttachDocumentsIfAbsentAsync(
        KycSubmission submission, IReadOnlyList<KycDocument> documents, CancellationToken ct = default)
        => AttachDocumentsCoreAsync(submission, documents, null, ct);

    private async Task<AZOAResult<KycSubmission>> AttachDocumentsCoreAsync(KycSubmission submission,
        IReadOnlyList<KycDocument> documents, TenantCustodialWitness? custody, CancellationToken ct)
    {
        var custodyGuards = CustodyGuards(submission, custody);
        if (custodyGuards is null) return CustodyRefusal();
        var storedSubmission = ToStorage(submission);
        var storedDocuments = documents.Select(ToStorage).ToList();
        var link = SurrealLink.ToLink(KycSubmission.SchemaNameConst, storedSubmission.Id);
        var findDocuments = SurrealGeneratedStatementScope.Assign(SurrealQuery<KycDocument>.From()
            .Where(row => row.SubmissionId == link).Limit(1), "kyc_attach_read", "_existing_documents");
        var mutation = SurrealWriter.UpdateOnly<KycSubmission>(storedSubmission.Id)
            .Where(row => row.Status == KycStatus.PENDING || row.Status == KycStatus.IN_REVIEW);
        mutation = storedSubmission.ModifiedDate is null ? mutation.Unset(row => row.ModifiedDate)
            : mutation.Set(row => row.ModifiedDate, storedSubmission.ModifiedDate);
        mutation = storedSubmission.ExpiresAt is null ? mutation.Unset(row => row.ExpiresAt)
            : mutation.Set(row => row.ExpiresAt, storedSubmission.ExpiresAt);
        // raw: current authority, attach-once assertion and scoped typed document creates share one transaction.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN") };
        statements.AddRange(custodyGuards);
        statements.Add(KycEvidenceFence.Create(submission.UserId, submission.TenantId));
        statements.Add(findDocuments);
        statements.Add(SurrealQuery.Of("IF array::len($_existing_documents) > 0 { THROW 'KYC_DOCUMENTS_ALREADY_ATTACHED' }"));
        statements.Add(SurrealGeneratedStatementScope.Assign(mutation.Build(), "kyc_attach", "_attached"));
        statements.Add(SurrealQuery.Of("IF $_attached = NONE OR $_attached = [] { THROW 'KYC_DOCUMENT_ATTACH_CONFLICT' }"));
        for (var index = 0; index < storedDocuments.Count; index++)
            statements.Add(SurrealGeneratedStatementScope.Apply(SurrealWriter.Create(storedDocuments[index]), "kyc_attach_doc" + index));
        statements.Add(SurrealQuery.Of("COMMIT"));
        try
        {
            await KycEvidenceFence.ExecuteAsync(_executor, SurrealQuery.Combine(statements.ToArray()), ct);
        }
        catch (SurrealStatementException exception) when (exception.Message.Contains("KYC_DOCUMENTS_ALREADY_ATTACHED", StringComparison.Ordinal))
        { /* Recover the original attachment below. */ }
        catch (SurrealStatementException exception) when (exception.Message.Contains("KYC_DOCUMENT_ATTACH_CONFLICT", StringComparison.Ordinal))
        {
            return AZOAResult<KycSubmission>.FailureWithCode("The KYC attempt changed before attachment.", AzoaErrorCodes.Conflict);
        }

        var persistedDocuments = await GetDocumentsBySubmissionAsync(
            Guid.ParseExact(submission.Id, "N"),
            ct);
        if (persistedDocuments.IsError)
            return AZOAResult<KycSubmission>.Failure(PublicStoreError);
        if (!(persistedDocuments.Result?.Any() ?? false))
        {
            return AZOAResult<KycSubmission>.Failure(
                "KYC_DOCUMENT_ATTACH_CONFLICT: The KYC attempt changed before documents could be attached.");
        }

        var persisted = await GetSubmissionByIdAsync(Guid.ParseExact(submission.Id, "N"), ct);
        return persisted.IsError || persisted.Result is null
            ? AZOAResult<KycSubmission>.Failure(PublicStoreError)
            : persisted;
    }

    // ── Documents ───────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<AZOAResult<IEnumerable<KycDocument>>> GetDocumentsBySubmissionAsync(Guid submissionId, CancellationToken ct = default)
    {
        var link = SurrealLink.ToLink(KycSubmission.SchemaNameConst, submissionId.ToString("N"));
        var rows = await _executor.QueryAsync<KycDocument>(SurrealQuery<KycDocument>.From()
            .Where(row => row.SubmissionId == link).OrderBy(row => row.CreatedDate), ct);
        return AZOAResult<IEnumerable<KycDocument>>.Success(rows.Select(FromStorage).ToList());
    }

    /// <inheritdoc/>
    public async Task<AZOAResult<bool>> AddDocumentsAsync(IEnumerable<KycDocument> documents, CancellationToken ct = default)
    {
        foreach (var document in documents)
        {
            var response = await KycEvidenceFence.ExecuteAsync(_executor, SurrealWriter.Upsert(ToStorage(document)), ct);
            response.EnsureAllOk();
        }
        return AZOAResult<bool>.Success(true);
    }

    // ── Mapping ───────────────────────────────────────────────────────────────


    /// <summary>
    /// Produce a write-ready clone whose FK fields carry the SurrealDB record-link
    /// form (<c>table:id</c>) and whose dates are UTC-kinded. The in-memory POCO
    /// keeps bare-hex id fields; this is the only place link encoding is applied.
    /// </summary>
    private static KycSubmission ToStorage(KycSubmission s) => new()
    {
        Id                = s.Id,
        UserId          = SurrealLink.ToLink("user", s.UserId),
        TenantId          = string.IsNullOrWhiteSpace(s.TenantId)
            ? null
            : SurrealLink.ToLink("user", s.TenantId),
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
        AmlLineageKey      = s.AmlLineageKey,
        SubmittedAt       = AsUtc(s.SubmittedAt),
        ReviewedAt        = AsUtc(s.ReviewedAt),
        ExpiresAt         = AsUtc(s.ExpiresAt),
        CreatedDate       = AsUtc(s.CreatedDate),
        ModifiedDate      = AsUtc(s.ModifiedDate)
    };

    private static KycSubmission FromStorage(KycSubmission s) => new()
    {
        Id                = s.Id,
        UserId          = SurrealLink.FromLink(s.UserId),
        TenantId          = string.IsNullOrWhiteSpace(s.TenantId)
            ? null
            : SurrealLink.FromLink(s.TenantId),
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
        AmlLineageKey      = s.AmlLineageKey,
        SubmittedAt       = s.SubmittedAt,
        ReviewedAt        = s.ReviewedAt,
        ExpiresAt         = s.ExpiresAt,
        CreatedDate       = s.CreatedDate,
        ModifiedDate      = s.ModifiedDate
    };

    private static KycDocument ToStorage(KycDocument d) => new()
    {
        Id            = d.Id,
        SubmissionId  = SurrealLink.ToLink(KycSubmission.SchemaNameConst, d.SubmissionId),
        Type          = d.Type,
        FileUrl       = d.FileUrl,
        FileName      = d.FileName,
        MimeType      = d.MimeType,
        FileSizeBytes = d.FileSizeBytes,
        Metadata      = d.Metadata,
        CreatedDate   = AsUtc(d.CreatedDate)
    };

    private static KycDocument FromStorage(KycDocument d) => new()
    {
        Id            = d.Id,
        SubmissionId  = SurrealLink.FromLink(d.SubmissionId),
        Type          = d.Type,
        FileUrl       = d.FileUrl,
        FileName      = d.FileName,
        MimeType      = d.MimeType,
        FileSizeBytes = d.FileSizeBytes,
        Metadata      = d.Metadata,
        CreatedDate   = d.CreatedDate
    };

    private static DateTimeOffset AsUtc(DateTimeOffset value)
        => value.ToUniversalTime();

    private static DateTimeOffset? AsUtc(DateTimeOffset? value)
        => value?.ToUniversalTime();
}
