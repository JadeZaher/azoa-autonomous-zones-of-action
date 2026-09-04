// SPDX-License-Identifier: UNLICENSED

using System.Diagnostics;
using SurrealForge.Client;
using SurrealForge.Client.Query;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <summary>
/// SurrealDB-backed <see cref="IKycScreeningStore"/> for AML results, monitoring
/// registrations, and the singleton cold-poll high-water mark. Like
/// <see cref="SurrealKycIdentityStore"/>, every table name is a compile-time
/// constant and none of these tables appears on a generic consumer read surface.
///
/// <para>Every predicate that selects rows on behalf of a caller carries a tenant
/// component. The AML key is a person's name and date of birth — a value two
/// tenants legitimately share — so an unscoped WHERE here is a cross-tenant read,
/// not a convenience.</para>
/// </summary>
public sealed class SurrealKycScreeningStore : IKycScreeningStore
{
    private const string PublicStoreError =
        "KYC_SCREENING_STORE_UNAVAILABLE: Screening persistence is temporarily unavailable.";

    /// <summary>Backoff applied after a failed poll cycle; bounded, never a hot retry.</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(15);

    /// <summary>Tenant-scoped predicate shared by every consumer-reachable read.</summary>
    private const string TenantPredicate =
        "(($_has_tenant AND tenant_id = $_tenant) OR (!$_has_tenant AND tenant_id = NONE))";

    private const string LatestResultQuery =
        "SELECT * FROM type::table($_t) WHERE subject_avatar_id = $_avatar AND "
        + TenantPredicate
        + " ORDER BY screened_at DESC LIMIT 1";

    private const string ResultsForAvatarQuery =
        "SELECT * FROM type::table($_t) WHERE subject_avatar_id = $_avatar AND "
        + TenantPredicate
        + " ORDER BY screened_at DESC LIMIT $_limit";

    private const string EraseSubjectQuery =
        "UPDATE type::table($_result_t) SET query_full_name = NONE, query_birthday_time_from = NONE,"
        + " erased_at = $_erased_at, erasure_reason = $_reason, modified_date = $_erased_at"
        + " WHERE subject_avatar_id INSIDE $_avatars AND " + TenantPredicate + " AND erased_at = NONE"
        + " RETURN VALUE id;"
        + " UPDATE type::table($_monitor_t) SET query_full_name = NONE, query_birthday_time_from = NONE,"
        + " erased_at = $_erased_at, erasure_reason = $_reason, modified_date = $_erased_at"
        + " WHERE subject_avatar_id INSIDE $_avatars AND " + TenantPredicate + " AND erased_at = NONE"
        + " RETURN VALUE id;";

    private readonly ISurrealExecutor _executor;
    private readonly ILogger<SurrealKycScreeningStore> _logger;

    public SurrealKycScreeningStore(
        ISurrealExecutor executor,
        ILogger<SurrealKycScreeningStore>? logger = null)
    {
        _executor = executor;
        _logger = logger ?? NullLogger<SurrealKycScreeningStore>.Instance;
    }

    // ── Results ─────────────────────────────────────────────────────────────

    public async Task<AZOAResult<KycScreeningResult>> SaveResultAsync(
        KycScreeningResult result,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            var body = ToStorage(result);
            var query = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", KycScreeningResult.SchemaNameConst)
                .WithParam("_id", body.Id)
                .WithParam("_body", body);

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            var saved = response.GetValues<KycScreeningResult>(0).FirstOrDefault();
            return AZOAResult<KycScreeningResult>.Success(
                saved is null ? result : FromStorage(saved),
                "Saved.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<KycScreeningResult>(exception, nameof(SaveResultAsync), result.Id);
        }
    }

    public async Task<AZOAResult<KycScreeningResult>> GetLatestResultAsync(
        Guid avatarId,
        Guid? tenantId,
        CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of(LatestResultQuery)
                .WithParam("_t", KycScreeningResult.SchemaNameConst)
                .WithParam("_avatar", SurrealRecordParam.Of("avatar", avatarId.ToString("N")))
                .WithParam("_has_tenant", tenantId.HasValue && tenantId.Value != Guid.Empty)
                .WithParam("_tenant", tenantId.HasValue && tenantId.Value != Guid.Empty
                    ? SurrealRecordParam.Of("avatar", tenantId.Value.ToString("N"))
                    : null);

            var row = await _executor.QuerySingleAsync<KycScreeningResult>(query, ct);
            return new AZOAResult<KycScreeningResult>
            {
                Message = row is null ? "No screening result found." : "Success",
                Result = row is null ? null : FromStorage(row),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<KycScreeningResult>(exception, nameof(GetLatestResultAsync), avatarId);
        }
    }

