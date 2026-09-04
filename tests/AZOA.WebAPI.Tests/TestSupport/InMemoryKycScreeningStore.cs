// SPDX-License-Identifier: UNLICENSED

using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Kyc;

namespace AZOA.WebAPI.Tests.TestSupport;

/// <summary>
/// In-memory <see cref="IKycScreeningStore"/> that reproduces the SurrealDB store's
/// TENANT SCOPING. Every lookup filters on the tenant exactly the way the real
/// predicates do, so a test that would pass against a scopeless fake fails here —
/// which is the whole point, given the cross-tenant leak this store was hardened for.
/// </summary>
public sealed class InMemoryKycScreeningStore : IKycScreeningStore
{
    private readonly List<KycScreeningResult> _results = [];
    private readonly List<KycScreeningMonitor> _monitors = [];

    public IReadOnlyList<KycScreeningResult> SavedResults => _results;
    public IReadOnlyList<KycScreeningMonitor> SavedMonitors => _monitors;

    public KycScreeningPollState? PollState { get; set; }
    public int PollSuccessCount { get; private set; }
    public int PollFailureCount { get; private set; }

    /// <summary>Forces every read to fail, for fail-closed-on-store-error coverage.</summary>
    public bool FailReads { get; set; }

    /// <summary>Fails only the provider-id lookup, which the poll must not treat as "not ours".</summary>
    public bool FailMonitorLookup { get; set; }

    public KycScreeningResult SeedResult(
        Guid avatarId,
        Guid? tenantId,
        KycScreeningDecision decision,
        DateTimeOffset? screenedAt = null,
        bool resultsTruncated = false)
    {
        var result = new KycScreeningResult
        {
            Id = Guid.NewGuid().ToString("N"),
            SubjectAvatarId = avatarId.ToString("N"),
            TenantId = tenantId is null || tenantId == Guid.Empty ? null : tenantId.Value.ToString("N"),
            QueryFullName = "Ada Lovelace",
            QueryBirthdayTimeFrom = "1815-12-10",
            Decision = decision,
            ResultsTruncated = resultsTruncated,
            PolicyVersion = KycScreeningRiskPolicy.CurrentPolicyVersion,
            ScreenedAt = screenedAt ?? DateTimeOffset.UtcNow,
            CreatedDate = screenedAt ?? DateTimeOffset.UtcNow,
        };
        _results.Add(result);
        return result;
    }

    public KycScreeningMonitor SeedMonitor(
        string fullName,
        string birthday,
        string providerMonitorId,
        Guid? tenantId = null,
        Guid? subjectAvatarId = null,
        string? lastObserved = null,
        DateTimeOffset? retentionExpiresAt = null)
    {
        var monitor = new KycScreeningMonitor
        {
            Id = Guid.NewGuid().ToString("N"),
            ProviderMonitorId = providerMonitorId,
            TenantScope = KycPersonKeyDeriver.ScopeValue(tenantId),
            TenantId = tenantId is null || tenantId == Guid.Empty ? null : tenantId.Value.ToString("N"),
            SubjectAvatarId = subjectAvatarId?.ToString("N"),
            QueryFullName = fullName,
            QueryBirthdayTimeFrom = birthday,
            FollowUpScoreThreshold = 80,
            State = KycMonitorState.ACTIVE,
            LastObservedUpdateDate = lastObserved,
            RetentionExpiresAt = retentionExpiresAt,
        };
        _monitors.Add(monitor);
        return monitor;
    }

    public Task<AZOAResult<KycScreeningResult>> SaveResultAsync(
        KycScreeningResult result, CancellationToken ct = default)
    {
        _results.Add(result);
        return Task.FromResult(AZOAResult<KycScreeningResult>.Success(result));
    }

    public Task<AZOAResult<KycScreeningResult>> GetLatestResultAsync(
        Guid avatarId, Guid? tenantId, CancellationToken ct = default)
    {
        if (FailReads)
            return Task.FromResult(AZOAResult<KycScreeningResult>.Failure("store unavailable"));

        var row = MatchingResults(avatarId, tenantId)
            .OrderByDescending(item => item.ScreenedAt)
            .FirstOrDefault();
        return Task.FromResult(new AZOAResult<KycScreeningResult> { Result = row });
    }

