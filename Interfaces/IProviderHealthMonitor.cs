using AZOA.WebAPI.Core;

namespace AZOA.WebAPI.Interfaces;

/// <summary>Provider health scoreboard read by <c>ProviderHealthMonitorHealthCheck</c>.</summary>
/// <remarks>
/// STATUS -- NOT WIRED. Verified by grep on 2026-09-07 at commit 9f4e026: no production code
/// calls <c>RecordSuccess</c>, <c>RecordFailure</c>, <c>MarkUnhealthy</c> or <c>MarkHealthy</c>.
/// The only callers are <c>tests/AZOA.WebAPI.Tests/Core/ProviderHealthMonitorTests.cs</c>.
/// <para>
/// Consequence, stated plainly so nobody cites this type as a control: the scoreboard is
/// permanently empty, so the sole production consumer
/// (<c>Observability/ProviderHealthMonitorHealthCheck.cs</c>) always takes its
/// <c>scores.Count == 0</c> branch and returns <c>Healthy("No provider data recorded yet.")</c>.
/// <c>/health</c> therefore CANNOT report provider degradation today. This is a data structure
/// waiting for a producer, not an enforcement point.
/// </para>
/// <para>
/// How it got here: the recorders lost their caller when the provider-selection layer
/// (<c>HealthRecordingProviderDecorator</c>, <c>Core/ProviderSelection/*</c>) was deleted in the
/// architecture-decoupling track; see that track's <c>plan.md</c> item 6, which retained this
/// interface for <c>/health</c> and logged the re-wire as debt. The debt was never paid.
/// </para>
/// <para>
/// Resolution is owned by the ops-postlaunch-hardening track and must be all-or-nothing.
/// Either (a) wire the recorders at the two boundaries that actually know outcome and latency --
/// <c>BaseBlockchainProvider.ExecuteWithRetryAsync</c> for chain rails and the
/// <c>IKycProviderService</c> implementations for KYC vendors -- or (b) delete this interface,
/// <c>Services/Blockchain/ProviderHealthMonitor.cs</c>, the health check and the DI registration
/// in <c>Program.cs</c> together. Whichever is chosen ships with a test that fails when the
/// wiring is removed. A partially wired scoreboard is worse than none: it reads Healthy in both
/// the healthy case and the never-recorded case, so it would look like coverage while providing
/// none.
/// </para>
/// </remarks>
public interface IProviderHealthMonitor
{
    /// <summary>Records a successful provider operation. NOT WIRED -- see the remarks on this interface.</summary>
    void RecordSuccess(string providerName, double latencyMs);

    /// <summary>Records a failed provider operation. NOT WIRED -- see the remarks on this interface.</summary>
    void RecordFailure(string providerName);

    /// <summary>Gets current health scores; always empty while the recorders have no production callers.</summary>
    IReadOnlyDictionary<string, ProviderHealthScore> GetScores();

    /// <summary>Marks a provider unhealthy (e.g. after a circuit breaker trips). NOT WIRED -- see the remarks on this interface.</summary>
    void MarkUnhealthy(string providerName);

    /// <summary>Marks a provider healthy again. NOT WIRED -- see the remarks on this interface.</summary>
    void MarkHealthy(string providerName);
}
