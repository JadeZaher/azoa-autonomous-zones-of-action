namespace AZOA.WebAPI.Helpers;

/// <summary>Loads the explicit isolated EVM development overlay only after local setup.</summary>
public static class EthereumLocalConfiguration
{
    public static void Apply(ConfigurationManager configuration, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment() || Environment.GetEnvironmentVariable("AZOA_ETHEREUM_LOCAL") != "true") return;
        var path = Path.Combine(environment.ContentRootPath, "appsettings.EthereumLocal.json");
        if (!File.Exists(path)) throw new InvalidOperationException("Run the isolated Ethereum deployment setup before enabling its overlay.");
        configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
        var endpoint = configuration["Blockchain:Chains:2:Devnet:NodeUrl"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsLoopback
            || configuration["Blockchain:Chains:2:Devnet:ChainId"] != "31337")
            throw new InvalidOperationException("Ethereum local overlay must bind the isolated loopback chain 31337.");
    }
}
