
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

    /// <summary>
    /// Fail-fast boot guard (production-readiness FR-5 AC3): a Production host must
    /// resolve a real identity provider, not manual/mock/unavailable.
    ///
    /// <para>This MUST read <c>Kyc:Provider</c> directly and diff it against the set
    /// of adapter keys that are actually registered, exactly as
    /// <c>BlockchainProviderFactory.GuardAgainstUnregisteredEnabledChains</c> reads
    /// <c>Blockchain:Chains</c> rather than routing through the factory: the runtime
    /// selection path SILENTLY substitutes <c>UnavailableKycProviderService</c> for an
    /// unknown key, so asking it would make the exact defect this guard exists to
    /// catch invisible. A misconfigured production node would then boot healthy and
    /// fail closed on every real verification instead of failing at start.</para>
    ///
    /// <para><see cref="GuardStartup"/> is the complementary direction: it rejects a
    /// simulation provider that is explicitly selected. This one rejects the absence
    /// of a real one.</para>
    /// </summary>
    public static void GuardProductionProviderIsReal(
        IHostEnvironment environment,
        IConfiguration configuration,
        IEnumerable<string> registeredAdapterKeys)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(registeredAdapterKeys);

        if (!environment.IsProduction())
            return;

        var configured = configuration["Kyc:Provider"]?.Trim().ToLowerInvariant() ?? string.Empty;
        var real = new HashSet<string>(registeredAdapterKeys, StringComparer.OrdinalIgnoreCase);
        real.Remove("manual");
        real.Remove("mock");
        real.Remove("unavailable");

        if (real.Contains(configured))
            return;

        throw new InvalidOperationException(
            $"Kyc:Provider is \"{(configured.Length == 0 ? "(unset)" : configured)}\" in a Production "
            + "environment, which resolves to a manual, mock, or unavailable identity provider. "
            + "A Production node must select a real KYC adapter before it starts, otherwise every "
            + "value-gated operation fails closed at request time instead of at boot. Set "
            + $"Kyc:Provider to one of: {string.Join(", ", real.OrderBy(key => key, StringComparer.Ordinal))}.");
    }
}
