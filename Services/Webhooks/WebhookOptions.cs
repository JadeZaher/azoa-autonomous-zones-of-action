// SPDX-License-Identifier: UNLICENSED

namespace AZOA.WebAPI.Services.Webhooks;

/// <summary>
/// Configuration for the consent webhook delivery worker (tenant-consent-delegation §4,
/// AC7). Bind from the <see cref="SectionName"/> section, exactly as
/// <c>ReconciliationOptions</c> / <c>SagaOptions</c> are wired:
/// <code>
/// builder.Services
///     .AddOptions&lt;WebhookOptions&gt;()
///     .Bind(builder.Configuration.GetSection(WebhookOptions.SectionName));
/// </code>
/// Every value has a sensible fallback so the module is config-driven but works out of
/// the box with no <c>appsettings</c> entry.
/// </summary>
public sealed class WebhookOptions
{
    /// <summary>Configuration section name: <c>"Webhooks"</c>.</summary>
    public const string SectionName = "Webhooks";

    /// <summary>
    /// Whether the background delivery worker is enabled. <b>Default <c>false</c></b> —
    /// like the saga processor, a consumerless outbound loop must not run in the
    /// production graph until a real consumer (a registered tenant endpoint) exists. The
    /// scoped stores can still be exercised directly (tests / an ops trigger) when this
    /// is false.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Seconds between due-scan ticks. Default 5. Clamped to a 1s floor by the
    /// worker so a misconfigured tiny interval cannot hot-loop.</summary>
    public int PollSeconds { get; set; } = 5;

    /// <summary>Delay before the first scan after host start, letting the app warm up.
    /// Default 5s.</summary>
    public int StartupDelaySeconds { get; set; } = 5;

    /// <summary>Max due events delivered per tick. Bounds DB + outbound HTTP fan-out.
    /// Default 50; clamped to [1, 1000].</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Max delivery attempts before an event is dead-lettered. Default 8.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Base backoff (seconds) for the exponential retry schedule
    /// (<c>BaseBackoffSeconds * 2^attempt</c>, capped by <see cref="MaxBackoffSeconds"/>).
    /// Default 5.</summary>
    public int BaseBackoffSeconds { get; set; } = 5;

    /// <summary>Ceiling for a single backoff interval (seconds). Default 3600 (1h).</summary>
    public int MaxBackoffSeconds { get; set; } = 3600;

    /// <summary>Per-POST HTTP timeout (seconds). Default 10.</summary>
    public int HttpTimeoutSeconds { get; set; } = 10;

    /// <summary>The named <see cref="System.Net.Http.IHttpClientFactory"/> client used
    /// for delivery POSTs.</summary>
    public const string HttpClientName = "consent-webhook";
}