    public async Task<AZOAResult<IReadOnlyList<KycScreeningResult>>> GetResultsForAvatarAsync(
        Guid avatarId,
        Guid? tenantId,
        int limit,
        CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of(ResultsForAvatarQuery)
                .WithParam("_t", KycScreeningResult.SchemaNameConst)
                .WithParam("_avatar", SurrealRecordParam.Of("avatar", avatarId.ToString("N")))
                .WithParam("_has_tenant", tenantId.HasValue && tenantId.Value != Guid.Empty)
                .WithParam("_tenant", tenantId.HasValue && tenantId.Value != Guid.Empty
                    ? SurrealRecordParam.Of("avatar", tenantId.Value.ToString("N"))
                    : null)
                .WithParam("_limit", Math.Clamp(limit, 1, 500));

            var rows = await _executor.QueryAsync<KycScreeningResult>(query, ct);
            return AZOAResult<IReadOnlyList<KycScreeningResult>>.Success(
                rows.Select(FromStorage).ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<IReadOnlyList<KycScreeningResult>>(
                exception,
                nameof(GetResultsForAvatarAsync),
                avatarId);
        }
    }

    // ── Monitors ────────────────────────────────────────────────────────────

    public async Task<AZOAResult<KycScreeningMonitor>> SaveMonitorAsync(
        KycScreeningMonitor monitor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        try
        {
            var body = ToStorage(monitor);
            var query = SurrealQuery
                .Of("UPSERT type::record($_t, $_id) CONTENT $_body RETURN AFTER")
                .WithParam("_t", KycScreeningMonitor.SchemaNameConst)
                .WithParam("_id", body.Id)
                .WithParam("_body", body);

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            var saved = response.GetValues<KycScreeningMonitor>(0).FirstOrDefault();
            return AZOAResult<KycScreeningMonitor>.Success(
                saved is null ? monitor : FromStorage(saved),
                "Saved.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<KycScreeningMonitor>(exception, nameof(SaveMonitorAsync), monitor.Id);
        }
    }

    public async Task<AZOAResult<KycScreeningMonitor>> GetMonitorByQueryAsync(
        string tenantScope,
        string? queryFullName,
        string? queryBirthdayTimeFrom,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantScope))
        {
            // A scopeless lookup would resolve to whichever tenant registered first.
            return AZOAResult<KycScreeningMonitor>.Failure("A uniqueness scope is required.");
        }

        try
        {
            // Matches the provider's monitor key — the EXACT query shape — but only
            // WITHIN this uniqueness scope, mirroring kyc_identity's
            // (tenant_scope, person_key) lookup.
            var query = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE tenant_scope = $_scope AND query_full_name = $_name AND query_birthday_time_from = $_birthday LIMIT 1")
                .WithParam("_t", KycScreeningMonitor.SchemaNameConst)
                .WithParam("_scope", tenantScope)
                .WithParam("_name", queryFullName)
                .WithParam("_birthday", queryBirthdayTimeFrom ?? string.Empty);

            var row = await _executor.QuerySingleAsync<KycScreeningMonitor>(query, ct);
            if (row is not null && !string.Equals(row.TenantScope, tenantScope, StringComparison.Ordinal))
            {
                // Defence in depth behind the predicate: a row from another scope is
                // never handed back, whatever the query did.
                _logger.LogError(
                    "A KYC monitor lookup resolved a row outside the requested uniqueness scope; refusing it.");
                return AZOAResult<KycScreeningMonitor>.FailureWithCode(
                    PublicStoreError,
                    AzoaErrorCodes.DependencyUnavailable);
            }

            return new AZOAResult<KycScreeningMonitor>
            {
                Message = row is null ? "No monitor found." : "Success",
                Result = row is null ? null : FromStorage(row),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<KycScreeningMonitor>(exception, nameof(GetMonitorByQueryAsync), "query-shape");
        }
    }

