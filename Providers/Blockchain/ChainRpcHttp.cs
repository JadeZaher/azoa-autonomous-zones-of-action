namespace AZOA.WebAPI.Providers.Blockchain;

/// <summary>Chain RPC <see cref="HttpClient"/> construction; see <c>Providers/Blockchain/AGENTS.md</c> §rpc-http-clients.</summary>
public static class ChainRpcHttp
{
    /// <summary>Named-client identity a host can configure (handler, resilience) per chain.</summary>
    public static string ClientName(string chainType) => "chain-rpc:" + chainType.ToLowerInvariant();

    /// <summary>Chains whose providers create named RPC clients.</summary>
    public static readonly string[] Chains = ["Algorand", "Ethereum", "Solana"];

    /// <summary>
    /// RV-L5-10: registers every named chain RPC client without the factory's request loggers, which would log URIs that may embed
    /// provider tokens in their path (QuickNode-style <c>/TOKEN/</c>).
    /// </summary>
    public static IServiceCollection AddChainRpcClients(this IServiceCollection services)
    {
        foreach (var chain in Chains) services.AddHttpClient(ClientName(chain)).RemoveAllLoggers();
        return services;
    }

    /// <summary>Scheme, host and port only: an RPC endpoint safe to log (no user info, path, query or fragment).</summary>
    public static string LogSafeEndpoint(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Scheme) + uri.Authority : "(unparsable endpoint)";

    /// <summary>An explicit handler (unit tests) wins, then the named factory client, else a bare client.</summary>
    public static HttpClient Create(string chainType, HttpMessageHandler? handler, IHttpClientFactory? factory)
        => handler is not null ? new HttpClient(handler, disposeHandler: false)
            : factory is not null ? factory.CreateClient(ClientName(chainType))
            : new HttpClient();
}
