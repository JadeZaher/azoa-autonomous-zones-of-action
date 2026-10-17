using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AZOA.WebAPI.Observability;

/// <summary>Fails readiness when real-value bridging is armed without its prerequisites.</summary>
public sealed class RealValueReadinessHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;
    private readonly IBlockchainProviderFactory _providerFactory;
    private readonly IKycProviderService _kycProvider;
    private readonly IHostEnvironment _environment;

    public RealValueReadinessHealthCheck(
        IConfiguration configuration,
        IBlockchainProviderFactory providerFactory,
        IKycProviderService kycProvider,
        IHostEnvironment environment)
    {
        _configuration = configuration;
        _providerFactory = providerFactory;
        _kycProvider = kycProvider;
        _environment = environment;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_configuration.GetValue<bool>("Blockchain:Bridge:RealValueEnabled"))
            return Task.FromResult(HealthCheckResult.Healthy("Real-value bridging is disabled."));

        var failures = FindFailures();
        return Task.FromResult(failures.Count == 0
            ? HealthCheckResult.Healthy("Real-value blockchain, custody, and KYC prerequisites are configured.")
            : HealthCheckResult.Unhealthy(
                $"Real-value bridging is enabled but not launch-ready: {string.Join("; ", failures)}"));
    }

    private List<string> FindFailures()
    {
        var failures = new List<string>();

        if (!string.Equals(_configuration["Blockchain:Mode"], "Live", StringComparison.OrdinalIgnoreCase))
            failures.Add("Blockchain:Mode must be Live");

        if (!string.Equals(_configuration["Blockchain:DefaultChain"], "Algorand", StringComparison.OrdinalIgnoreCase))
            failures.Add("Blockchain:DefaultChain must be Algorand");

        if (!string.Equals(_configuration["Blockchain:Wormhole:DefaultMode"], "Trusted", StringComparison.OrdinalIgnoreCase))
            failures.Add("Blockchain:Wormhole:DefaultMode must be Trusted");

        AddKycFailures(failures);

        AddCustodyFailures(failures);

        AddPlatformKeyFailures(failures);

        var vault = _configuration["Blockchain:Wormhole:BridgeVaults:Algorand:VaultAddress"];
        if (string.IsNullOrWhiteSpace(vault)
            || !Algorand.Address.IsValid(vault)
            || string.Equals(vault, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAY5HFKQ", StringComparison.Ordinal))
        {
            failures.Add("Blockchain:Wormhole:BridgeVaults:Algorand:VaultAddress must be a valid non-zero Algorand address");
        }

        var networkName = _configuration["Blockchain:DefaultNetwork"];
        if (!Enum.TryParse<ChainNetwork>(networkName, ignoreCase: true, out var network)
            || !Enum.IsDefined(network))
        {
            failures.Add("Blockchain:DefaultNetwork must name a supported network");
            return failures;
        }

        var algorand = _configuration.GetSection("Blockchain:Chains")
            .GetChildren()
            .FirstOrDefault(chain => string.Equals(
                chain["ChainType"], "Algorand", StringComparison.OrdinalIgnoreCase));
        var networkSection = algorand?.GetSection(network.ToString());
        if (networkSection is null || !networkSection.GetValue<bool>("IsEnabled"))
            failures.Add($"Algorand {network} must be enabled");

        var nodeUrl = networkSection?["NodeUrl"];
        if (!Uri.TryCreate(nodeUrl, UriKind.Absolute, out var nodeUri)
            || nodeUri.Scheme is not ("http" or "https"))
        {
            failures.Add($"Algorand {network} NodeUrl must be an absolute HTTP(S) URL");
        }

        if (failures.Count != 0)
            return failures;

        try
        {
            var provider = _providerFactory.GetProvider("Algorand", network);
            if (!string.Equals(provider.ChainType, "Algorand", StringComparison.OrdinalIgnoreCase)
                || provider.ActiveNetwork != network)
            {
                failures.Add($"Algorand {network} provider did not bind to the requested chain/network");
            }
            // Real value leaves the platform through the Algorand SOURCE leg (the configured
            // vault above is an Algorand vault). Requiring the source capability is what this
            // probe can truthfully assert; Algorand deliberately implements no target leg, so
            // demanding one here would fail readiness for a capability the route never uses.
            else if (!provider.TryGetModule<IBridgeSourceLeg>(out _))
            {
                failures.Add($"Algorand {network} provider is not bridge-source-capable");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"Algorand {network} provider could not initialize ({ex.GetType().Name})");
        }

        return failures;
    }

    /// <summary>
    /// Custodial key management is a real-value prerequisite, so the healthy message says so.
    /// This CONSUMES <see cref="CustodyModeResolver"/> rather than restating its predicate: a probe
    /// that re-derived the rule would drift from the resolver the wallet path actually obeys, and
    /// green here would then mean nothing. Without it a node deployed with RealValueEnabled=true,
    /// CustodyMode=Disabled and a short encryption key reported ready while every wallet
    /// provisioning attempt failed closed.
    /// </summary>
    private void AddCustodyFailures(List<string> failures)
    {
        var custody = CustodyModeResolver.Resolve(_configuration, _environment);
        if (!custody.Available)
        {
            failures.Add(custody.UnavailableReason
                ?? $"{CustodyModeResolver.CustodyModeConfigurationPath} must name a usable custody mode");
        }
    }

    /// <summary>
    /// Every chain that is ENABLED for at least one network must have a usable platform
    /// signing key, so a missing or malformed Solana/Ethereum key surfaces here rather
    /// than at the first value operation. A chain configured but disabled on every
    /// network is skipped — it cannot reach the platform door.
    /// <para>
    /// Enabled-implies-registered is guaranteed at boot by
    /// <c>BlockchainProviderFactory.GuardAgainstUnregisteredEnabledChains</c>, so this
    /// reads config rather than instantiating providers: building every provider inside a
    /// readiness probe would be strictly more expensive and no more truthful.
    /// </para>
    /// </summary>
    private void AddPlatformKeyFailures(List<string> failures)
    {
        var derivations = PlatformKeyDerivationRegistry.Default;

        foreach (var chain in _configuration.GetSection("Blockchain:Chains").GetChildren())
        {
            var chainType = chain["ChainType"];
            if (string.IsNullOrWhiteSpace(chainType))
                continue;

            // Simulated settlement never signs; a platform key for it would be meaningless.
            if (string.Equals(chainType, "Simulated", StringComparison.OrdinalIgnoreCase))
                continue;

            var isEnabled = chain.GetSection("Devnet").GetValue<bool>("IsEnabled")
                || chain.GetSection("Testnet").GetValue<bool>("IsEnabled")
                || chain.GetSection("Mainnet").GetValue<bool>("IsEnabled");
            if (!isEnabled)
                continue;

            if (!derivations.TryGet(chainType, out var derivation))
            {
                failures.Add(
                    $"chain '{chainType.Trim()}' is enabled but has no registered platform signing key "
                    + $"derivation (chains with a platform key door: {derivations.RegisteredChainTypesDisplay})");
                continue;
            }

            var configured = _configuration[derivation.PlatformKeyConfigPath];
            if (string.IsNullOrWhiteSpace(configured))
            {
                failures.Add($"{derivation.PlatformKeyConfigPath} is required");
                continue;
            }

            // Prove the key by deriving it, then wipe it: a readiness probe that only
            // checked for a non-empty string would pass on a malformed secret.
            if (!derivation.TryDerive(configured, out var key))
            {
                failures.Add($"{derivation.PlatformKeyConfigPath} must be a valid {derivation.SecretDescription}");
                continue;
            }

            System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
        }
    }

    private void AddKycFailures(List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(_configuration["Kyc:Provider"]))
        {
            failures.Add("Kyc:Provider must explicitly select an authoritative provider");
            return;
        }

        var expiryDays = _configuration.GetValue<int?>("Kyc:SubmissionExpiryDays");
        if (expiryDays is null or <= 0)
            failures.Add("Kyc:SubmissionExpiryDays must be greater than zero for real-value operations");

        try
        {
            var settings = _configuration
                .GetSection(KycSettings.SectionName)
                .Get<KycSettings>() ?? new KycSettings();
            if (_kycProvider.Provider == KycProvider.MANUAL
                || string.Equals(settings.Provider, "manual", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("Manual KYC simulation cannot authorize real-value operations");
                return;
            }
            if (!KycApprovalTrust.TryResolveCurrentProfile(
                    _kycProvider,
                    settings,
                    _environment,
                    out _,
                    out _,
                    out var policyFailure))
            {
                failures.Add($"The configured KYC authority or approval policy is unavailable ({policyFailure})");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"The configured KYC authority could not initialize ({ex.GetType().Name})");
        }
    }
}
