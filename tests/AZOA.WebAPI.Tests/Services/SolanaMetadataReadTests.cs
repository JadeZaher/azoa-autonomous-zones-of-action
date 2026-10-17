using System.Net;
using System.Text;
using System.Text.Json;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Solnet.Wallet;

namespace AZOA.WebAPI.Tests.Services;

public sealed class SolanaMetadataReadTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("zero", true)]
    [InlineData("absent-metadata", true)]
    [InlineData("absent-supply", false)]
    [InlineData("absent-decimals", false)]
    [InlineData("wrong-owner", false)]
    [InlineData("wrong-mint", false)]
    [InlineData("truncated", false)]
    public async Task ReadsActualMetadataWithoutInventingMissingSupply(string scenario, bool accepted)
    {
        using var rpc = new MetadataRpc(scenario);
        var result = await rpc.Provider.GetTokenMetadataAsync(rpc.Mint.Key);
        Assert.Equal(accepted, !result.IsError);
        if (!accepted) return;
        Assert.Equal(scenario == "zero" ? "0" : "18446744073709551615", result.Result!["totalSupply"]);
        Assert.Equal(scenario != "absent-metadata", result.Result["metadataPresent"]);
        if (scenario == "absent-metadata") { Assert.False(result.Result.ContainsKey("name")); return; }
        Assert.Equal("Actual name", result.Result["name"]);
        Assert.Equal("https://metadata.example/item", result.Result["uri"]);
        Assert.Equal(rpc.Authority.Key, result.Result["updateAuthority"]);
        Assert.Equal(false, result.Result["isMutable"]);
    }

    private sealed class MetadataRpc : HttpMessageHandler
    {
        internal PublicKey Mint { get; } = new Account().PublicKey;
        internal PublicKey Authority { get; } = new Account().PublicKey;
        internal SolanaProvider Provider { get; }
        private readonly string _scenario;
        internal MetadataRpc(string scenario)
        {
            _scenario = scenario;
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "Devnet", ["Blockchain:Chains:0:ChainType"] = "Solana",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://metadata.test/", ["AZOA:Solana:RpcRetryMaxAttempts"] = "0",
            }).Build();
            Provider = new(config, NullLogger<SolanaProvider>.Instance, null, null, null, this);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = json.RootElement;
            var args = root.GetProperty("params");
            Assert.Equal("finalized", args[1].GetProperty("commitment").GetString());
            object? value;
            switch (root.GetProperty("method").GetString())
            {
                case "getTokenSupply":
                    Assert.Equal(Mint.Key, args[0].GetString());
                    value = _scenario == "absent-supply" ? null : _scenario == "absent-decimals" ? new { amount = "1" }
                        : new { amount = _scenario == "zero" ? "0" : "18446744073709551615", decimals = 0, uiAmountString = "18446744073709551615" };
                    break;
                case "getAccountInfo":
                    Assert.Equal(SolanaNftMetadata.Metadata(Mint).Key, args[0].GetString());
                    value = _scenario == "absent-metadata" ? null : new { owner = _scenario == "wrong-owner" ? Authority.Key : SolanaNftMetadata.Program.Key,
                        executable = false, lamports = 1, data = new[] { Convert.ToBase64String(Metadata()), "base64" } };
                    break;
                default: throw new InvalidOperationException("Unexpected metadata read or egress.");
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").Clone(), result = new { value } })) };
        }
        private byte[] Metadata()
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write((byte)4); writer.Write(Authority.KeyBytes); writer.Write((_scenario == "wrong-mint" ? Authority : Mint).KeyBytes);
            foreach (var text in new[] { "Actual name", "SYMBOL", "https://metadata.example/item" }) { var bytes = Encoding.UTF8.GetBytes(text); writer.Write((uint)bytes.Length); writer.Write(bytes); }
            writer.Write((ushort)250); writer.Write((byte)0); writer.Write(false); writer.Write(false);
            writer.Write(new byte[4]);
            return _scenario == "truncated" ? stream.ToArray()[..20] : stream.ToArray();
        }
    }
}
