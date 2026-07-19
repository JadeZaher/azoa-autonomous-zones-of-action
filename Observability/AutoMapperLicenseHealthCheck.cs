using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AZOA.WebAPI.Observability;

/// <summary>Fails Production readiness when AutoMapper licensing is absent.</summary>
public sealed class AutoMapperLicenseHealthCheck : IHealthCheck
{
    private readonly IHostEnvironment _environment;

    public AutoMapperLicenseHealthCheck(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var license = Environment.GetEnvironmentVariable("AUTOMAPPER_LICENSE_KEY")
            ?? Environment.GetEnvironmentVariable("LUCKYPENNY_LICENSE_KEY");
        if (!string.IsNullOrWhiteSpace(license))
            return Task.FromResult(HealthCheckResult.Healthy("AutoMapper licensing is configured."));

        return Task.FromResult(_environment.IsProduction()
            ? HealthCheckResult.Unhealthy(
                "A registered AutoMapper 15+ license is required for hosted production use.")
            : HealthCheckResult.Healthy(
                "AutoMapper licensing is not enforced outside Production."));
    }
}
