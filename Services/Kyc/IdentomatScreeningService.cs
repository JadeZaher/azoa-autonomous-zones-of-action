// SPDX-License-Identifier: UNLICENSED

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Kyc;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>
/// AML / sanctions screening, folded into the Identomat adapter rather than
/// deferred (D24). All five endpoints share the <c>/begin</c> host, the camelCase
/// <c>companyKey</c> convention, and — critically — the SAME per-IP request budget.
/// See <c>Services/Kyc/AGENTS.md</c> §aml-screening.
/// </summary>
public interface IIdentomatScreeningService
{
    /// <summary>Runs a one-off screening query and persists the AZOA-derived verdict.</summary>
    Task<AZOAResult<KycScreeningResult>> ScreenAsync(
        Guid subjectUserId,
        Guid? tenantId,
        string fullName,
        string? birthdayTimeFrom,
        CancellationToken ct = default);

    /// <summary>
    /// Registers ongoing monitoring. The provider keys the monitor on the QUERY
    /// SHAPE, so the exact fields are persisted; <c>already-exists</c> is benign
    /// idempotent success, not a failure.
    /// </summary>
    Task<AZOAResult<KycScreeningMonitor>> RegisterMonitorAsync(
        Guid subjectUserId,
        Guid? tenantId,
        string fullName,
        string? birthdayTimeFrom,
        CancellationToken ct = default);

    /// <summary>
    /// Unregisters one tenant's monitoring. The upstream registration is removed only
    /// when no other tenant still shares it.
    /// </summary>
    Task<AZOAResult<bool>> RemoveMonitorAsync(
        Guid? tenantId,
        string providerMonitorId,
        CancellationToken ct = default);

    /// <summary>
    /// ONE batched, paginated poll cycle. Never one call per monitored identity: the
    /// shared budget makes a per-identity loop a self-inflicted outage.
    /// </summary>
    Task<AZOAResult<KycMonitorPollReport>> PollMonitoringAsync(CancellationToken ct = default);
}

/// <summary>Outcome of a single batched monitoring poll cycle.</summary>
public sealed class KycMonitorPollReport
{
    public int RecordsExamined { get; init; }
    public int RecordsChanged { get; init; }
    public int FollowUpsPerformed { get; init; }
    public int PagesWalked { get; init; }
    public bool StoppedOnBudget { get; init; }

    /// <summary>True when a persistence read failed mid-cycle and the cycle aborted.</summary>
    public bool AbortedOnStoreError { get; init; }

    public string? NextHighWaterMarkDate { get; init; }
}

/// <inheritdoc />
public sealed class IdentomatScreeningService : IIdentomatScreeningService
{
    /// <summary>Vendor's own dedup signal on re-registration; benign, not an error.</summary>
    public const string AlreadyExistsArgumentError = "already-exists";

    private const string SearchPath = "/search-screening-person";
    private const string MonitorRegisterPath = "/monitor-screening-query";
    private const string MonitorRemovePath = "/remove-screening-query-from-monitoring";
    private const string MonitorListPath = "/list-screening-monitoring-records";

    private const string UnconfiguredMessage =
        "The Identomat screening adapter is not fully configured.";
    private const string QuotaMessage =
        "This tenant's AML screening quota is exhausted.";

    private readonly IdentomatKycSettings _settings;
    private readonly KycIdentitySettings _identitySettings;
    private readonly IIdentomatHttpTransport _transport;
    private readonly IKycScreeningRiskPolicy _policy;
    private readonly IKycScreeningStore _store;
    private readonly IKycTenantQuota _quota;
    private readonly ILogger<IdentomatScreeningService> _logger;
    private readonly TimeProvider _clock;

