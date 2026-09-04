// SPDX-License-Identifier: UNLICENSED

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>Operation classes that draw on a tenant's KYC quota.</summary>
public enum KycQuotaOperation
{
    /// <summary>Beginning a hosted verification session.</summary>
    BeginSession,

    /// <summary>An AML/sanctions screening call.</summary>
    Screening
}

/// <summary>
/// Per-tenant call quota in front of the shared provider budget. Without it one
/// consumer's begin-session storm consumes the node-wide per-IP ceiling and evicts
/// every other tenant from KYC — a cross-tenant denial of service, not merely a
/// noisy-neighbour problem. See <c>Services/Kyc/AGENTS.md</c>.
/// </summary>
public interface IKycTenantQuota
{
    /// <summary>Consumes one unit for the tenant, or returns false when exhausted.</summary>
    bool TryConsume(Guid? tenantId, KycQuotaOperation operation);

    /// <summary>Seconds a rejected caller should wait before retrying.</summary>
    int RetryAfterSeconds { get; }
}

/// <inheritdoc />
public sealed class KycTenantQuota : IKycTenantQuota
{
    /// <summary>
    /// Node-direct flows share ONE partition rather than bypassing the quota, so a
    /// direct-user storm cannot drain the shared vendor budget out from under every
    /// tenant. The cost is deliberate and known: direct users contend with each other
    /// rather than each holding an individual allowance. A per-avatar partition would
    /// make the ceiling unbounded in the number of unauthenticated-to-a-tenant users,
    /// which is the exact denial-of-service this class exists to stop.
    /// </summary>
    private const string NodeDirectPartition = "__node_direct__";

    private const int MinimumPermits = 1;
    private const int MaximumPermits = 10_000;
    private const int MinimumWindowSeconds = 1;
    private const int MaximumWindowSeconds = 3_600;
    private const int MinimumReplicas = 1;
    private const int MaximumReplicas = 100;

    private readonly int _beginPermits;
    private readonly int _screeningPermits;
    private readonly int _windowSeconds;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, int> _counters = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public KycTenantQuota(IOptions<KycSettings> options, TimeProvider? clock = null)
    {
        var quota = options.Value.Identomat.TenantQuota;
        // Per-process counter in front of a per-IP vendor ceiling: the configured
        // numbers are node-wide, so they are divided across replicas for the same
        // reason IdentomatRateBudget divides its own.
        var replicas = Math.Clamp(options.Value.Identomat.ReplicaCount, MinimumReplicas, MaximumReplicas);
        _beginPermits = Math.Clamp(
            Math.Clamp(quota.BeginSessionsPerWindow, MinimumPermits, MaximumPermits) / replicas,
            MinimumPermits,
            MaximumPermits);
        _screeningPermits = Math.Clamp(
            Math.Clamp(quota.ScreeningCallsPerWindow, MinimumPermits, MaximumPermits) / replicas,
            MinimumPermits,
            MaximumPermits);
        _windowSeconds = Math.Clamp(quota.WindowSeconds, MinimumWindowSeconds, MaximumWindowSeconds);
        _clock = clock ?? TimeProvider.System;
    }

    public int RetryAfterSeconds => _windowSeconds;

    public bool TryConsume(Guid? tenantId, KycQuotaOperation operation)
    {
        var permits = operation == KycQuotaOperation.BeginSession
            ? _beginPermits
            : _screeningPermits;
        var partition = tenantId is null || tenantId.Value == Guid.Empty
            ? NodeDirectPartition
            : tenantId.Value.ToString("N");
        var window = _clock.GetUtcNow().ToUnixTimeSeconds() / _windowSeconds;
        var key = $"{operation}:{partition}:{window}";

        lock (_gate)
        {
            var used = _counters.GetValueOrDefault(key);
            if (used >= permits)
                return false;
            _counters[key] = used + 1;

            var windowSuffix = $":{window}";
            foreach (var stale in _counters.Keys
                         .Where(existing => !existing.EndsWith(windowSuffix, StringComparison.Ordinal))
                         .ToList())
            {
                _counters.TryRemove(stale, out _);
            }
        }

        return true;
    }
}
