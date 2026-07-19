using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AZOA.WebAPI.Services.Kyc;

/// <summary>Central runtime boundary for the non-authoritative manual KYC simulator.</summary>
public static class KycRuntimeSafety
{
    public const string ManualSimulationUnavailable =
        "Manual KYC simulation requires Development, Blockchain:Mode=Simulated, and real-value bridging disabled.";

    public static bool IsManualSimulationAllowed(
        IHostEnvironment environment,
        IConfiguration configuration)
        => environment.IsDevelopment()
            && string.Equals(
                configuration["Blockchain:Mode"],
                "Simulated",
                StringComparison.OrdinalIgnoreCase)
            && !configuration.GetValue<bool>("Blockchain:Bridge:RealValueEnabled");

    /// <summary>Rejects simulation-only identity paths before a production/mainnet host starts.</summary>
    public static void GuardStartup(IHostEnvironment environment, IConfiguration configuration)
    {
        var provider = configuration["Kyc:Provider"]?.Trim();
        var adminOverrideEnabled = configuration.GetValue<bool>("Kyc:AdminOverride:Enabled");
        var simulationProvider = string.Equals(provider, "manual", StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider, "mock", StringComparison.OrdinalIgnoreCase);
        var mainnet = string.Equals(
            configuration["Blockchain:DefaultNetwork"], "Mainnet", StringComparison.OrdinalIgnoreCase);

        var permittedSimulationEnvironment = environment.IsDevelopment()
            || environment.IsEnvironment("Local")
            || environment.IsEnvironment("Test")
            || environment.IsEnvironment("CI")
            || environment.IsEnvironment("Testnet")
            || environment.IsEnvironment("IntegrationTest");
        if ((simulationProvider || adminOverrideEnabled)
            && (!permittedSimulationEnvironment || environment.IsProduction() || mainnet))
        {
            throw new InvalidOperationException(
                "KYC mock/manual providers and admin overrides are limited to Local/Test/CI/Testnet simulation and cannot run in Production or on Mainnet.");
        }
    }
}
