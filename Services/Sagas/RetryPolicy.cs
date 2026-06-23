namespace AZOA.WebAPI.Sagas;

/// <summary>
/// Per-step retry policy: a bounded attempt budget plus exponential backoff
/// with full jitter. Generic and bridge-agnostic — any saga step declares its
/// own (or inherits the saga default / the <see cref="SagaOptions"/> fallback).
///
/// <para>The backoff schedule is <c>base * 2^(attempt-1)</c> clamped to
/// <see cref="MaxBackoff"/>, then "full jitter" is applied
/// (<c>Random(0, computed)</c>) — the AWS-recommended scheme — so a fleet of
/// failing steps does not retry in lockstep (thundering herd). The processor
/// adds the returned delay to <c>NextRunAt</c>; the conditional claim then
/// naturally skips the step until it is due again.</para>
/// </summary>
public sealed class RetryPolicy
{
    /// <summary>
    /// Maximum number of attempts for the step before it is routed to its
    /// declared compensation (forward step) or dead-lettered (compensation
    /// step). Must be &gt;= 1. Default 5.
    /// </summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Base backoff before the first retry. Default 2s.</summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Upper bound on a single backoff interval. Default 5 min.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The conservative default policy.</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>
    /// Compute the delay before the <paramref name="attempt"/>-th retry
    /// (1-based: <c>attempt==1</c> ⇒ the delay after the first failure).
    /// Exponential, clamped to <see cref="MaxBackoff"/>, then full-jittered.
    /// A null/absent <paramref name="random"/> uses a shared thread-safe source.
    /// </summary>
    public TimeSpan NextDelay(int attempt, Random? random = null)
    {
        if (attempt < 1) attempt = 1;

        // 2^(attempt-1) without overflow for large attempt counts.
        var factor = Math.Pow(2, Math.Min(attempt - 1, 30));
        var raw = BaseBackoff.TotalMilliseconds * factor;
        var capped = Math.Min(raw, MaxBackoff.TotalMilliseconds);

        // Full jitter: uniform in [0, capped]. Never negative; never 0-length
        // so a claimed-then-failed step always moves NextRunAt strictly forward.
        var rng = random ?? Random.Shared;
        var jittered = rng.NextDouble() * capped;
        var ms = Math.Max(1, jittered);
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>True once <paramref name="attemptCount"/> attempts have been
    /// consumed (no budget remains).</summary>
    public bool IsExhausted(int attemptCount) => attemptCount >= Math.Max(1, MaxAttempts);
}
