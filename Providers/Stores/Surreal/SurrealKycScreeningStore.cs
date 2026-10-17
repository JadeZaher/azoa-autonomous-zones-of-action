// SPDX-License-Identifier: UNLICENSED

using SurrealForge.Client;
using SurrealForge.Client.Query;
using SurrealForge.Client.Idempotency;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Core.Surreal;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;

namespace AZOA.WebAPI.Providers.Stores.Surreal;

/// <inheritdoc />
public sealed class SurrealKycScreeningStore : IKycScreeningStore
{
    private const string PublicStoreError =
        "KYC_SCREENING_STORE_UNAVAILABLE: Screening persistence is temporarily unavailable.";

    /// <summary>Backoff applied after a failed poll cycle; bounded, never a hot retry.</summary>
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(15);

    /// <summary>Tenant-scoped predicate shared by every consumer-reachable read.</summary>
    private const string TenantPredicate =
        "(($_has_tenant AND tenant_id = $_tenant) OR (!$_has_tenant AND tenant_id = NONE))";

    private const string EraseSubjectQuery =
        "BEGIN TRANSACTION; UPDATE type::table($_result_t) SET query_full_name = NONE, query_birthday_time_from = NONE,"
        + " erased_at = $_erased_at, erasure_reason = $_reason, modified_date = $_erased_at"
        + " WHERE subject_user_id INSIDE $_users AND " + TenantPredicate + " AND erased_at = NONE"
        + " RETURN VALUE id;"
        + " UPDATE type::table($_monitor_t) SET query_full_name = NONE, query_birthday_time_from = NONE,"
        + " erased_at = $_erased_at, erasure_reason = $_reason, modified_date = $_erased_at"
        + " WHERE subject_user_id INSIDE $_users AND " + TenantPredicate + " AND erased_at = NONE"
        + " RETURN VALUE id; COMMIT TRANSACTION;";

    private readonly ISurrealExecutor _executor;
    private readonly ILogger<SurrealKycScreeningStore> _logger;

    public SurrealKycScreeningStore(
        ISurrealExecutor executor,
        ILogger<SurrealKycScreeningStore>? logger = null)
    {
        _executor = executor;
        _logger = logger ?? NullLogger<SurrealKycScreeningStore>.Instance;
    }

