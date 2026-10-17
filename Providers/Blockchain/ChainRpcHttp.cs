namespace AZOA.WebAPI.Providers.Blockchain;

/// <summary>Chain RPC <see cref="HttpClient"/> construction; see <c>Providers/Blockchain/AGENTS.md</c> §rpc-http-clients.</summary>
public static class ChainRpcHttp
{
    /// <summary>Named-client identity a host can configure (handler, resilience) per chain.</summary>
    public static string ClientName(string chainType) => "chain-rpc:" + chainType.ToLowerInvariant();

    /// <summary>An explicit handler (unit tests) wins, then the named factory client, else a bare client.</summary>
    public static HttpClient Create(string chainType, HttpMessageHandler? handler, IHttpClientFactory? factory)
        => handler is not null ? new HttpClient(handler, disposeHandler: false)
            : factory is not null ? factory.CreateClient(ClientName(chainType))
            : new HttpClient();
}
