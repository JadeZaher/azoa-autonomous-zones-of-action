using System.Globalization;
using System.Text.Json;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Providers.Blockchain.Solana;

public partial class SolanaProvider
{
    private static readonly HttpClient JupiterBuildHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxResponseHeadersLength = 32,
    }) { Timeout = TimeSpan.FromSeconds(30) };

    private async Task<AZOAResult<JupiterBuildPlan>> ReadJupiterBuildAsync(string inputMint, string outputMint,
        ulong amount, string owner, string destination, int slippageBps, CancellationToken ct)
    {
        var apiKey = _config["AZOA:Solana:Dex:JupiterApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey)) return Unavailable("Jupiter Build credentials are not configured.");
        var fields = new Dictionary<string, string>
        {
            ["inputMint"] = inputMint, ["outputMint"] = outputMint, ["amount"] = amount.ToString(CultureInfo.InvariantCulture),
            ["taker"] = owner, ["payer"] = owner, ["slippageBps"] = slippageBps.ToString(CultureInfo.InvariantCulture),
            ["dexes"] = "Whirlpool", ["maxAccounts"] = "64", ["wrapAndUnwrapSol"] = "false",
            ["platformFeeBps"] = "0", ["transactionVersion"] = "0", ["destinationTokenAccount"] = destination,
        };
        var query = string.Join("&", fields.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.jup.ag/swap/v2/build?" + query);
        request.Headers.Add("x-api-key", apiKey);
        using var testClient = _httpMessageHandler is null ? null : new HttpClient(_httpMessageHandler, disposeHandler: false);
        using var response = await (testClient ?? JupiterBuildHttp).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            return Unavailable("Jupiter Build did not return an available route (HTTP " + (int)response.StatusCode + ").");
        const int maximumBytes = 131072;
        if (response.Content.Headers.ContentLength > maximumBytes) return Unavailable("Jupiter Build response exceeds the accepted size.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (body.Length + count > maximumBytes) return Unavailable("Jupiter Build response exceeds the accepted size.");
            body.Write(buffer, 0, count);
        }
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            return AZOAResult<JupiterBuildPlan>.Success(JupiterBuildPlanCodec.Read(document.RootElement, inputMint, outputMint, amount, slippageBps));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or
            KeyNotFoundException or FormatException or ArgumentException or OverflowException)
        {
            return Unavailable("Jupiter Build returned an unreviewed or malformed route contract.");
        }

        static AZOAResult<JupiterBuildPlan> Unavailable(string reason)
            => AZOAResult<JupiterBuildPlan>.FailureWithCode(reason, "DependencyUnavailable");
    }
}