    public async Task<AZOAResult<string>> GetProviderMonitorIdByQueryAsync(
        string? queryFullName,
        string? queryBirthdayTimeFrom,
        CancellationToken ct = default)
    {
        try
        {
            // Projects the opaque provider id ONLY. There is no row here to leak a
            // subject avatar, a tenant id, or a retained name to another tenant.
            var query = SurrealQuery
                .Of("SELECT VALUE provider_monitor_id FROM type::table($_t) WHERE query_full_name = $_name AND query_birthday_time_from = $_birthday AND state = $_active LIMIT 1")
                .WithParam("_t", KycScreeningMonitor.SchemaNameConst)
                .WithParam("_name", queryFullName)
                .WithParam("_birthday", queryBirthdayTimeFrom ?? string.Empty)
                .WithParam("_active", nameof(KycMonitorState.ACTIVE));

            var value = await _executor.QuerySingleAsync<string>(query, ct);
            return new AZOAResult<string>
            {
                Message = string.IsNullOrWhiteSpace(value) ? "No upstream monitor known." : "Success",
                Result = string.IsNullOrWhiteSpace(value) ? null : value,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<string>(exception, nameof(GetProviderMonitorIdByQueryAsync), "query-shape");
        }
    }

    public async Task<AZOAResult<IReadOnlyList<KycScreeningMonitor>>> GetMonitorsByProviderIdAsync(
        string providerMonitorId,
        CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of("SELECT * FROM type::table($_t) WHERE provider_monitor_id = $_monitor ORDER BY created_date ASC")
                .WithParam("_t", KycScreeningMonitor.SchemaNameConst)
                .WithParam("_monitor", providerMonitorId);

            var rows = await _executor.QueryAsync<KycScreeningMonitor>(query, ct);
            return AZOAResult<IReadOnlyList<KycScreeningMonitor>>.Success(
                rows.Select(FromStorage).ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<IReadOnlyList<KycScreeningMonitor>>(
                exception,
                nameof(GetMonitorsByProviderIdAsync),
                providerMonitorId);
        }
    }

    public async Task<AZOAResult<bool>> MarkMonitorRemovedAsync(
        string tenantScope,
        string providerMonitorId,
        DateTimeOffset removedAt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantScope))
            return AZOAResult<bool>.Failure("A uniqueness scope is required.");

        try
        {
            // Scoped: one upstream monitor backs one row per tenant, so an unscoped
            // UPDATE here would silently stop every other tenant's monitoring.
            var query = SurrealQuery
                .Of("UPDATE type::table($_t) SET state = $_removed, removed_at = $_removed_at, modified_date = $_removed_at WHERE tenant_scope = $_scope AND provider_monitor_id = $_monitor RETURN NONE")
                .WithParam("_t", KycScreeningMonitor.SchemaNameConst)
                .WithParam("_removed", nameof(KycMonitorState.REMOVED))
                .WithParam("_removed_at", removedAt.ToUniversalTime())
                .WithParam("_scope", tenantScope)
                .WithParam("_monitor", providerMonitorId);

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            return AZOAResult<bool>.Success(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<bool>(exception, nameof(MarkMonitorRemovedAsync), providerMonitorId);
        }
    }

    public async Task<AZOAResult<bool>> TouchMonitorPolledAsync(
        string monitorId,
        DateTimeOffset polledAt,
        CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET last_polled_at = $_polled_at RETURN NONE")
                .WithParam("_t", KycScreeningMonitor.SchemaNameConst)
                .WithParam("_id", monitorId)
                .WithParam("_polled_at", polledAt.ToUniversalTime());

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            return AZOAResult<bool>.Success(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<bool>(exception, nameof(TouchMonitorPolledAsync), monitorId);
        }
    }

    public async Task<AZOAResult<bool>> RecordMonitorFollowUpAsync(
        string monitorId,
        string? observedUpdateDate,
        DateTimeOffset followedUpAt,
        CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of("UPDATE type::record($_t, $_id) SET last_observed_update_date = $_observed, last_follow_up_at = $_followed_up_at, last_polled_at = $_followed_up_at, modified_date = $_followed_up_at RETURN NONE")
                .WithParam("_t", KycScreeningMonitor.SchemaNameConst)
                .WithParam("_id", monitorId)
                .WithParam("_observed", observedUpdateDate)
                .WithParam("_followed_up_at", followedUpAt.ToUniversalTime());

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            return AZOAResult<bool>.Success(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<bool>(exception, nameof(RecordMonitorFollowUpAsync), monitorId);
        }
    }

