// SPDX-License-Identifier: UNLICENSED

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using AZOA.WebAPI.Settings;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>
/// The ONE shared per-IP request budget for every Identomat endpoint. KYC session
/// calls, AML screening, and monitoring polls all draw from the same ceiling; the
/// vendor bans the source IP for an hour past it, which would lock the production
/// node out of its own identity provider. See <c>Services/Kyc/AGENTS.md</c>.
/// </summary>
public interface IIdentomatRateBudget
{
    /// <summary>Reserves one request. False means the caller must back off, not retry hot.</summary>
    bool TryAcquire(string operation);

    /// <summary>Records an observed rate-limit ban; opens the circuit for the cooldown.</summary>
    void NoteRateLimited();

    /// <summary>True while a previously observed ban is still in force.</summary>
    bool IsCircuitOpen { get; }

    /// <summary>When the circuit closes again, or null when it is closed.</summary>
    DateTimeOffset? CircuitOpenUntil { get; }
}

/// <inheritdoc />
public sealed class IdentomatRateBudget : IIdentomatRateBudget
{
    private const int MinimumCeiling = 1;
    private const int MaximumCeiling = 600;
    private const int MinimumCooldownMinutes = 1;
    private const int MaximumCooldownMinutes = 24 * 60;

    private readonly int _ceilingPerMinute;
    private readonly TimeSpan _cooldown;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<long, int> _window = new();
    private readonly object _gate = new();

    private long _bannedUntilTicks;

    /// <summary>Replicas the node-wide ceiling is divided across; see the settings doc.</summary>
    private const int MinimumReplicas = 1;
    private const int MaximumReplicas = 100;

    public IdentomatRateBudget(IOptions<KycSettings> options, TimeProvider? clock = null)
    {
        var settings = options.Value.Identomat;
        // The vendor's ceiling is PER IP; this counter is per PROCESS. With N
        // replicas behind one egress IP an undivided ceiling lets the deployment
        // spend N times the node's allowance and earn the one-hour ban this budget
        // exists to avoid, so the node-wide number is divided down here.
        var replicas = Math.Clamp(settings.ReplicaCount, MinimumReplicas, MaximumReplicas);
        _ceilingPerMinute = Math.Clamp(
            Math.Clamp(settings.SharedRequestsPerMinute, MinimumCeiling, MaximumCeiling) / replicas,
            MinimumCeiling,
            MaximumCeiling);
        _cooldown = TimeSpan.FromMinutes(Math.Clamp(
            settings.RateLimitBanCooldownMinutes,
            MinimumCooldownMinutes,
            MaximumCooldownMinutes));
        _clock = clock ?? TimeProvider.System;
    }

    public bool IsCircuitOpen => CircuitOpenUntil is not null;

    public DateTimeOffset? CircuitOpenUntil
    {
        get
        {
            var ticks = Interlocked.Read(ref _bannedUntilTicks);
            if (ticks == 0)
                return null;
            var until = new DateTimeOffset(ticks, TimeSpan.Zero);
            return until > _clock.GetUtcNow() ? until : null;
        }
    }

    public bool TryAcquire(string operation)
    {
        _ = operation;
        if (IsCircuitOpen)
            return false;

        var now = _clock.GetUtcNow();
        var bucket = now.ToUnixTimeSeconds() / 60;
        lock (_gate)
        {
            var used = _window.GetValueOrDefault(bucket);
            if (used >= _ceilingPerMinute)
                return false;
            _window[bucket] = used + 1;

            // Drop buckets older than the current minute; the window is one minute
            // wide, so anything earlier can never be consulted again.
            foreach (var stale in _window.Keys.Where(key => key < bucket).ToList())
                _window.TryRemove(stale, out _);
        }

        return true;
    }

    public void NoteRateLimited()
    {
        var until = _clock.GetUtcNow().Add(_cooldown).UtcDateTime;
        Interlocked.Exchange(ref _bannedUntilTicks, until.Ticks);
    }
}
