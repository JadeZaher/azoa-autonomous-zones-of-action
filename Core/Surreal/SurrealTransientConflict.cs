namespace AZOA.WebAPI.Core.Surreal;

/// <summary>Shared optimistic-concurrency retry primitive for SurrealDB 3.x
/// single-winner conditional-UPDATE seams. See <c>Core/Surreal/AGENTS.md</c>
/// §transient-conflict for the rationale.</summary>
public static class SurrealTransientConflict
{
    /// <summary>Default bounded retry budget for a contended single-winner claim
    /// (matches the idempotency-store precedent E3 shipped).</summary>
    public const int DefaultMaxRetries = 8;

    /// <summary>True when <paramref name="ex"/> is SurrealDB 3.x's transient
    /// write-write contention signal ("Transaction conflict: Resource busy ...
    /// can be retried"). Matched on the message tokens because the RocksDB engine
    /// raises a plain exception type; the tokens are stable across 3.x.</summary>
    public static bool IsRetryableConflict(Exception ex)
    {
        var m = ex.Message;
        return m.Contains("Transaction conflict", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Resource busy", StringComparison.OrdinalIgnoreCase)
            || m.Contains("can be retried", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Runs <paramref name="operation"/> under a bounded retry loop,
    /// retrying only on <see cref="IsRetryableConflict"/>. On retry the winner's
    /// write has already landed, so a conditional-UPDATE loser resolves cleanly
    /// to its no-op/affected==0 path. Backoff is a small exponential-ish delay
    /// with per-attempt jitter to break the contending herd.</summary>
    public static async Task<T> RetryOnConflictAsync<T>(
        Func<Task<T>> operation,
        CancellationToken ct,
        int maxRetries = DefaultMaxRetries)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < maxRetries && IsRetryableConflict(ex))
            {
                var jitterMs = Random.Shared.Next(0, 4);
                await Task.Delay(TimeSpan.FromMilliseconds(5 * (attempt + 1) + jitterMs), ct);
            }
        }
    }
}
