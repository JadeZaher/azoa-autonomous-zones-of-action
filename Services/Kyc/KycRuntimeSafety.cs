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
}
