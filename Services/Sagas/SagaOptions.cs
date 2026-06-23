namespace AZOA.WebAPI.Sagas;

/// <summary>
/// Configuration for the durable saga processor. Bind from the
/// <see cref="SectionName"/> section, exactly as
/// <c>ReconciliationOptions</c> is wired:
/// <code>
/// builder.Services
///     .AddOptions&lt;SagaOptions&gt;()
///     .Bind(builder.Configuration.GetSection(SagaOptions.SectionName));
/// </code>
/// Every value has a sensible fallback so the module is config-driven but works
/// out of the box with no <c>appsettings</c> entry.
/// </summary>
public sealed class SagaOptions
{
    /// <summary>Configuration section name: <c>"Sagas"</c>.</summary>
    public const string SectionName = "Sagas";

    /// <summary>
    /// Whether the background processor is enabled. <b>Default
    /// <c>false</c>:</b> the durable-saga module is a Phase-1 skeleton with
    /// ZERO bridge (or any) consumer pre-launch, so a consumerless hosted loop
    /// must not run in the production financial graph. It stays OFF until
    /// durable-saga Phase 2 ships a real consumer (see
    /// <c>docs/adr/ADR-0001-sagas-disabled-prelaunch.md</c> and
    /// <c>conductor/tracks/api-safety-hardening/docs/GO-TO-PROD.md</c> §4).
    /// The scoped <see cref="ISagaProcessor"/> can still be invoked directly
    /// (tests / an ops endpoint) when this is false.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Seconds between processor ticks. Default 5. Clamped to a 1s floor by the
    /// hosted service so a misconfigured tiny interval cannot hot-loop.
    /// </summary>
    public int IntervalSeconds { get; set; } = 5;

    /// <summary>Delay before the first tick after host start. Default 5s.</summary>
    public int StartupDelaySeconds { get; set; } = 5;

    /// <summary>Max due steps processed per tick. Bounds DB fan-out.
    /// Default 50; clamped to [1, 1000].</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Default max attempts when a step does not declare its own
    /// <see cref="RetryPolicy"/>. Default 5.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Default base backoff (seconds) for the exponential+jitter
    /// schedule when a step does not declare its own. Default 2.</summary>
    public int BaseBackoffSeconds { get; set; } = 2;

    /// <summary>
    /// Lease / visibility timeout (seconds): an <c>InProgress</c> step whose
    /// claim is older than this is treated as a crashed processor and made due
    /// again (crash-safe re-entry). Must comfortably exceed the longest
    /// expected single step. Default 300 (5 min).
    /// </summary>
    public int LeaseTimeoutSeconds { get; set; } = 300;
}