    public Task<AZOAResult<IReadOnlyList<KycScreeningResult>>> GetResultsForAvatarAsync(
        Guid avatarId, Guid? tenantId, int limit, CancellationToken ct = default)
    {
        if (FailReads)
            return Task.FromResult(AZOAResult<IReadOnlyList<KycScreeningResult>>.Failure("store unavailable"));

        return Task.FromResult(AZOAResult<IReadOnlyList<KycScreeningResult>>.Success(
            MatchingResults(avatarId, tenantId)
                .OrderByDescending(item => item.ScreenedAt)
                .Take(limit)
                .ToList()));
    }

    public Task<AZOAResult<KycScreeningMonitor>> SaveMonitorAsync(
        KycScreeningMonitor monitor, CancellationToken ct = default)
    {
        _monitors.Add(monitor);
        return Task.FromResult(AZOAResult<KycScreeningMonitor>.Success(monitor));
    }

    public Task<AZOAResult<KycScreeningMonitor>> GetMonitorByQueryAsync(
        string tenantScope,
        string? queryFullName,
        string? queryBirthdayTimeFrom,
        CancellationToken ct = default)
    {
        if (FailReads)
            return Task.FromResult(AZOAResult<KycScreeningMonitor>.Failure("store unavailable"));

        var row = _monitors.FirstOrDefault(monitor =>
            monitor.TenantScope == tenantScope
            && monitor.QueryFullName == queryFullName
            && (monitor.QueryBirthdayTimeFrom ?? string.Empty) == (queryBirthdayTimeFrom ?? string.Empty));
        return Task.FromResult(new AZOAResult<KycScreeningMonitor> { Result = row });
    }

    public Task<AZOAResult<string>> GetProviderMonitorIdByQueryAsync(
        string? queryFullName,
        string? queryBirthdayTimeFrom,
        CancellationToken ct = default)
    {
        if (FailReads)
            return Task.FromResult(AZOAResult<string>.Failure("store unavailable"));

        var id = _monitors
            .Where(monitor => monitor.QueryFullName == queryFullName
                && (monitor.QueryBirthdayTimeFrom ?? string.Empty) == (queryBirthdayTimeFrom ?? string.Empty)
                && monitor.State == KycMonitorState.ACTIVE)
            .Select(monitor => monitor.ProviderMonitorId)
            .FirstOrDefault();
        return Task.FromResult(new AZOAResult<string> { Result = id });
    }

    public Task<AZOAResult<IReadOnlyList<KycScreeningMonitor>>> GetMonitorsByProviderIdAsync(
        string providerMonitorId, CancellationToken ct = default)
    {
        if (FailReads || FailMonitorLookup)
        {
            return Task.FromResult(
                AZOAResult<IReadOnlyList<KycScreeningMonitor>>.Failure("store unavailable"));
        }

        return Task.FromResult(AZOAResult<IReadOnlyList<KycScreeningMonitor>>.Success(
            _monitors.Where(monitor => monitor.ProviderMonitorId == providerMonitorId).ToList()));
    }

    public Task<AZOAResult<bool>> MarkMonitorRemovedAsync(
        string tenantScope,
        string providerMonitorId,
        DateTimeOffset removedAt,
        CancellationToken ct = default)
    {
        foreach (var monitor in _monitors.Where(item =>
                     item.TenantScope == tenantScope && item.ProviderMonitorId == providerMonitorId))
        {
            monitor.State = KycMonitorState.REMOVED;
            monitor.RemovedAt = removedAt;
        }

        return Task.FromResult(AZOAResult<bool>.Success(true));
    }

    public Task<AZOAResult<bool>> TouchMonitorPolledAsync(
        string monitorId, DateTimeOffset polledAt, CancellationToken ct = default)
    {
        var monitor = _monitors.FirstOrDefault(item => item.Id == monitorId);
        if (monitor is not null)
            monitor.LastPolledAt = polledAt;
        return Task.FromResult(AZOAResult<bool>.Success(true));
    }

