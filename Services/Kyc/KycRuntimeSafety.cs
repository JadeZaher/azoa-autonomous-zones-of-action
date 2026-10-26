
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

    /// <summary>Opt-in flag for the local dev Identomat sidecar; see <c>tools/dev-kyc-vendor/AGENTS.md</c> §origin-allowance.</summary>
    public const string InsecureDevVendorConfigurationKey = "Kyc:Identomat:AllowInsecureDevVendor";

    /// <summary>True only on a Development + Simulated host that explicitly sets <see cref="InsecureDevVendorConfigurationKey"/>.</summary>
    public static bool IsInsecureDevVendorAllowed(
        IHostEnvironment? environment,
        IConfiguration? configuration)
        => environment is not null
            && configuration is not null
            && configuration.GetValue<bool>(InsecureDevVendorConfigurationKey)
            && IsManualSimulationAllowed(environment, configuration);

    /// <summary>A loopback-only http(s) URL with no credentials or fragment (and no query when <paramref name="origin"/>).</summary>
    public static bool IsLoopbackDevVendorUrl(string? value, bool origin)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Fragment)
            && (!origin || string.IsNullOrEmpty(uri.Query))
            && uri.IsLoopback;

    /// <summary>
    /// A loopback dev-vendor redirect (<see cref="IsLoopbackDevVendorUrl"/>) that ALSO sits on
    /// the same scheme+host+port as <paramref name="pinnedOrigin"/> — the operator-configured
    /// vendor origin the applicant's browser is allowed to land on. Without this pin, the dev
    /// allowance would accept a redirect to any loopback port, not just the configured sidecar
    /// (M1, A4 adversarial review 2026-10-24). Shared by the KYC and KYB adapters so the two
    /// cannot drift again the way they did before this fix.
    /// </summary>
    public static bool IsPinnedLoopbackRedirect(string? value, string? pinnedOrigin)
        => IsLoopbackDevVendorUrl(value, origin: false)
            && Uri.TryCreate(value, UriKind.Absolute, out var redirect)
            && Uri.TryCreate(pinnedOrigin, UriKind.Absolute, out var pinned)
            && Uri.Compare(redirect, pinned, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

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

        // L2 (A4 fix batch): the dev vendor allowance only ever activates on a Development host
        // (IsInsecureDevVendorAllowed), so the flag being set anywhere else is never a live
        // bypass — but it silently no-ops instead of telling the operator their config did
        // nothing. Fail loud at boot instead, matching the throw convention above.
        if (configuration.GetValue<bool>(InsecureDevVendorConfigurationKey) && !environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                $"{InsecureDevVendorConfigurationKey} is set to true but the host environment is "
                + $"\"{environment.EnvironmentName}\", not Development. The dev KYC vendor allowance "
                + "only ever activates on Development (see KycRuntimeSafety.IsInsecureDevVendorAllowed), "
                + "so on this host the flag can only be a leftover misconfiguration; unset it before boot.");
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
