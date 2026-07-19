using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Admin;

/// <summary>Process-local defense in depth for operator credential guessing.</summary>
public sealed class NodeOperatorLoginThrottle
{
    private readonly ConcurrentDictionary<string, AttemptWindow> _attempts = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;
    private readonly int _addressLimit;
    private readonly int _addressUsernameLimit;
    private int _calls;

    public NodeOperatorLoginThrottle(IOptions<NodeOperatorLoginThrottleOptions> options)
    {
        var value = options.Value;
        _window = TimeSpan.FromSeconds(Math.Clamp(value.WindowSeconds, 60, 86_400));
        _addressLimit = Math.Clamp(value.PermitLimit, 1, 10_000);
        _addressUsernameLimit = Math.Clamp(value.UsernamePermitLimit, 1, _addressLimit);
    }

    public NodeOperatorThrottleDecision TryAcquire(
        string clientAddress,
        string username,
        DateTimeOffset now)
    {
        if (Interlocked.Increment(ref _calls) % 128 == 0)
            Prune(now);
        var ipKey = $"ip:{clientAddress}";
        var pairKey = $"pair:{clientAddress}:{username}";
        var address = Acquire(ipKey, _addressLimit, now);
        if (!address.Allowed)
            return address;
        return Acquire(pairKey, _addressUsernameLimit, now);
    }

    public void Reset(string clientAddress, string username)
    {
        _attempts.TryRemove($"pair:{clientAddress}:{username}", out _);
        _attempts.TryRemove($"ip:{clientAddress}", out _);
    }

    private NodeOperatorThrottleDecision Acquire(string key, int limit, DateTimeOffset now)
    {
        while (true)
        {
            var current = _attempts.GetOrAdd(key, _ => new AttemptWindow(now, 0));
            var next = now - current.StartedAt >= _window
                ? new AttemptWindow(now, 1)
                : current with { Count = current.Count + 1 };
            if (!_attempts.TryUpdate(key, next, current))
                continue;
            var allowed = next.Count <= limit;
            var retryAfterSeconds = allowed
                ? 0
                : Math.Clamp(
                    (int)Math.Ceiling((_window - (now - next.StartedAt)).TotalSeconds),
                    1,
                    (int)_window.TotalSeconds);
            return new NodeOperatorThrottleDecision(allowed, retryAfterSeconds);
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var entry in _attempts)
        {
            if (now - entry.Value.StartedAt >= _window)
                _attempts.TryRemove(entry.Key, out _);
        }

        foreach (var entry in _attempts
                     .OrderBy(item => item.Value.StartedAt)
                     .Take(Math.Max(0, _attempts.Count - 4096)))
        {
            _attempts.TryRemove(entry.Key, out _);
        }
    }

    private sealed record AttemptWindow(DateTimeOffset StartedAt, int Count);
}

public sealed class NodeOperatorLoginThrottleOptions
{
    public int PermitLimit { get; set; } = 20;
    public int UsernamePermitLimit { get; set; } = 5;
    public int WindowSeconds { get; set; } = 900;
}

public readonly record struct NodeOperatorThrottleDecision(bool Allowed, int RetryAfterSeconds);
