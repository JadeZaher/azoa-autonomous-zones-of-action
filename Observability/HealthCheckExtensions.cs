using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace AZOA.WebAPI.Observability;

/// <summary>
/// Registers health checks and maps the /health endpoint with a JSON response writer.
/// No external packages — uses only Microsoft.Extensions.Diagnostics.HealthChecks (framework-native).
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>
    /// Registers AddHealthChecks() with the AZOA storage and provider-monitor checks.
    /// Call from Program.cs: builder.Services.AddAzoaHealthChecks();
    /// </summary>
    public static IServiceCollection AddAzoaHealthChecks(this IServiceCollection services)
    {
        services
            .AddHealthChecks()
            .AddCheck<StorageHealthCheck>(
                name: "storage-db",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready", "db"])
            .AddCheck<ProviderHealthMonitorHealthCheck>(
                name: "provider-monitor",
                failureStatus: HealthStatus.Degraded,
                tags: ["ready", "providers"]);

        return services;
    }

    /// <summary>
    /// Maps GET /health returning a JSON payload listing each check's name, status, and description.
    /// Call from Program.cs: app.MapAzoaHealth(app.Environment);
    /// </summary>
    public static void MapAzoaHealth(this IEndpointRouteBuilder app, IHostEnvironment environment)
    {
        // Exception messages can leak internals (connection strings, host names)
        // so they are only included in Development; the bare probe stays minimal.
        var includeExceptions = environment.IsDevelopment();
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = (context, report) => WriteJsonResponseAsync(context, report, includeExceptions)
        });
    }

    private static async Task WriteJsonResponseAsync(HttpContext context, HealthReport report, bool includeExceptions)
    {
        context.Response.ContentType = "application/json; charset=utf-8";

        var result = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                durationMs = entry.Value.Duration.TotalMilliseconds,
                exception = includeExceptions ? entry.Value.Exception?.Message : null
            })
        };

        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });

        await context.Response.WriteAsync(json, System.Text.Encoding.UTF8);
    }
}