    // â”€â”€ Results â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <inheritdoc />
    public async Task<AZOAResult<KycScreeningResult>> SaveResultAsync(
        KycScreeningResult result,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var body = ToStorage(result);
        var scoped = !string.IsNullOrWhiteSpace(result.SubjectUserId);
        var query = scoped ? KycEvidenceFence.Mutation(result.SubjectUserId, result.TenantId, SurrealWriter.Upsert(body))
            : SurrealWriter.Upsert(body);

        var response = await KycEvidenceFence.ExecuteAsync(_executor, query, ct);
        response.EnsureAllOk();
        var saved = response.GetValues<KycScreeningResult>(scoped ? 2 : 0).FirstOrDefault();
        return AZOAResult<KycScreeningResult>.Success(
            saved is null ? result : FromStorage(saved),
            "Saved.");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<KycScreeningResult>> GetLatestResultAsync(
        Guid userId,
        Guid? tenantId,
        CancellationToken ct = default, string? lineageKey = null)
    {
        var user = SurrealLink.ToLink("user", userId.ToString("N"));
        var tenant = tenantId.HasValue && tenantId.Value != Guid.Empty
            ? SurrealLink.ToLink("user", tenantId.Value.ToString("N")) : null;
        var query = SurrealQuery<KycScreeningResult>.From()
            .Where(row => row.SubjectUserId == user);
        query = tenant is null ? query.Where(row => row.TenantId == null) : query.Where(row => row.TenantId == tenant);
        if (lineageKey is not null)
            query = query.Where(row => row.LineageKey == lineageKey);
        var latest = query.OrderByDescending(row => row.ScreenedAt).Limit(1).AsUntyped();

        var row = await _executor.QuerySingleAsync<KycScreeningResult>(latest, ct);
        return new AZOAResult<KycScreeningResult>
        {
            Message = row is null ? "No screening result found." : "Success",
            Result = row is null ? null : FromStorage(row),
        };
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<KycScreeningResult>>> GetResultsForUserAsync(
        Guid userId,
        Guid? tenantId,
        int limit,
        CancellationToken ct = default)
    {
        var user = SurrealLink.ToLink("user", userId.ToString("N"));
        var tenant = tenantId.HasValue && tenantId.Value != Guid.Empty
            ? SurrealLink.ToLink("user", tenantId.Value.ToString("N")) : null;
        var query = SurrealQuery<KycScreeningResult>.From()
            .Where(row => row.SubjectUserId == user);
        query = tenant is null ? query.Where(row => row.TenantId == null) : query.Where(row => row.TenantId == tenant);

        var rows = await _executor.QueryAsync<KycScreeningResult>(query.OrderByDescending(row => row.ScreenedAt).Limit(Math.Clamp(limit, 1, 500)), ct);
        return AZOAResult<IReadOnlyList<KycScreeningResult>>.Success(
            rows.Select(FromStorage).ToList());
    }

    // â”€â”€ Monitors â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <inheritdoc />
    public async Task<AZOAResult<KycScreeningMonitor>> SaveMonitorAsync(
        KycScreeningMonitor monitor,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var body = ToStorage(monitor);
        var query = SurrealWriter.Upsert(body);

        var response = await KycEvidenceFence.ExecuteAsync(_executor, query, ct);
        response.EnsureAllOk();
        var saved = response.GetValues<KycScreeningMonitor>(0).FirstOrDefault();
        return AZOAResult<KycScreeningMonitor>.Success(
            saved is null ? monitor : FromStorage(saved),
            "Saved.");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<KycScreeningMonitor>> GetMonitorByQueryAsync(
        string tenantScope,
        string? queryFullName,
        string? queryBirthdayTimeFrom,
        CancellationToken ct = default, string? lineageKey = null)
    {
        if (string.IsNullOrWhiteSpace(tenantScope))
        {
            // A scopeless lookup would resolve to whichever tenant registered first.
            return AZOAResult<KycScreeningMonitor>.Failure("A uniqueness scope is required.");
        }

        var birthday = queryBirthdayTimeFrom ?? string.Empty;
        var query = SurrealQuery<KycScreeningMonitor>.From()
            .Where(row => row.TenantScope == tenantScope && row.QueryFullName == queryFullName
                && row.QueryBirthdayTimeFrom == birthday && row.LineageKey == lineageKey)
            .Limit(1).AsUntyped();

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

    /// <inheritdoc />
    public async Task<AZOAResult<string>> GetProviderMonitorIdByQueryAsync(
        string? queryFullName,
        string? queryBirthdayTimeFrom,
        CancellationToken ct = default)
    {
        var birthday = queryBirthdayTimeFrom ?? string.Empty;
        var query = SurrealQuery<KycScreeningMonitor>.From()
            .Where(row => row.QueryFullName == queryFullName && row.QueryBirthdayTimeFrom == birthday
                && row.State == KycMonitorState.ACTIVE)
            .Select(row => new { row.ProviderMonitorId }).Limit(1).AsUntyped();
        var row = await _executor.QuerySingleAsync<KycScreeningMonitor>(query, ct);
        var value = row?.ProviderMonitorId;
        return new AZOAResult<string>
        {
            Message = string.IsNullOrWhiteSpace(value) ? "No upstream monitor known." : "Success",
            Result = string.IsNullOrWhiteSpace(value) ? null : value,
        };
    }

    /// <inheritdoc />
    public async Task<AZOAResult<IReadOnlyList<KycScreeningMonitor>>> GetMonitorsByProviderIdAsync(
        string providerMonitorId,
        CancellationToken ct = default)
    {
        var query = SurrealQuery<KycScreeningMonitor>.From()
            .Where(row => row.ProviderMonitorId == providerMonitorId)
            .OrderBy(row => row.CreatedDate).AsUntyped();

        var rows = await _executor.QueryAsync<KycScreeningMonitor>(query, ct);
        return AZOAResult<IReadOnlyList<KycScreeningMonitor>>.Success(
            rows.Select(FromStorage).ToList());
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> MarkMonitorRemovedAsync(
        string tenantScope,
        string providerMonitorId,
        DateTimeOffset removedAt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenantScope))
            return AZOAResult<bool>.Failure("A uniqueness scope is required.");

        var monitors = await _executor.QueryAsync<KycScreeningMonitor>(
            SurrealQuery<KycScreeningMonitor>.From()
                .Where(row => row.TenantScope == tenantScope && row.ProviderMonitorId == providerMonitorId)
                .AsUntyped(), ct);
        foreach (var monitor in monitors)
        {
            var query = SurrealWriter.UpdateOnly<KycScreeningMonitor>(monitor.Id)
                .Where(row => row.TenantScope == tenantScope && row.ProviderMonitorId == providerMonitorId)
                .Set(row => row.State, KycMonitorState.REMOVED)
                .Set(row => row.RemovedAt, removedAt.ToUniversalTime())
                .Set(row => row.ModifiedDate, removedAt.ToUniversalTime()).Build();
            (await KycEvidenceFence.ExecuteAsync(_executor, query, ct)).EnsureAllOk();
        }
        return AZOAResult<bool>.Success(true);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> TouchMonitorPolledAsync(
        string monitorId,
        DateTimeOffset polledAt,
        CancellationToken ct = default)
    {
        var query = SurrealWriter.UpdateOnly<KycScreeningMonitor>(monitorId)
            .Where(row => row.State == KycMonitorState.ACTIVE)
            .Set(row => row.LastPolledAt, polledAt.ToUniversalTime()).Build();

        var response = await KycEvidenceFence.ExecuteAsync(_executor, query, ct);
        response.EnsureAllOk();
        return AZOAResult<bool>.Success(true);
    }

    /// <inheritdoc />
    public async Task<AZOAResult<bool>> RecordMonitorFollowUpAsync(
        string monitorId,
        string? observedUpdateDate,
        DateTimeOffset followedUpAt,
        CancellationToken ct = default)
    {
        var update = SurrealWriter.UpdateOnly<KycScreeningMonitor>(monitorId)
            .Where(row => row.State == KycMonitorState.ACTIVE)
            .Set(row => row.LastFollowUpAt, followedUpAt.ToUniversalTime())
            .Set(row => row.LastPolledAt, followedUpAt.ToUniversalTime())
            .Set(row => row.ModifiedDate, followedUpAt.ToUniversalTime());
        if (observedUpdateDate is null) update = update.Unset(row => row.LastObservedUpdateDate);
        else update = update.Set(row => row.LastObservedUpdateDate, observedUpdateDate);
        var query = update.Build();

        var response = await KycEvidenceFence.ExecuteAsync(_executor, query, ct);
        response.EnsureAllOk();
        return AZOAResult<bool>.Success(true);
    }

    // â”€â”€ Retention + erasure (D25) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <inheritdoc />
    public async Task<AZOAResult<int>> EraseSubjectAsync(
        IReadOnlyList<string> userIds,
        Guid? tenantId,
        string reason,
        DateTimeOffset erasedAt,
        CancellationToken ct = default)
    {
        if (userIds is null || userIds.Count == 0)
            return AZOAResult<int>.Success(0);

        // Of() accepts both the bare hex kyc_identity.user_ids holds and an
        // already-prefixed link, so either storage shape binds as a record id.
        var links = userIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => SurrealRecordParam.Of("user", id))
            .Where(link => link is not null)
            .ToArray();
        if (links.Length == 0)
            return AZOAResult<int>.Success(0);

        var erasedAtUtc = erasedAt.ToUniversalTime();
        var hasTenant = tenantId.HasValue && tenantId.Value != Guid.Empty;
        var tenantLink = hasTenant
            ? SurrealRecordParam.Of("user", tenantId!.Value.ToString("N"))
            : null;

        // Explicit SET: the verdict, the provider ids, the scope, and the audit
        // timestamps survive. Only the two retained personal fields are tombstoned.
        // raw: atomically erase both AML tables; see AGENTS.md AML persistence.
        var statements = EraseSubjectQuery.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(SurrealQuery.Of).ToList();
        statements.Insert(1, KycEvidenceFence.Create(userIds, tenantId?.ToString("N")));
        var query = SurrealQuery.Combine(statements.ToArray())
            .WithParam("_result_t", KycScreeningResult.SchemaNameConst)
            .WithParam("_monitor_t", KycScreeningMonitor.SchemaNameConst)
            .WithParam("_users", links)
            .WithParam("_has_tenant", hasTenant)
            .WithParam("_tenant", tenantLink)
            .WithParam("_erased_at", erasedAtUtc)
            .WithParam("_reason", reason);

        var response = await KycEvidenceFence.ExecuteAsync(_executor, query, ct);
        response.EnsureAllOk();
        var erased = response.GetValues<string>(2).Count() + response.GetValues<string>(3).Count();
        return AZOAResult<int>.Success(erased, "Erased.");
    }

    /// <inheritdoc />
    public async Task<AZOAResult<int>> EraseRetentionExpiredAsync(
        DateTimeOffset asOf,
        string reason,
        int limit,
        CancellationToken ct = default)
    {
        var asOfUtc = asOf.ToUniversalTime();
        var bound = Math.Clamp(limit, 1, 1000);
        var results = await _executor.QueryAsync<KycScreeningResult>(SurrealQuery<KycScreeningResult>.From()
            .Where(row => row.ErasedAt == null && row.RetentionExpiresAt != null && row.RetentionExpiresAt <= asOfUtc)
            .OrderBy(row => row.RetentionExpiresAt!).Limit(bound), ct);
        var monitors = await _executor.QueryAsync<KycScreeningMonitor>(SurrealQuery<KycScreeningMonitor>.From()
            .Where(row => row.ErasedAt == null && row.RetentionExpiresAt != null && row.RetentionExpiresAt <= asOfUtc)
            .OrderBy(row => row.RetentionExpiresAt!).Limit(bound), ct);
        if (results.Count == 0 && monitors.Count == 0) return AZOAResult<int>.Success(0);
        // raw: subject evidence fences and both bounded erasure batches commit atomically; see AGENTS.md.
        var statements = new List<SurrealQuery> { SurrealQuery.Of("BEGIN"), KycEvidenceFence.CreateScopes(
            results.Select(row => (row.SubjectUserId, row.TenantId)).Concat(monitors.Select(row => (row.SubjectUserId, row.TenantId)))) };
        foreach (var result in results)
        {
            var update = SurrealWriter.UpdateOnly<KycScreeningResult>(result.Id)
                .Where(row => row.ErasedAt == null && row.RetentionExpiresAt == result.RetentionExpiresAt)
                .Unset(row => row.QueryFullName).Unset(row => row.QueryBirthdayTimeFrom)
                .Set(row => row.ErasedAt, asOfUtc).Set(row => row.ErasureReason, reason).Set(row => row.ModifiedDate, asOfUtc);
            update = result.TenantId is null ? update.Where(row => row.TenantId == null) : update.Where(row => row.TenantId == result.TenantId);
            update = result.SubjectUserId is null ? update.Where(row => row.SubjectUserId == null) : update.Where(row => row.SubjectUserId == result.SubjectUserId);
            statements.Add(SurrealGeneratedStatementScope.Apply(update.Build(), ErasureScope(statements.Count)));
        }
        foreach (var monitor in monitors)
        {
            var update = SurrealWriter.UpdateOnly<KycScreeningMonitor>(monitor.Id)
                .Where(row => row.ErasedAt == null && row.RetentionExpiresAt == monitor.RetentionExpiresAt)
                .Unset(row => row.QueryFullName).Unset(row => row.QueryBirthdayTimeFrom)
                .Set(row => row.ErasedAt, asOfUtc).Set(row => row.ErasureReason, reason).Set(row => row.ModifiedDate, asOfUtc);
            update = monitor.TenantId is null ? update.Where(row => row.TenantId == null) : update.Where(row => row.TenantId == monitor.TenantId);
            update = monitor.SubjectUserId is null ? update.Where(row => row.SubjectUserId == null) : update.Where(row => row.SubjectUserId == monitor.SubjectUserId);
            statements.Add(SurrealGeneratedStatementScope.Apply(update.Build(), ErasureScope(statements.Count)));
        }
        statements.Add(SurrealQuery.Of("COMMIT"));
        var response = await KycEvidenceFence.ExecuteAsync(_executor, SurrealQuery.Combine(statements.ToArray()), ct);
        response.EnsureAllOk();
        var erased = Enumerable.Range(2, results.Count).Sum(index => response.GetValues<KycScreeningResult>(index).Count())
            + Enumerable.Range(2 + results.Count, monitors.Count).Sum(index => response.GetValues<KycScreeningMonitor>(index).Count());
        return AZOAResult<int>.Success(erased, "Erased.");
    }

    // â”€â”€ Poll state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <inheritdoc />
    public async Task<AZOAResult<KycScreeningPollState>> GetPollStateAsync(CancellationToken ct = default)
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

    /// <inheritdoc />
    public Task<AZOAResult<bool>> RecordPollSuccessAsync(
        string? highWaterMarkDate, DateTimeOffset completedAt, int changedCount,
        CancellationToken ct = default) => UpdatePollStateAsync(true, highWaterMarkDate, completedAt, changedCount, ct);

    /// <inheritdoc />
    public Task<AZOAResult<bool>> RecordPollFailureAsync(
        DateTimeOffset failedAt, CancellationToken ct = default) => UpdatePollStateAsync(false, null, failedAt, 0, ct);

    private static string ErasureScope(int index)
        => "erasure_" + string.Concat(index.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(digit => (char)('a' + digit - '0')));

    private async Task<AZOAResult<bool>> UpdatePollStateAsync(
        bool success, string? highWaterMarkDate, DateTimeOffset occurredAt, int changedCount, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var previous = await _executor.QuerySingleAsync<KycScreeningPollState>(
                SurrealQuery<KycScreeningPollState>.Key(KycScreeningPollState.LocalId), ct);
            var at = occurredAt.ToUniversalTime();
            if (previous is null)
            {
                var created = new KycScreeningPollState
                {
                    Id = KycScreeningPollState.LocalId,
                    HighWaterMarkDate = success ? highWaterMarkDate : null,
                    LastPollStartedAt = success ? null : at,
                    LastPollCompletedAt = success ? at : null,
                    LastPollChangedCount = success ? changedCount : 0,
                    ConsecutiveFailures = success ? 0 : 1,
                    BackoffUntil = success ? null : at.Add(FailureBackoff),
                    ModifiedDate = at,
                };
                try
                {
                    (await KycEvidenceFence.ExecuteAsync(_executor, SurrealWriter.Create(created), ct)).EnsureAllOk();
                    return AZOAResult<bool>.Success(true);
                }
                catch (SurrealStatementException exception) when (SurrealIdempotencyLedger.IsUniqueViolation(exception.Message))
                {
                    if (await _executor.QuerySingleAsync<KycScreeningPollState>(
                        SurrealQuery<KycScreeningPollState>.Key(KycScreeningPollState.LocalId), ct) is null) throw;
                    continue;
                }
            }
            var mutation = SurrealWriter.UpdateOnly<KycScreeningPollState>(KycScreeningPollState.LocalId)
                .Where(row => row.ModifiedDate == previous.ModifiedDate
                    && row.ConsecutiveFailures == previous.ConsecutiveFailures
                    && row.HighWaterMarkDate == previous.HighWaterMarkDate)
                .Set(row => row.ModifiedDate, at);
            if (success)
            {
                mutation = mutation.Set(row => row.LastPollCompletedAt, at)
                    .Set(row => row.LastPollChangedCount, (long)changedCount)
                    .Set(row => row.ConsecutiveFailures, 0).Unset(row => row.BackoffUntil);
                if (highWaterMarkDate is null) mutation = mutation.Unset(row => row.HighWaterMarkDate);
                else mutation = mutation.Set(row => row.HighWaterMarkDate, highWaterMarkDate);
            }
            else
            {
                mutation = mutation.Set(row => row.ConsecutiveFailures, checked(previous.ConsecutiveFailures + 1))
                    .Set(row => row.BackoffUntil, at.Add(FailureBackoff))
                    .Set(row => row.LastPollStartedAt, at);
            }
            var response = await KycEvidenceFence.ExecuteAsync(_executor, mutation.Build(), ct);
            response.EnsureAllOk();
            if (response.GetValues<KycScreeningPollState>(0).Any()) return AZOAResult<bool>.Success(true);
        }
        return AZOAResult<bool>.FailureWithCode("Concurrent AML poll state update; retry this cycle.", AzoaErrorCodes.Conflict);
    }

    private static KycScreeningResult ToStorage(KycScreeningResult result) => new()
    {
        Id = result.Id,
        SubjectUserId = string.IsNullOrWhiteSpace(result.SubjectUserId)
            ? null
            : SurrealLink.ToLink("user", result.SubjectUserId),
        TenantId = string.IsNullOrWhiteSpace(result.TenantId)
            ? null
            : SurrealLink.ToLink("user", result.TenantId),
        LineageKey = result.LineageKey,
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
        SubjectUserId = string.IsNullOrWhiteSpace(result.SubjectUserId)
            ? null
            : SurrealLink.FromLink(result.SubjectUserId),
        TenantId = string.IsNullOrWhiteSpace(result.TenantId)
            ? null
            : SurrealLink.FromLink(result.TenantId),
        LineageKey = result.LineageKey,
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
        SubjectUserId = string.IsNullOrWhiteSpace(monitor.SubjectUserId)
            ? null
            : SurrealLink.ToLink("user", monitor.SubjectUserId),
        TenantId = string.IsNullOrWhiteSpace(monitor.TenantId)
            ? null
            : SurrealLink.ToLink("user", monitor.TenantId),
        LineageKey = monitor.LineageKey,
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
        SubjectUserId = string.IsNullOrWhiteSpace(monitor.SubjectUserId)
            ? null
            : SurrealLink.FromLink(monitor.SubjectUserId),
        TenantId = string.IsNullOrWhiteSpace(monitor.TenantId)
            ? null
            : SurrealLink.FromLink(monitor.TenantId),
        LineageKey = monitor.LineageKey,
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
