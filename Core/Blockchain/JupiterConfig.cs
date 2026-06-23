namespace AZOA.WebAPI.Core.Blockchain;

/// <summary>
/// Configuration for Jupiter DEX aggregator API on Solana.
/// Bind from appsettings under "Blockchain:Jupiter".
/// </summary>
public class JupiterConfig
{
    public const string SectionName = "Blockchain:Jupiter";

    /// <summary>Jupiter API key from developers.jup.ag/portal.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Jupiter API base URL. Default: https://quote-api.jup.ag.</summary>
    public string BaseUrl { get; set; } = "https://quote-api.jup.ag";
}
