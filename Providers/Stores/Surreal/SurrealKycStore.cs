// SPDX-License-Identifier: UNLICENSED

using SurrealForge.Client;
using SurrealForge.Client.Query;
using System.Diagnostics;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IKycStore"/>.
///
/// ID encoding: <c>Guid.ToString("N").ToLowerInvariant()</c> (32-char hex, no
/// dashes). The <c>avatar_id</c> / <c>submission_id</c> foreign keys are
/// SurrealDB record links written via <see cref="SurrealLink"/>
/// (<c>table:id</c>) so SurrealDB 3.x's strict <c>record&lt;table&gt;</c>
/// coercion accepts them; reads strip the prefix back to bare hex.
///
/// The store round-trips the source-generated <see cref="KycSubmission"/> /
/// <see cref="KycDocument"/> POCOs directly: a private <c>ToStorage</c> swaps
/// the in-memory bare-hex id fields for link form before write, and
/// <c>FromStorage</c> reverses it after read. Mirrors
/// <c>SurrealStarStore</c>'s ToPoco/FromPoco pattern.
/// </summary>
public sealed class SurrealKycStore : IKycStore
{
    private const string PublicStoreError =
        "KYC_STORE_UNAVAILABLE: KYC persistence is temporarily unavailable.";
    private readonly ISurrealExecutor _executor;
    private readonly ILogger<SurrealKycStore> _logger;

    public SurrealKycStore(
        ISurrealExecutor executor,
        ILogger<SurrealKycStore>? logger = null)
    {
        _executor = executor;
        _logger = logger ?? NullLogger<SurrealKycStore>.Instance;
    }

    // ── Submissions ─────────────────────────────────────────────────────────

