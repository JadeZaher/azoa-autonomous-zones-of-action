using Microsoft.Extensions.Diagnostics.HealthChecks;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.Observability;

/// <summary>
/// Health check that verifies the primary storage backend (SurrealDB) is
/// reachable. Reports Healthy when a trivial round-trip query (<c>RETURN 1</c>)
/// completes; Unhealthy otherwise.
///
/// Sole storage backend post-wave-3 EF deletion (`surrealdb-migration` Stream D).
/// The probe goes through <see cref="ISurrealExecutor"/> — the same code path
/// production traffic uses — so DI, connection, auth, and the OTEL
/// instrumentation decorator are all exercised.
/// </summary>
public sealed class StorageHealthCheck : IHealthCheck
{
    private readonly ISurrealExecutor _executor;

    public StorageHealthCheck(ISurrealExecutor executor)
    {
        _executor = executor;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var q = SurrealQuery.Of("RETURN 1");
            var response = await _executor.ExecuteAsync(q, cancellationToken);

            if (response.Count == 0 || !response[0].IsOk)
            {
                var detail = response.Count > 0 ? response[0].Detail ?? string.Empty : "no statements";
                return HealthCheckResult.Unhealthy(
                    $"SurrealDB probe returned non-OK: {detail}");
            }

            return HealthCheckResult.Healthy("SurrealDB connection established.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                description: "SurrealDB connection failed with an exception.",
                exception: ex);
        }
    }
}