    public IdentomatScreeningService(
        IOptions<KycSettings> options,
        IIdentomatHttpTransport transport,
        IKycScreeningRiskPolicy policy,
        IKycScreeningStore store,
        IKycTenantQuota quota,
        ILogger<IdentomatScreeningService>? logger = null,
        TimeProvider? clock = null)
    {
        _settings = options.Value.Identomat;
        _identitySettings = options.Value.Identity;
        _transport = transport;
        _policy = policy;
        _store = store;
        _quota = quota;
        _logger = logger ?? NullLogger<IdentomatScreeningService>.Instance;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<AZOAResult<KycScreeningResult>> ScreenAsync(
        Guid subjectUserId,
        Guid? tenantId,
        string fullName,
        string? birthdayTimeFrom,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            return AZOAResult<KycScreeningResult>.Failure(UnconfiguredMessage);
        if (string.IsNullOrWhiteSpace(fullName))
            return AZOAResult<KycScreeningResult>.Failure("A full name is required to screen.");
        if (!_quota.TryConsume(tenantId, KycQuotaOperation.Screening))
        {
            return new AZOAResult<KycScreeningResult>
            {
                IsError = true,
                Message = QuotaMessage,
                Code = AzoaErrorCodes.OperationNotAllowed,
                RetryAfterSeconds = _quota.RetryAfterSeconds,
            };
        }

        var threshold = _policy.ScoreThreshold;
        var assessment = await SearchAsync(fullName, birthdayTimeFrom, threshold, ct);
        if (assessment.IsError || assessment.Result is null)
        {
            return AZOAResult<KycScreeningResult>.FailureWithCode(
                assessment.Message,
                assessment.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        var now = _clock.GetUtcNow();
        var record = new KycScreeningResult
        {
            Id = Guid.NewGuid().ToString("N"),
            SubjectUserId = subjectUserId == Guid.Empty ? null : subjectUserId.ToString("N"),
            TenantId = tenantId is null || tenantId == Guid.Empty ? null : tenantId.Value.ToString("N"),
            QueryFullName = fullName.Trim(),
            QueryBirthdayTimeFrom = string.IsNullOrWhiteSpace(birthdayTimeFrom) ? null : birthdayTimeFrom.Trim(),
            // Persisted because the provider applies it server-side and never echoes
            // it per hit: without it, this row cannot be explained later.
            ScoreThreshold = threshold,
            Decision = assessment.Result.Decision,
            TopScore = assessment.Result.TopScore,
            HitCount = assessment.Result.HitCount,
            Topics = assessment.Result.Topics,
            PersonIds = assessment.Result.PersonIds,
            PolicyVersion = _policy.PolicyVersion,
            ResultsTruncated = assessment.Result.ResultsTruncated,
            RetentionExpiresAt = now.AddDays(RetentionDays),
            ScreenedAt = now,
            CreatedDate = now,
            ModifiedDate = now,
        };

        return await _store.SaveResultAsync(record, ct);
    }

    public async Task<AZOAResult<KycScreeningMonitor>> RegisterMonitorAsync(
        Guid subjectUserId,
        Guid? tenantId,
        string fullName,
        string? birthdayTimeFrom,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            return AZOAResult<KycScreeningMonitor>.Failure(UnconfiguredMessage);
        if (string.IsNullOrWhiteSpace(fullName))
            return AZOAResult<KycScreeningMonitor>.Failure("A full name is required to monitor.");
        if (!_quota.TryConsume(tenantId, KycQuotaOperation.Screening))
        {
            return new AZOAResult<KycScreeningMonitor>
            {
                IsError = true,
                Message = QuotaMessage,
                Code = AzoaErrorCodes.OperationNotAllowed,
                RetryAfterSeconds = _quota.RetryAfterSeconds,
            };
        }

        var scope = KycPersonKeyDeriver.ScopeValue(tenantId);
        var canonicalName = fullName.Trim();
        var canonicalBirthday = string.IsNullOrWhiteSpace(birthdayTimeFrom)
            ? string.Empty
            : birthdayTimeFrom.Trim();

        var response = await _transport
            .PostAsync<IdentomatMonitorRegisterRequest, IdentomatMonitorRegisterResponse>(
                _settings.BeginBaseUrl!,
                MonitorRegisterPath,
                new IdentomatMonitorRegisterRequest
                {
                    CompanyKey = _settings.CompanyKey!.Trim(),
                    Query = new IdentomatScreeningQuery
                    {
                        FullName = canonicalName,
                        BirthdayTimeFrom = canonicalBirthday.Length == 0 ? null : canonicalBirthday,
                        // The register call accepts no threshold; sending one would be
                        // an invented field.
                        ScoreThreshold = null,
                    },
                },
                "monitor-register",
                ct);
        if (response.IsError || response.Result is null)
        {
            return AZOAResult<KycScreeningMonitor>.FailureWithCode(
                response.Message,
                response.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        var alreadyExists = IsAlreadyExists(response.Result.ArgumentError);
        if (!alreadyExists && !string.IsNullOrWhiteSpace(response.Result.ArgumentError))
        {
            _logger.LogError(
                "Identomat monitor registration was refused with a non-idempotent argument error.");
            return AZOAResult<KycScreeningMonitor>.FailureWithCode(
                "The AML monitoring registration was refused.",
                AzoaErrorCodes.InvalidRequest);
        }

        var providerMonitorId = response.Result.Result?.Trim();
        if (alreadyExists)
        {
            // Benign idempotent success. THIS TENANT's row is the only one that may
            // be returned: the vendor dedups the query shape across the whole company
            // key, so another tenant's row for the same person is not ours to hand back.
            var existing = await _store.GetMonitorByQueryAsync(scope, canonicalName, canonicalBirthday, ct);
            if (existing.IsError)
            {
                return AZOAResult<KycScreeningMonitor>.FailureWithCode(
                    existing.Message,
                    existing.Code ?? AzoaErrorCodes.DependencyUnavailable);
            }

            if (existing.Result is not null)
            {
                existing.Result.RegisteredIdempotently = true;
                return AZOAResult<KycScreeningMonitor>.Success(existing.Result);
            }

            // No row in THIS scope. One upstream monitor legitimately backs one row
            // per tenant, so recover the id another scope already holds. Only the
            // opaque id crosses over — never a row, an user, or a tenant.
            if (string.IsNullOrWhiteSpace(providerMonitorId))
            {
                var shared = await _store.GetProviderMonitorIdByQueryAsync(canonicalName, canonicalBirthday, ct);
                if (shared.IsError)
                {
                    return AZOAResult<KycScreeningMonitor>.FailureWithCode(
                        shared.Message,
                        shared.Code ?? AzoaErrorCodes.DependencyUnavailable);
                }

                providerMonitorId = shared.Result?.Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(providerMonitorId))
        {
            if (alreadyExists)
            {
                // The provider confirms the monitor exists but returns no id, and no
                // scope on this node holds one. Surface it rather than inventing an id
                // we would later fail to remove.
                return AZOAResult<KycScreeningMonitor>.FailureWithCode(
                    "The AML monitor already exists upstream but is not reconciled locally.",
                    AzoaErrorCodes.Conflict);
            }

            return AZOAResult<KycScreeningMonitor>.FailureWithCode(
                "The AML monitoring registration returned no monitor id.",
                AzoaErrorCodes.DependencyUnavailable);
        }

        var now = _clock.GetUtcNow();
        return await _store.SaveMonitorAsync(
            new KycScreeningMonitor
            {
                Id = Guid.NewGuid().ToString("N"),
                ProviderMonitorId = providerMonitorId,
                TenantScope = scope,
                SubjectUserId = subjectUserId == Guid.Empty ? null : subjectUserId.ToString("N"),
                TenantId = tenantId is null || tenantId == Guid.Empty ? null : tenantId.Value.ToString("N"),
                QueryFullName = canonicalName,
                QueryBirthdayTimeFrom = canonicalBirthday,
                FollowUpScoreThreshold = _policy.ScoreThreshold,
                State = KycMonitorState.ACTIVE,
                RegisteredIdempotently = alreadyExists,
                RetentionExpiresAt = now.AddDays(RetentionDays),
                RegisteredAt = now,
                CreatedDate = now,
                ModifiedDate = now,
            },
            ct);
    }

    public async Task<AZOAResult<bool>> RemoveMonitorAsync(
        Guid? tenantId,
        string providerMonitorId,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            return AZOAResult<bool>.Failure(UnconfiguredMessage);
        if (string.IsNullOrWhiteSpace(providerMonitorId))
            return AZOAResult<bool>.Failure("A monitor id is required.");

        var scope = KycPersonKeyDeriver.ScopeValue(tenantId);
        var trimmedId = providerMonitorId.Trim();

        var siblings = await _store.GetMonitorsByProviderIdAsync(trimmedId, ct);
        if (siblings.IsError)
        {
            return AZOAResult<bool>.FailureWithCode(
                siblings.Message,
                siblings.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        var rows = siblings.Result ?? [];
        var mine = rows.Where(row => string.Equals(row.TenantScope, scope, StringComparison.Ordinal)).ToList();
        if (mine.Count == 0)
        {
            // Not this scope's monitor. Refusing without saying whose it is keeps the
            // answer identical to "no such monitor".
            return AZOAResult<bool>.Failure("No AML monitor matched.");
        }

        var othersStillActive = rows.Any(row =>
            !string.Equals(row.TenantScope, scope, StringComparison.Ordinal)
            && row.State == KycMonitorState.ACTIVE);

        var marked = await _store.MarkMonitorRemovedAsync(scope, trimmedId, _clock.GetUtcNow(), ct);
        if (marked.IsError)
            return AZOAResult<bool>.FailureWithCode(marked.Message, marked.Code ?? AzoaErrorCodes.DependencyUnavailable);

        if (othersStillActive)
        {
            // One upstream registration serves every tenant that asked for this query
            // shape. Unregistering it here would silently stop another tenant's
            // sanctions monitoring.
            return AZOAResult<bool>.Success(true, "Removed for this scope; the shared upstream monitor stays.");
        }

        var response = await _transport
            .PostAsync<IdentomatMonitorRemoveRequest, IdentomatMonitorRemoveResponse>(
                _settings.BeginBaseUrl!,
                MonitorRemovePath,
                new IdentomatMonitorRemoveRequest
                {
                    CompanyKey = _settings.CompanyKey!.Trim(),
                    Id = trimmedId,
                },
                "monitor-remove",
                ct);
        if (response.IsError || response.Result is null)
        {
            return AZOAResult<bool>.FailureWithCode(
                response.Message,
                response.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        return AZOAResult<bool>.Success(response.Result.Result);
    }

    public async Task<AZOAResult<KycMonitorPollReport>> PollMonitoringAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return AZOAResult<KycMonitorPollReport>.Failure(UnconfiguredMessage);

        var stateResult = await _store.GetPollStateAsync(ct);
        if (stateResult.IsError)
        {
            return AZOAResult<KycMonitorPollReport>.FailureWithCode(
                stateResult.Message,
                stateResult.Code ?? AzoaErrorCodes.DependencyUnavailable);
        }

        var now = _clock.GetUtcNow();
        var state = stateResult.Result ?? new KycScreeningPollState { Id = KycScreeningPollState.LocalId };
        if (state.BackoffUntil is not null && state.BackoffUntil > now)
        {
            return AZOAResult<KycMonitorPollReport>.Success(new KycMonitorPollReport
            {
                StoppedOnBudget = true,
                NextHighWaterMarkDate = state.HighWaterMarkDate,
            });
        }

        var pageSize = Math.Clamp(_settings.Screening.MonitorPollPageSize, 1, 500);
        var maxPages = Math.Clamp(_settings.Screening.MonitorPollMaxPagesPerCycle, 1, 100);
        var examined = 0;
        var changed = 0;
        var followUps = 0;
        var pages = 0;
        var stoppedOnBudget = false;
        var abortedOnStoreError = false;
        var highWaterMark = state.HighWaterMarkDate;
        var start = 0;

        while (pages < maxPages)
        {
            // ONE batched, paginated call per page — never one call per identity.
            var page = await _transport
                .PostAsync<IdentomatMonitorListRequest, IdentomatMonitorListResponse>(
                    _settings.BeginBaseUrl!,
                    MonitorListPath,
                    new IdentomatMonitorListRequest
                    {
                        CompanyKey = _settings.CompanyKey!.Trim(),
                        Start = start,
                        Limit = pageSize,
                        Date = state.HighWaterMarkDate,
                    },
                    "monitor-list",
                    ct);
            if (page.IsError || page.Result?.Result is null)
            {
                stoppedOnBudget = true;
                await _store.RecordPollFailureAsync(now, ct);
                break;
            }

            pages++;
            var records = page.Result.Result.Data ?? [];
            examined += records.Count;
            var pageHighWaterMark = highWaterMark;

            foreach (var record in records)
            {
                if (string.IsNullOrWhiteSpace(record.Id))
                    continue;

                var known = await _store.GetMonitorsByProviderIdAsync(record.Id.Trim(), ct);
                if (known.IsError)
                {
                    // A STORE error is not "not ours". Advancing the high-water mark
                    // past a record we could not classify filters it out of every
                    // future cycle, so a sanctions change on it would never be seen
                    // again. Abort instead, leaving the mark where it was.
                    abortedOnStoreError = true;
                    break;
                }

                // An ERASED row has no query shape left to search with (D25 tombstoned
                // it), so polling it would send the vendor an empty fullName.
                var rows = (known.Result ?? [])
                    .Where(row => row.State == KycMonitorState.ACTIVE
                        && row.ErasedAt is null
                        && !string.IsNullOrWhiteSpace(row.QueryFullName))
                    .ToList();
                if (rows.Count == 0)
                    continue;

                // The list call returns only fullName/id/lastUpdatedDate, so a changed
                // timestamp is the ONLY change signal and a follow-up fetch is
                // mandatory to learn what actually changed.
                var advanced = rows
                    .Where(row => HasAdvanced(row.LastObservedUpdateDate, record.LastUpdatedDate))
                    .ToList();
                if (advanced.Count == 0)
                {
                    foreach (var row in rows)
                        await _store.TouchMonitorPolledAsync(row.Id, now, ct);
                    continue;
                }

                changed++;
                pageHighWaterMark = Later(pageHighWaterMark, record.LastUpdatedDate);

                // ONE upstream search for the shared query shape, fanned out to every
                // tenant row that registered it. A per-tenant search would multiply the
                // shared per-IP budget by the number of tenants watching one person.
                var template = advanced[0];
                var followUp = await SearchAsync(
                    template.QueryFullName ?? string.Empty,
                    string.IsNullOrWhiteSpace(template.QueryBirthdayTimeFrom)
                        ? null
                        : template.QueryBirthdayTimeFrom,
                    template.FollowUpScoreThreshold > 0
                        ? template.FollowUpScoreThreshold
                        : _policy.ScoreThreshold,
                    ct);
                if (followUp.IsError || followUp.Result is null)
                {
                    // Out of budget mid-cycle: stop cleanly and resume next cycle
                    // rather than hammering into a one-hour IP ban.
                    stoppedOnBudget = true;
                    break;
                }

                followUps++;
                foreach (var row in advanced)
                    await PersistFollowUpAsync(row, followUp.Result, record.LastUpdatedDate, now, ct);
            }

            if (stoppedOnBudget || abortedOnStoreError)
                break;

            // The mark only moves for a page walked end to end.
            highWaterMark = pageHighWaterMark;

            start += pageSize;
            if (records.Count < pageSize || start >= page.Result.Result.Total)
                break;
        }

        if (abortedOnStoreError)
            await _store.RecordPollFailureAsync(now, ct);

        var report = new KycMonitorPollReport
        {
            RecordsExamined = examined,
            RecordsChanged = changed,
            FollowUpsPerformed = followUps,
            PagesWalked = pages,
            StoppedOnBudget = stoppedOnBudget,
            AbortedOnStoreError = abortedOnStoreError,
            NextHighWaterMarkDate = highWaterMark,
        };

        if (!stoppedOnBudget && !abortedOnStoreError)
            await _store.RecordPollSuccessAsync(highWaterMark, now, changed, ct);

        return AZOAResult<KycMonitorPollReport>.Success(report);
    }

    /// <summary>True when a monitoring record's timestamp moved past what we last saw.</summary>
    public static bool HasAdvanced(string? lastObserved, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        if (string.IsNullOrWhiteSpace(lastObserved))
            return true;
        return TryParse(candidate, out var candidateTime) && TryParse(lastObserved, out var observedTime)
            ? candidateTime > observedTime
            : !string.Equals(candidate.Trim(), lastObserved.Trim(), StringComparison.Ordinal);
    }

    private bool IsConfigured
        => !string.IsNullOrWhiteSpace(_settings.CompanyKey)
            && !string.IsNullOrWhiteSpace(_settings.BeginBaseUrl);

    private int RetentionDays => Math.Clamp(_identitySettings.RetentionDays, 1, 3650);

    private static bool IsAlreadyExists(string? argumentError)
        => string.Equals(
            argumentError?.Trim(),
            AlreadyExistsArgumentError,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pages the sanctions search to exhaustion rather than reading only the first
    /// window. The response carries no <c>total</c> and the vendor documents no sort
    /// order, so a single unpaged window silently drops every hit past it AND cannot
    /// tell a clean subject from a saturated one. When the page budget runs out with
    /// a full final page the result is marked truncated, and a truncated result may
    /// never read as a pass.
    /// </summary>
    private async Task<AZOAResult<KycScreeningAssessment>> SearchAsync(
        string fullName,
        string? birthdayTimeFrom,
        int threshold,
        CancellationToken ct)
    {
        var pageSize = Math.Clamp(_settings.Screening.SearchPageSize, 1, 500);
        var maxPages = Math.Clamp(_settings.Screening.SearchMaxPages, 1, 50);
        var hits = new List<IdentomatScreeningHit>();
        var truncated = false;

        for (var page = 0; page < maxPages; page++)
        {
            var response = await _transport
                .PostAsync<IdentomatScreeningSearchRequest, IdentomatScreeningSearchResponse>(
                    _settings.BeginBaseUrl!,
                    SearchPath,
                    new IdentomatScreeningSearchRequest
                    {
                        CompanyKey = _settings.CompanyKey!.Trim(),
                        Query = new IdentomatScreeningQuery
                        {
                            FullName = fullName.Trim(),
                            BirthdayTimeFrom = string.IsNullOrWhiteSpace(birthdayTimeFrom)
                                ? null
                                : birthdayTimeFrom.Trim(),
                            ScoreThreshold = threshold,
                        },
                        Offset = page * pageSize,
                        Limit = pageSize,
                    },
                    "screening-search",
                    ct);
            if (response.IsError || response.Result is null)
            {
                // A later page failing is NOT a partial pass: the hits already
                // collected may be the harmless prefix of a list whose adverse
                // entries are still unread.
                return AZOAResult<KycScreeningAssessment>.FailureWithCode(
                    response.Message,
                    response.Code ?? AzoaErrorCodes.DependencyUnavailable);
            }

            if (!string.IsNullOrWhiteSpace(response.Result.ArgumentError))
            {
                _logger.LogError("Identomat screening search was refused with an argument error.");
                return AZOAResult<KycScreeningAssessment>.FailureWithCode(
                    "The AML screening query was refused.",
                    AzoaErrorCodes.InvalidRequest);
            }

            var window = response.Result.Result ?? [];
            hits.AddRange(window);
            if (window.Count < pageSize)
                return AZOAResult<KycScreeningAssessment>.Success(_policy.Assess(hits));

            truncated = page == maxPages - 1;
        }

        if (truncated)
        {
            _logger.LogWarning(
                "An AML screening search saturated {MaxPages} page(s) of {PageSize}; the verdict is forced to manual review.",
                maxPages,
                pageSize);
        }

        return AZOAResult<KycScreeningAssessment>.Success(_policy.Assess(hits, truncated));
    }

    private async Task PersistFollowUpAsync(
        KycScreeningMonitor monitor,
        KycScreeningAssessment assessment,
        string? observedUpdateDate,
        DateTimeOffset now,
        CancellationToken ct)
    {
        await _store.SaveResultAsync(
            new KycScreeningResult
            {
                Id = Guid.NewGuid().ToString("N"),
                SubjectUserId = monitor.SubjectUserId,
                TenantId = monitor.TenantId,
                QueryFullName = monitor.QueryFullName,
                QueryBirthdayTimeFrom = string.IsNullOrWhiteSpace(monitor.QueryBirthdayTimeFrom)
                    ? null
                    : monitor.QueryBirthdayTimeFrom,
                ScoreThreshold = monitor.FollowUpScoreThreshold > 0
                    ? monitor.FollowUpScoreThreshold
                    : _policy.ScoreThreshold,
                Decision = assessment.Decision,
                TopScore = assessment.TopScore,
                HitCount = assessment.HitCount,
                Topics = assessment.Topics,
                PersonIds = assessment.PersonIds,
                MonitorId = monitor.Id,
                PolicyVersion = _policy.PolicyVersion,
                ResultsTruncated = assessment.ResultsTruncated,
                RetentionExpiresAt = now.AddDays(RetentionDays),
                ScreenedAt = now,
                CreatedDate = now,
                ModifiedDate = now,
            },
            ct);

        await _store.RecordMonitorFollowUpAsync(monitor.Id, observedUpdateDate, now, ct);
    }

    private static string? Later(string? current, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return current;
        if (string.IsNullOrWhiteSpace(current))
            return candidate.Trim();
        return TryParse(candidate, out var candidateTime) && TryParse(current, out var currentTime)
            ? candidateTime > currentTime ? candidate.Trim() : current
            : candidate.Trim();
    }

    private static bool TryParse(string value, out DateTimeOffset parsed)
        => DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out parsed);
}
