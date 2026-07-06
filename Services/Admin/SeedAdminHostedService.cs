using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AZOA.WebAPI.Services.Admin;

/// <summary>
/// Boot-time fail-closed guard for the H2 operator:admin bootstrap seam. Does
/// NOT itself stamp any avatar — <c>AvatarManager.StampOperatorAdminIfSeeded</c>
/// is the actual (stateless, per-JWT-mint) mechanism. This service exists so a
/// misconfigured bootstrap (seed email set, seed secret missing) is loud at
/// startup rather than a silent, permanent no-op an operator only discovers
/// when their onboarding fails. See Services/Admin/AGENTS.md.
/// </summary>
public sealed class SeedAdminHostedService : IHostedService
{
    private readonly AdminBootstrapOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<SeedAdminHostedService> _logger;

    public SeedAdminHostedService(
        IOptions<AdminBootstrapOptions> options,
        IHostEnvironment environment,
        ILogger<SeedAdminHostedService> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var hasEmail = !string.IsNullOrWhiteSpace(_options.SeedEmail);
        var hasSecret = !string.IsNullOrWhiteSpace(_options.SeedSecret);

        if (!hasEmail && !hasSecret)
        {
            _logger.LogInformation(
                "Admin bootstrap is OFF (AdminBootstrap:SeedEmail/SeedSecret unset). " +
                "No JWT will ever be stamped operator:admin by this seam.");
            return Task.CompletedTask;
        }

        if (hasEmail != hasSecret)
        {
            // Fail-closed: partial config (email without secret, or vice versa) is
            // never treated as "armed" by AvatarManager, but in Production it is a
            // configuration error worth crashing loudly for rather than leaving a
            // silently-inert bootstrap an operator assumes is working.
            var message =
                "AdminBootstrap is misconfigured: SeedEmail and SeedSecret must BOTH " +
                "be set (or both unset). Bootstrap is inert until fixed.";
            if (_environment.IsProduction())
                throw new InvalidOperationException(message);

            _logger.LogWarning(message);
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Admin bootstrap is ARMED for seed email {SeedEmail}: its next JWT mint " +
            "will carry operator:admin. Environment={Environment}.",
            _options.SeedEmail, _environment.EnvironmentName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