    // ── Retention + erasure (D25) ───────────────────────────────────────────

    public async Task<AZOAResult<int>> EraseSubjectAsync(
        IReadOnlyList<string> avatarIds,
        Guid? tenantId,
        string reason,
        DateTimeOffset erasedAt,
        CancellationToken ct = default)
    {
        if (avatarIds is null || avatarIds.Count == 0)
            return AZOAResult<int>.Success(0);

        try
        {
            // Of() accepts both the bare hex kyc_identity.avatar_ids holds and an
            // already-prefixed link, so either storage shape binds as a record id.
            var links = avatarIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => SurrealRecordParam.Of("avatar", id))
                .Where(link => link is not null)
                .ToArray();
            if (links.Length == 0)
                return AZOAResult<int>.Success(0);

            var erasedAtUtc = erasedAt.ToUniversalTime();
            var hasTenant = tenantId.HasValue && tenantId.Value != Guid.Empty;
            var tenantLink = hasTenant
                ? SurrealRecordParam.Of("avatar", tenantId!.Value.ToString("N"))
                : null;

            // Explicit SET: the verdict, the provider ids, the scope, and the audit
            // timestamps survive. Only the two retained personal fields are tombstoned.
            var query = SurrealQuery
                .Of(EraseSubjectQuery)
                .WithParam("_result_t", KycScreeningResult.SchemaNameConst)
                .WithParam("_monitor_t", KycScreeningMonitor.SchemaNameConst)
                .WithParam("_avatars", links)
                .WithParam("_has_tenant", hasTenant)
                .WithParam("_tenant", tenantLink)
                .WithParam("_erased_at", erasedAtUtc)
                .WithParam("_reason", reason);

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            var erased = response.GetValues<string>(0).Count() + response.GetValues<string>(1).Count();
            return AZOAResult<int>.Success(erased, "Erased.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<int>(exception, nameof(EraseSubjectAsync), "subject");
        }
    }