    public async Task<AZOAResult<KycSubmission>> GetSubmissionByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery.SelectById(KycSubmission.SchemaNameConst, SurrealId.ToSurrealId(id));
            var row = await _executor.QuerySingleAsync<KycSubmission>(q, ct);
            return new AZOAResult<KycSubmission>
            {
                Message = row == null ? "No KYC submission found." : "Success",
                Result  = row == null ? null : FromStorage(row)
            };
        }
        catch (Exception ex)
        {
            return StoreFailure<KycSubmission>(ex, nameof(GetSubmissionByIdAsync), id);
        }
    }

    public Task<AZOAResult<KycSubmission>> GetLatestSubmissionByAvatarAsync(
        Guid avatarId,
        CancellationToken ct = default)
        => GetLatestSubmissionAsync(avatarId, null, ct);

    public async Task<AZOAResult<KycSubmission>> GetLatestSubmissionAsync(
        Guid avatarId,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE avatar_id = $_avatar AND (($_has_tenant AND tenant_id = $_tenant) OR (!$_has_tenant AND tenant_id = NONE)) ORDER BY submitted_at DESC LIMIT 1")
                .WithParam("_t",      KycSubmission.SchemaNameConst)
                .WithParam("_avatar", SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(avatarId)))
                .WithParam("_has_tenant", tenantId.HasValue)
                .WithParam("_tenant", tenantId.HasValue
                    ? SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(tenantId.Value))
                    : null);

            var row = await _executor.QuerySingleAsync<KycSubmission>(q, ct);
            return new AZOAResult<KycSubmission>
            {
                Message = row == null ? "No KYC submission found for avatar." : "Success",
                Result  = row == null ? null : FromStorage(row)
            };
        }
        catch (Exception ex)
        {
            return StoreFailure<KycSubmission>(ex, nameof(GetLatestSubmissionAsync),
                $"{avatarId:N}:{tenantId?.ToString("N") ?? "node"}");
        }
    }

    public Task<AZOAResult<KycSubmission>> GetActiveSubmissionByAvatarAsync(
        Guid avatarId,
        CancellationToken ct = default)
        => GetActiveSubmissionAsync(avatarId, null, ct);

    public async Task<AZOAResult<KycSubmission>> GetActiveSubmissionAsync(
        Guid avatarId,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE avatar_id = $_avatar AND (($_has_tenant AND tenant_id = $_tenant) OR (!$_has_tenant AND tenant_id = NONE)) AND status INSIDE $_active ORDER BY submitted_at DESC LIMIT 1")
                .WithParam("_t",      KycSubmission.SchemaNameConst)
                .WithParam("_avatar", SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(avatarId)))
                .WithParam("_has_tenant", tenantId.HasValue)
                .WithParam("_tenant", tenantId.HasValue
                    ? SurrealLink.ToLink("avatar", SurrealId.ToSurrealId(tenantId.Value))
                    : null)
                .WithParam("_active", new[] { nameof(KycStatus.PENDING), nameof(KycStatus.IN_REVIEW) });

            var row = await _executor.QuerySingleAsync<KycSubmission>(q, ct);
            return new AZOAResult<KycSubmission>
            {
                Message = row == null ? "No active KYC submission." : "Success",
                Result  = row == null ? null : FromStorage(row)
            };
        }
        catch (Exception ex)
        {
            return StoreFailure<KycSubmission>(ex, nameof(GetActiveSubmissionAsync),
                $"{avatarId:N}:{tenantId?.ToString("N") ?? "node"}");
        }
    }

    public async Task<AZOAResult<IEnumerable<KycSubmission>>> GetPendingAsync(CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE status INSIDE $_active ORDER BY submitted_at ASC")
                .WithParam("_t",      KycSubmission.SchemaNameConst)
                .WithParam("_active", new[] { nameof(KycStatus.PENDING), nameof(KycStatus.IN_REVIEW) });

            var rows = await _executor.QueryAsync<KycSubmission>(q, ct);
            return new AZOAResult<IEnumerable<KycSubmission>>
            {
                Result  = rows.Select(FromStorage).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return StoreFailure<IEnumerable<KycSubmission>>(ex, nameof(GetPendingAsync), "operator-list");
        }
    }

    public async Task<AZOAResult<IReadOnlyList<KycSubmission>>> GetPendingPageAsync(
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        var query = SurrealQuery
            .Of("SELECT * FROM type::table($_t) WHERE status INSIDE $_active AND expires_at != NONE AND expires_at > time::now() ORDER BY submitted_at ASC, id ASC START $_offset LIMIT $_limit")
            .WithParam("_t", KycSubmission.SchemaNameConst)
            .WithParam("_active", new[] { nameof(KycStatus.PENDING), nameof(KycStatus.IN_REVIEW) })
            .WithParam("_offset", Math.Max(0, offset))
            .WithParam("_limit", Math.Clamp(limit, 1, 101));
        var rows = await _executor.QueryAsync<KycSubmission>(query, ct);
        return AZOAResult<IReadOnlyList<KycSubmission>>.Success(rows.Select(FromStorage).ToList());
    }

    public async Task<AZOAResult<IReadOnlyList<KycSubmission>>> GetOperatorPageAsync(
        IReadOnlyCollection<KycStatus> statuses,
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        if (statuses.Count == 0)
            return AZOAResult<IReadOnlyList<KycSubmission>>.Success(Array.Empty<KycSubmission>());
        var query = SurrealQuery
            .Of("SELECT * FROM type::table($_t) WHERE status INSIDE $_statuses ORDER BY submitted_at DESC, id ASC START $_offset LIMIT $_limit")
            .WithParam("_t", KycSubmission.SchemaNameConst)
            .WithParam("_statuses", statuses.Select(status => status.ToString()).ToArray())
            .WithParam("_offset", Math.Max(0, offset))
            .WithParam("_limit", Math.Clamp(limit, 1, 101));
        var rows = await _executor.QueryAsync<KycSubmission>(query, ct);
        return AZOAResult<IReadOnlyList<KycSubmission>>.Success(rows.Select(FromStorage).ToList());
    }

    public async Task<AZOAResult<IReadOnlyList<KycSubmission>>> GetEffectiveOperatorPageAsync(
        string effectiveStatus,
        int offset,
        int limit,
        CancellationToken ct = default)
    {
        var statement = EffectiveStatusStatement(effectiveStatus, count: false);
        if (statement is null)
            return AZOAResult<IReadOnlyList<KycSubmission>>.Failure("Unsupported KYC status filter.");
        // raw: bounded effective-expiry classification; see this directory's AGENTS.md.
        var query = SurrealQuery
            .Of(statement)
            .WithParam("_t", KycSubmission.SchemaNameConst)
            .WithParam("_active", new[] { nameof(KycStatus.PENDING), nameof(KycStatus.IN_REVIEW) })
            .WithParam("_approved", nameof(KycStatus.APPROVED))
            .WithParam("_terminal_negative", new[] { nameof(KycStatus.REJECTED), nameof(KycStatus.EXPIRED) })
            .WithParam("_offset", Math.Max(0, offset))
            .WithParam("_limit", Math.Clamp(limit, 1, 101));
        try
        {
            var rows = await _executor.QueryAsync<KycSubmission>(query, ct);
            return AZOAResult<IReadOnlyList<KycSubmission>>.Success(rows.Select(FromStorage).ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StoreFailure<IReadOnlyList<KycSubmission>>(
                ex,
                nameof(GetEffectiveOperatorPageAsync),
                effectiveStatus);
        }
    }

    public async Task<AZOAResult<long>> CountByStatusesAsync(
        IReadOnlyCollection<KycStatus> statuses,
        CancellationToken ct = default)
    {
        if (statuses.Count == 0)
            return AZOAResult<long>.Success(0);
        var query = SurrealQuery
            .Of("SELECT VALUE count() FROM type::table($_t) WHERE status INSIDE $_statuses GROUP ALL")
            .WithParam("_t", KycSubmission.SchemaNameConst)
            .WithParam("_statuses", statuses.Select(status => status.ToString()).ToArray());
        try
        {
            var values = await _executor.QueryAsync<long>(query, ct);
            return AZOAResult<long>.Success(values.SingleOrDefault());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StoreFailure<long>(
                ex,
                nameof(CountByStatusesAsync),
                string.Join(',', statuses));
        }
    }

    public async Task<AZOAResult<long>> CountEffectiveStatusAsync(
        string effectiveStatus,
        CancellationToken ct = default)
    {
        var statement = EffectiveStatusStatement(effectiveStatus, count: true);
        if (statement is null)
            return AZOAResult<long>.Failure("Unsupported KYC status filter.");
        // raw: the count shares the exact effective-expiry predicate used by the page.
        var query = SurrealQuery
            .Of(statement)
            .WithParam("_t", KycSubmission.SchemaNameConst)
            .WithParam("_active", new[] { nameof(KycStatus.PENDING), nameof(KycStatus.IN_REVIEW) })
            .WithParam("_approved", nameof(KycStatus.APPROVED))
            .WithParam("_terminal_negative", new[] { nameof(KycStatus.REJECTED), nameof(KycStatus.EXPIRED) });
        try
        {
            var values = await _executor.QueryAsync<long>(query, ct);
            return AZOAResult<long>.Success(values.SingleOrDefault());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return StoreFailure<long>(ex, nameof(CountEffectiveStatusAsync), effectiveStatus);
        }
    }

    private static string? EffectiveStatusStatement(string value, bool count)
    {
        var prefix = count ? "SELECT VALUE count()" : "SELECT *";
        var suffix = count
            ? " GROUP ALL"
            : " ORDER BY submitted_at DESC, id ASC START $_offset LIMIT $_limit";
        var predicate = value?.Trim().ToLowerInvariant() switch
        {
            "pending" => "status INSIDE $_active AND expires_at != NONE AND expires_at > time::now()",
            "approved" => "status = $_approved AND expires_at != NONE AND expires_at > time::now()",
            "rejected" => "status INSIDE $_terminal_negative OR ((status INSIDE $_active OR status = $_approved) AND (expires_at = NONE OR expires_at <= time::now()))",
            _ => null,
        };
        return predicate is null
            ? null
            : string.Concat(prefix, " FROM type::table($_t) WHERE ", predicate, suffix);
    }

    public async Task<AZOAResult<KycSubmission>> UpsertSubmissionAsync(KycSubmission submission, CancellationToken ct = default)
    {
        try
        {
            var body = ToStorage(submission);
            var q = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t",    KycSubmission.SchemaNameConst)
                .WithParam("_id",   body.Id)
                .WithParam("_body", body);

            var resp = await _executor.ExecuteAsync(q, ct);
            resp.EnsureAllOk();

            var saved  = resp.GetValues<KycSubmission>(0).FirstOrDefault();
            var result = saved is not null ? FromStorage(saved) : submission;

            return new AZOAResult<KycSubmission> { Result = result, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return StoreFailure<KycSubmission>(ex, nameof(UpsertSubmissionAsync), submission.Id);
        }
    }

    public async Task<AZOAResult<KycSubmission>> CreateSubmissionAsync(
        KycSubmission submission,
        IReadOnlyList<KycDocument> documents,
        CancellationToken ct = default)
    {
        try
        {
            var storedSubmission = ToStorage(submission);
            var storedDocuments = documents.Select(ToStorage).ToList();
            var hasTenant = !string.IsNullOrWhiteSpace(submission.TenantId);
            var tenantId = hasTenant ? submission.TenantId : string.Empty;
            var authorityGuardTable = "kyc_authority_guard";
            var attemptGuardId = $"attempt-{submission.AvatarId}-{(hasTenant ? tenantId : "direct")}";
            var loadSelection = SurrealQuery
                .Of("LET $_selection = (SELECT provider_key, selection_version FROM type::record($_selection_table, $_tenant_id) WHERE $_has_tenant)")
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
                .Of("IF $_has_tenant { UPSERT type::record($_guard_table, $_guard_id) CONTENT { kind: 'tenant', authority_revision: $_revision, provider_key: $_provider, touched_at: time::now() } RETURN NONE }")
                .WithParam("_has_tenant", hasTenant)
                .WithParam("_guard_table", authorityGuardTable)
                .WithParam("_guard_id", $"tenant-{tenantId}")
                .WithParam("_revision", submission.ProviderSelectionVersion)
                .WithParam("_provider", submission.ProviderKey);
            var writeProviderGuard = SurrealQuery
                .Of("IF $_has_tenant { UPSERT type::record($_guard_table, $_guard_id) CONTENT { kind: 'provider', authority_revision: $_revision, provider_key: $_provider, touched_at: time::now() } RETURN NONE }")
                .WithParam("_has_tenant", hasTenant)
                .WithParam("_guard_table", authorityGuardTable)
                .WithParam("_guard_id", $"provider-{submission.ProviderKey}")
                .WithParam("_revision", submission.ProviderTrustRevision)
                .WithParam("_provider", submission.ProviderKey);
            var writeAttemptGuard = SurrealQuery
                .Of("UPSERT type::record($_guard_table, $_guard_id) SET kind = 'attempt', authority_revision = math::max([authority_revision ?? 0, 0]) + 1, provider_key = $_provider, touched_at = time::now() RETURN NONE")
                .WithParam("_guard_table", authorityGuardTable)
                .WithParam("_guard_id", attemptGuardId)
                .WithParam("_provider", submission.ProviderKey);
            var findActive = SurrealQuery
                .Of("LET $_active = (SELECT id FROM type::table($_submission_table) WHERE avatar_id = $_avatar AND (($_has_tenant AND tenant_id = $_tenant) OR (!$_has_tenant AND tenant_id = NONE)) AND status INSIDE $_active_statuses LIMIT 1)")
                .WithParam("_submission_table", KycSubmission.SchemaNameConst)
                .WithParam("_avatar", storedSubmission.AvatarId)
                .WithParam("_has_tenant", !string.IsNullOrWhiteSpace(storedSubmission.TenantId))
                .WithParam("_tenant", storedSubmission.TenantId)
                .WithParam("_active_statuses", new[] { nameof(KycStatus.PENDING), nameof(KycStatus.IN_REVIEW) });
            var rejectActive = SurrealQuery.Of(
                "IF array::len($_active) > 0 { THROW 'An active KYC submission already exists' } ELSE { NONE }");
            var createSubmission = SurrealQuery
                .Of("CREATE type::record($_submission_table, $_submission_id) CONTENT $_submission RETURN AFTER")
                .WithParam("_submission_table", KycSubmission.SchemaNameConst)
                .WithParam("_submission_id", storedSubmission.Id)
                .WithParam("_submission", storedSubmission);
            var createDocuments = SurrealQuery
                .Of("FOR $_document IN $_documents { CREATE type::record($_document_table, $_document.id) CONTENT $_document RETURN NONE }")
                .WithParam("_document_table", KycDocument.SchemaNameConst)
                .WithParam("_documents", storedDocuments);
            // raw: atomic active-attempt CAS + document batch; see this directory's AGENTS.md.
            var transaction = SurrealQuery.Combine(
                SurrealQuery.Of("BEGIN"),
                loadSelection,
                assertSelection,
                loadProfile,
                assertProfile,
                writeTenantGuard,
                writeProviderGuard,
                writeAttemptGuard,
                findActive,
                rejectActive,
                createSubmission,
                createDocuments,
                SurrealQuery.Of("COMMIT"));

            var response = await _executor.ExecuteAsync(transaction, ct);
            response.EnsureAllOk();
            return new AZOAResult<KycSubmission> { Result = submission, Message = "Created." };
        }
        catch (Exception ex)
        {
            return StoreFailure<KycSubmission>(ex, nameof(CreateSubmissionAsync), submission.Id);
        }
    }

    public async Task<AZOAResult<KycSubmission>> TryReviewAsync(
        KycSubmission decision,
        CancellationToken ct = default)
    {
        try
        {
            var stored = ToStorage(decision);
            // raw: single-winner manual-review CAS; see this directory's AGENTS.md.
            var query = SurrealQuery
                .Of("UPDATE type::record($_table, $_id) SET status = $_status, reviewer_id = $_reviewer, review_notes = $_notes, rejection_reason = $_reason, reviewed_at = $_reviewed, modified_date = $_modified WHERE provider = $_manual AND provider_key = $_provider_key AND avatar_id = $_avatar_id AND tenant_id = $_tenant_id AND provider_selection_version = $_selection_version AND provider_trust_revision = $_trust_revision AND provider_result = $_provider_result AND status INSIDE $_active AND expires_at != NONE AND expires_at > $_reviewed RETURN AFTER")
                .WithParam("_table", KycSubmission.SchemaNameConst)
                .WithParam("_id", stored.Id)
                .WithParam("_status", stored.Status.ToString())
                .WithParam("_reviewer", stored.ReviewerId)
                .WithParam("_notes", stored.ReviewNotes)
                .WithParam("_reason", stored.RejectionReason)
                .WithParam("_reviewed", stored.ReviewedAt)
                .WithParam("_modified", stored.ModifiedDate)
                .WithParam("_manual", nameof(KycProvider.MANUAL))
                .WithParam("_provider_key", stored.ProviderKey)
                .WithParam("_avatar_id", stored.AvatarId)
                .WithParam("_tenant_id", stored.TenantId)
                .WithParam("_selection_version", stored.ProviderSelectionVersion)
                .WithParam("_trust_revision", stored.ProviderTrustRevision)
                .WithParam("_provider_result", stored.ProviderResult)
                .WithParam("_active", new[] { nameof(KycStatus.PENDING), nameof(KycStatus.IN_REVIEW) });
            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            var saved = response.GetValues<KycSubmission>(0).SingleOrDefault();
            return new AZOAResult<KycSubmission>
            {
                Result = saved is null ? null : FromStorage(saved),
                Message = saved is null
                    ? "KYC submission changed before the review decision could be committed."
                    : "Reviewed."
            };
        }
        catch (Exception ex)
        {
            return StoreFailure<KycSubmission>(ex, nameof(TryReviewAsync), decision.Id);
        }
    }

    public async Task<AZOAResult<KycSubmission>> AttachDocumentsIfAbsentAsync(
        KycSubmission submission,
        IReadOnlyList<KycDocument> documents,
        CancellationToken ct = default)
    {
        try
        {
            var storedSubmission = ToStorage(submission);
            var storedDocuments = documents.Select(ToStorage).ToList();
            var findDocuments = SurrealQuery
                .Of("LET $_existing_documents = (SELECT id FROM type::table($_document_table) WHERE submission_id = $_submission_link LIMIT 1)")
                .WithParam("_document_table", KycDocument.SchemaNameConst)
                .WithParam("_submission_link", SurrealLink.ToLink(
                    KycSubmission.SchemaNameConst,
                    storedSubmission.Id));
            var attach = SurrealQuery
                .Of("LET $_attached = (UPDATE type::record($_submission_table, $_submission_id) SET expires_at = $_expires_at, modified_date = $_modified_date WHERE status INSIDE $_active AND array::len($_existing_documents) = 0 RETURN AFTER)")
                .WithParam("_submission_table", KycSubmission.SchemaNameConst)
                .WithParam("_submission_id", storedSubmission.Id)
                .WithParam("_expires_at", storedSubmission.ExpiresAt)
                .WithParam("_modified_date", storedSubmission.ModifiedDate)
                .WithParam("_active", new[] { nameof(KycStatus.PENDING), nameof(KycStatus.IN_REVIEW) });
            var createDocuments = SurrealQuery
                .Of("IF array::len($_attached) > 0 { FOR $_document IN $_documents { CREATE type::record($_document_table, $_document.id) CONTENT $_document RETURN NONE } } ELSE { NONE }")
                .WithParam("_document_table", KycDocument.SchemaNameConst)
                .WithParam("_documents", storedDocuments);
            // raw: attach-once CAS + document batch; see this directory's AGENTS.md.
            var transaction = SurrealQuery.Combine(
                SurrealQuery.Of("BEGIN"),
                findDocuments,
                attach,
                createDocuments,
                SurrealQuery.Of("COMMIT"));

            var response = await _executor.ExecuteAsync(transaction, ct);
            response.EnsureAllOk();

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
        catch (Exception ex)
        {
            return StoreFailure<KycSubmission>(
                ex,
                nameof(AttachDocumentsIfAbsentAsync),
                submission.Id);
        }
    }

    // ── Documents ───────────────────────────────────────────────────────────

    public async Task<AZOAResult<IEnumerable<KycDocument>>> GetDocumentsBySubmissionAsync(Guid submissionId, CancellationToken ct = default)
    {
        try
        {
            var q = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE submission_id = $_submission ORDER BY created_date ASC")
                .WithParam("_t",          KycDocument.SchemaNameConst)
                .WithParam("_submission", SurrealLink.ToLink(KycSubmission.SchemaNameConst, SurrealId.ToSurrealId(submissionId)));

            var rows = await _executor.QueryAsync<KycDocument>(q, ct);
            return new AZOAResult<IEnumerable<KycDocument>>
            {
                Result  = rows.Select(FromStorage).ToList(),
                Message = "Success"
            };
        }
        catch (Exception ex)
        {
            return StoreFailure<IEnumerable<KycDocument>>(
                ex,
                nameof(GetDocumentsBySubmissionAsync),
                submissionId);
        }
    }

    public async Task<AZOAResult<bool>> AddDocumentsAsync(IEnumerable<KycDocument> documents, CancellationToken ct = default)
    {
        try
        {
            foreach (var doc in documents)
            {
                var body = ToStorage(doc);
                var q = SurrealQuery
                    .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                    .WithParam("_t",    KycDocument.SchemaNameConst)
                    .WithParam("_id",   body.Id)
                    .WithParam("_body", body);

                var resp = await _executor.ExecuteAsync(q, ct);
                resp.EnsureAllOk();
            }

            return new AZOAResult<bool> { Result = true, Message = "Saved." };
        }
        catch (Exception ex)
        {
            return StoreFailure<bool>(ex, nameof(AddDocumentsAsync), "document-batch");
        }
    }

    private AZOAResult<T> StoreFailure<T>(Exception exception, string operation, object entityId)
    {
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        _logger.LogError(
            exception,
            "KYC persistence failure in {Operation}; correlation={CorrelationId}; entity={EntityId}",
            operation,
            correlationId,
            entityId);
        return AZOAResult<T>.FailureWithCode(
            PublicStoreError,
            AzoaErrorCodes.DependencyUnavailable);
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
        AvatarId          = SurrealLink.ToLink("avatar", s.AvatarId),
        TenantId          = string.IsNullOrWhiteSpace(s.TenantId)
            ? null
            : SurrealLink.ToLink("avatar", s.TenantId),
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
        SubmittedAt       = AsUtc(s.SubmittedAt),
        ReviewedAt        = AsUtc(s.ReviewedAt),
        ExpiresAt         = AsUtc(s.ExpiresAt),
        CreatedDate       = AsUtc(s.CreatedDate),
        ModifiedDate      = AsUtc(s.ModifiedDate)
    };

    private static KycSubmission FromStorage(KycSubmission s) => new()
    {
        Id                = s.Id,
        AvatarId          = SurrealLink.FromLink(s.AvatarId),
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
