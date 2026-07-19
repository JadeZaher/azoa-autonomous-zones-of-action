using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Providers;
using AZOA.WebAPI.Models.Kyc;
using AZOA.WebAPI.Providers.Blockchain;
using AZOA.WebAPI.Services.Custody;
using AZOA.WebAPI.Services.Kyc;
using AZOA.WebAPI.Settings;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

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

        var mnemonic = _configuration[KeyCustodyService.PlatformMnemonicConfigPath];
        if (string.IsNullOrWhiteSpace(mnemonic))
            failures.Add($"{KeyCustodyService.PlatformMnemonicConfigPath} is required");
        else
        {
            try
            {
                _ = new Algorand.Algod.Model.Account(mnemonic.Trim());
            }
            catch
            {
                failures.Add($"{KeyCustodyService.PlatformMnemonicConfigPath} must be a valid Algorand mnemonic");
            }
        }

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
                || provider.ActiveNetwork != network
                || !provider.SupportsBridging)
            {
                failures.Add($"Algorand {network} provider is not bridge-capable");
            }
        }
        catch (Exception ex)
        {
            failures.Add($"Algorand {network} provider could not initialize ({ex.GetType().Name})");
        }

        return failures;
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