    public async Task<AZOAResult<int>> EraseRetentionExpiredAsync(
        DateTimeOffset asOf,
        string reason,
        int limit,
        CancellationToken ct = default)
    {
        try
        {
            var asOfUtc = asOf.ToUniversalTime();
            var bound = Math.Clamp(limit, 1, 1000);
            var query = SurrealQuery
                .Of("""
                    LET $due_results = (SELECT VALUE id FROM type::table($_result_t)
                        WHERE erased_at = NONE AND retention_expires_at != NONE AND retention_expires_at <= $_as_of
                        ORDER BY retention_expires_at ASC LIMIT $_limit);
                    UPDATE $due_results
                    SET query_full_name = NONE,
                        query_birthday_time_from = NONE,
                        erased_at = $_erased_at,
                        erasure_reason = $_reason,
                        modified_date = $_erased_at
                    RETURN VALUE id;
                    LET $due_monitors = (SELECT VALUE id FROM type::table($_monitor_t)
                        WHERE erased_at = NONE AND retention_expires_at != NONE AND retention_expires_at <= $_as_of
                        ORDER BY retention_expires_at ASC LIMIT $_limit);
                    UPDATE $due_monitors
                    SET query_full_name = NONE,
                        query_birthday_time_from = NONE,
                        erased_at = $_erased_at,
                        erasure_reason = $_reason,
                        modified_date = $_erased_at
                    RETURN VALUE id;
                    """)
                .WithParam("_result_t", KycScreeningResult.SchemaNameConst)
                .WithParam("_monitor_t", KycScreeningMonitor.SchemaNameConst)
                .WithParam("_as_of", asOfUtc)
                .WithParam("_limit", bound)
                .WithParam("_erased_at", asOfUtc)
                .WithParam("_reason", reason);

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            var erased = response.GetValues<string>(1).Count() + response.GetValues<string>(3).Count();
            return AZOAResult<int>.Success(erased, "Erased.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<int>(exception, nameof(EraseRetentionExpiredAsync), asOf);
        }
    }

    // ── Poll state ──────────────────────────────────────────────────────────

    public async Task<AZOAResult<KycScreeningPollState>> GetPollStateAsync(CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery.SelectById(
                KycScreeningPollState.SchemaNameConst,
                KycScreeningPollState.LocalId);
            var row = await _executor.QuerySingleAsync<KycScreeningPollState>(query, ct);
            return new AZOAResult<KycScreeningPollState>
            {
                Message = row is null ? "No poll state yet." : "Success",
                Result = row,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<KycScreeningPollState>(exception, nameof(GetPollStateAsync), "local");
        }
    }

    public async Task<AZOAResult<bool>> RecordPollSuccessAsync(
        string? highWaterMarkDate,
        DateTimeOffset completedAt,
        int changedCount,
        CancellationToken ct = default)
    {
        try
        {
            var query = SurrealQuery
                .Of("""
                    UPSERT type::record($_t, $_id)
                    SET high_water_mark_date = $_high_water_mark,
                        last_poll_completed_at = $_completed_at,
                        last_poll_changed_count = $_changed,
                        consecutive_failures = 0,
                        backoff_until = NONE,
                        modified_date = $_completed_at
                    RETURN NONE
                    """)
                .WithParam("_t", KycScreeningPollState.SchemaNameConst)
                .WithParam("_id", KycScreeningPollState.LocalId)
                .WithParam("_high_water_mark", highWaterMarkDate)
                .WithParam("_completed_at", completedAt.ToUniversalTime())
                .WithParam("_changed", (long)changedCount);

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            return AZOAResult<bool>.Success(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<bool>(exception, nameof(RecordPollSuccessAsync), "local");
        }
    }

    public async Task<AZOAResult<bool>> RecordPollFailureAsync(
        DateTimeOffset failedAt,
        CancellationToken ct = default)
    {
        try
        {
            // Bounded backoff, never a hot retry: a failing cycle is usually the
            // shared request budget refusing, and retrying into it is what earns the
            // one-hour IP ban.
            var query = SurrealQuery
                .Of("""
                    UPSERT type::record($_t, $_id)
                    SET consecutive_failures = (consecutive_failures OR 0) + 1,
                        backoff_until = $_backoff_until,
                        last_poll_started_at = $_failed_at,
                        modified_date = $_failed_at
                    RETURN NONE
                    """)
                .WithParam("_t", KycScreeningPollState.SchemaNameConst)
                .WithParam("_id", KycScreeningPollState.LocalId)
                .WithParam("_backoff_until", failedAt.Add(FailureBackoff).ToUniversalTime())
                .WithParam("_failed_at", failedAt.ToUniversalTime());

            var response = await _executor.ExecuteAsync(query, ct);
            response.EnsureAllOk();
            return AZOAResult<bool>.Success(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return StoreFailure<bool>(exception, nameof(RecordPollFailureAsync), "local");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private AZOAResult<T> StoreFailure<T>(Exception exception, string operation, object entityId)
    {
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        _logger.LogError(
            exception,
            "KYC screening persistence failure in {Operation}; correlation={CorrelationId}; entity={EntityId}",
            operation,
            correlationId,
            entityId);
        return AZOAResult<T>.FailureWithCode(PublicStoreError, AzoaErrorCodes.DependencyUnavailable);
    }

    private static KycScreeningResult ToStorage(KycScreeningResult result) => new()
    {
        Id = result.Id,
        SubjectAvatarId = string.IsNullOrWhiteSpace(result.SubjectAvatarId)
            ? null
            : SurrealLink.ToLink("avatar", result.SubjectAvatarId),
        TenantId = string.IsNullOrWhiteSpace(result.TenantId)
            ? null
            : SurrealLink.ToLink("avatar", result.TenantId),
        QueryFullName = result.QueryFullName,
        QueryBirthdayTimeFrom = result.QueryBirthdayTimeFrom,
        ScoreThreshold = result.ScoreThreshold,
        Decision = result.Decision,
        TopScore = result.TopScore,
        HitCount = result.HitCount,
        Topics = result.Topics,
        PersonIds = result.PersonIds,
        MonitorId = result.MonitorId,
        PolicyVersion = result.PolicyVersion,
        ResultsTruncated = result.ResultsTruncated,
        RetentionExpiresAt = result.RetentionExpiresAt?.ToUniversalTime(),
        ErasedAt = result.ErasedAt?.ToUniversalTime(),
        ErasureReason = result.ErasureReason,
        ScreenedAt = result.ScreenedAt.ToUniversalTime(),
        CreatedDate = result.CreatedDate.ToUniversalTime(),
        ModifiedDate = result.ModifiedDate?.ToUniversalTime(),
    };

    private static KycScreeningResult FromStorage(KycScreeningResult result) => new()
    {
        Id = result.Id,
        SubjectAvatarId = string.IsNullOrWhiteSpace(result.SubjectAvatarId)
            ? null
            : SurrealLink.FromLink(result.SubjectAvatarId),
        TenantId = string.IsNullOrWhiteSpace(result.TenantId)
            ? null
            : SurrealLink.FromLink(result.TenantId),
        QueryFullName = result.QueryFullName,
        QueryBirthdayTimeFrom = result.QueryBirthdayTimeFrom,
        ScoreThreshold = result.ScoreThreshold,
        Decision = result.Decision,
        TopScore = result.TopScore,
        HitCount = result.HitCount,
        Topics = result.Topics,
        PersonIds = result.PersonIds,
        MonitorId = result.MonitorId,
        PolicyVersion = result.PolicyVersion,
        ResultsTruncated = result.ResultsTruncated,
        RetentionExpiresAt = result.RetentionExpiresAt,
        ErasedAt = result.ErasedAt,
        ErasureReason = result.ErasureReason,
        ScreenedAt = result.ScreenedAt,
        CreatedDate = result.CreatedDate,
        ModifiedDate = result.ModifiedDate,
    };

    private static KycScreeningMonitor ToStorage(KycScreeningMonitor monitor) => new()
    {
        Id = monitor.Id,
        ProviderMonitorId = monitor.ProviderMonitorId,
        TenantScope = monitor.TenantScope,
        SubjectAvatarId = string.IsNullOrWhiteSpace(monitor.SubjectAvatarId)
            ? null
            : SurrealLink.ToLink("avatar", monitor.SubjectAvatarId),
        TenantId = string.IsNullOrWhiteSpace(monitor.TenantId)
            ? null
            : SurrealLink.ToLink("avatar", monitor.TenantId),
        QueryFullName = monitor.QueryFullName,
        QueryBirthdayTimeFrom = monitor.QueryBirthdayTimeFrom,
        FollowUpScoreThreshold = monitor.FollowUpScoreThreshold,
        State = monitor.State,
        RegisteredIdempotently = monitor.RegisteredIdempotently,
        LastObservedUpdateDate = monitor.LastObservedUpdateDate,
        LastPolledAt = monitor.LastPolledAt?.ToUniversalTime(),
        LastFollowUpAt = monitor.LastFollowUpAt?.ToUniversalTime(),
        RetentionExpiresAt = monitor.RetentionExpiresAt?.ToUniversalTime(),
        ErasedAt = monitor.ErasedAt?.ToUniversalTime(),
        ErasureReason = monitor.ErasureReason,
        RegisteredAt = monitor.RegisteredAt.ToUniversalTime(),
        CreatedDate = monitor.CreatedDate.ToUniversalTime(),
        ModifiedDate = monitor.ModifiedDate?.ToUniversalTime(),
        RemovedAt = monitor.RemovedAt?.ToUniversalTime(),
    };

    private static KycScreeningMonitor FromStorage(KycScreeningMonitor monitor) => new()
    {
        Id = monitor.Id,
        ProviderMonitorId = monitor.ProviderMonitorId,
        TenantScope = monitor.TenantScope,
        SubjectAvatarId = string.IsNullOrWhiteSpace(monitor.SubjectAvatarId)
            ? null
            : SurrealLink.FromLink(monitor.SubjectAvatarId),
        TenantId = string.IsNullOrWhiteSpace(monitor.TenantId)
            ? null
            : SurrealLink.FromLink(monitor.TenantId),
        QueryFullName = monitor.QueryFullName,
        QueryBirthdayTimeFrom = monitor.QueryBirthdayTimeFrom,
        FollowUpScoreThreshold = monitor.FollowUpScoreThreshold,
        State = monitor.State,
        RegisteredIdempotently = monitor.RegisteredIdempotently,
        LastObservedUpdateDate = monitor.LastObservedUpdateDate,
        LastPolledAt = monitor.LastPolledAt,
        LastFollowUpAt = monitor.LastFollowUpAt,
        RetentionExpiresAt = monitor.RetentionExpiresAt,
        ErasedAt = monitor.ErasedAt,
        ErasureReason = monitor.ErasureReason,
        RegisteredAt = monitor.RegisteredAt,
        CreatedDate = monitor.CreatedDate,
        ModifiedDate = monitor.ModifiedDate,
        RemovedAt = monitor.RemovedAt,
    };
}