    public Task<AZOAResult<bool>> RecordMonitorFollowUpAsync(
        string monitorId,
        string? observedUpdateDate,
        DateTimeOffset followedUpAt,
        CancellationToken ct = default)
    {
        var monitor = _monitors.FirstOrDefault(item => item.Id == monitorId);
        if (monitor is not null)
        {
            monitor.LastObservedUpdateDate = observedUpdateDate;
            monitor.LastFollowUpAt = followedUpAt;
        }

        return Task.FromResult(AZOAResult<bool>.Success(true));
    }

    public Task<AZOAResult<int>> EraseSubjectAsync(
        IReadOnlyList<string> avatarIds,
        Guid? tenantId,
        string reason,
        DateTimeOffset erasedAt,
        CancellationToken ct = default)
    {
        var tenantKey = tenantId is null || tenantId == Guid.Empty ? null : tenantId.Value.ToString("N");
        var erased = 0;

        foreach (var result in _results.Where(item =>
                     item.SubjectAvatarId is not null
                     && avatarIds.Contains(item.SubjectAvatarId, StringComparer.Ordinal)
                     && item.TenantId == tenantKey
                     && item.ErasedAt is null))
        {
            Tombstone(result, reason, erasedAt);
            erased++;
        }

        foreach (var monitor in _monitors.Where(item =>
                     item.SubjectAvatarId is not null
                     && avatarIds.Contains(item.SubjectAvatarId, StringComparer.Ordinal)
                     && item.TenantId == tenantKey
                     && item.ErasedAt is null))
        {
            Tombstone(monitor, reason, erasedAt);
            erased++;
        }

        return Task.FromResult(AZOAResult<int>.Success(erased));
    }

    public Task<AZOAResult<int>> EraseRetentionExpiredAsync(
        DateTimeOffset asOf,
        string reason,
        int limit,
        CancellationToken ct = default)
    {
        var erased = 0;

        foreach (var result in _results
                     .Where(item => item.ErasedAt is null
                         && item.RetentionExpiresAt is not null
                         && item.RetentionExpiresAt <= asOf)
                     .Take(limit)
                     .ToList())
        {
            Tombstone(result, reason, asOf);
            erased++;
        }

        foreach (var monitor in _monitors
                     .Where(item => item.ErasedAt is null
                         && item.RetentionExpiresAt is not null
                         && item.RetentionExpiresAt <= asOf)
                     .Take(limit)
                     .ToList())
        {
            Tombstone(monitor, reason, asOf);
            erased++;
        }

        return Task.FromResult(AZOAResult<int>.Success(erased));
    }

    public Task<AZOAResult<KycScreeningPollState>> GetPollStateAsync(CancellationToken ct = default)
        => Task.FromResult(new AZOAResult<KycScreeningPollState> { Result = PollState });

    public Task<AZOAResult<bool>> RecordPollSuccessAsync(
        string? highWaterMarkDate,
        DateTimeOffset completedAt,
        int changedCount,
        CancellationToken ct = default)
    {
        PollSuccessCount++;
        return Task.FromResult(AZOAResult<bool>.Success(true));
    }

    public Task<AZOAResult<bool>> RecordPollFailureAsync(
        DateTimeOffset failedAt, CancellationToken ct = default)
    {
        PollFailureCount++;
        return Task.FromResult(AZOAResult<bool>.Success(true));
    }

    private IEnumerable<KycScreeningResult> MatchingResults(Guid avatarId, Guid? tenantId)
    {
        var avatarKey = avatarId.ToString("N");
        var tenantKey = tenantId is null || tenantId == Guid.Empty ? null : tenantId.Value.ToString("N");
        return _results.Where(item => item.SubjectAvatarId == avatarKey && item.TenantId == tenantKey);
    }

    private static void Tombstone(KycScreeningResult result, string reason, DateTimeOffset erasedAt)
    {
        result.QueryFullName = null;
        result.QueryBirthdayTimeFrom = null;
        result.ErasedAt = erasedAt;
        result.ErasureReason = reason;
    }

    private static void Tombstone(KycScreeningMonitor monitor, string reason, DateTimeOffset erasedAt)
    {
        monitor.QueryFullName = null;
        monitor.QueryBirthdayTimeFrom = null;
        monitor.ErasedAt = erasedAt;
        monitor.ErasureReason = reason;
    }
}
