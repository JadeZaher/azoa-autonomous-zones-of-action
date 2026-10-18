using System.Net.Http.Json;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Nethereum.Util;

namespace AZOA.WebAPI.IntegrationTests.Fixtures.Wtt;

/// <summary>WTT six-route protocol gate over pinned local Wormhole deployments; fails closed without them. See AGENTS.md.</summary>
[Trait("Gate", "WttSixRoute")]
public sealed class WttSixRouteProtocolTests : IDisposable
{
    private const int Solana = 1, Ethereum = 2, Algorand = 8;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string _state = LocateState();

    public void Dispose() => _http.Dispose();

    public static TheoryData<int, int> Routes => new()
    {
        { Ethereum, Solana }, { Ethereum, Algorand }, { Solana, Ethereum }, { Solana, Algorand }, { Algorand, Ethereum }, { Algorand, Solana },
    };

    [Fact]
    public async Task EveryChainRunsThePinnedProtocolUnderTheOneLocalGuardianSet()
    {
        var guardians = Read("guardians.json").GetProperty("addresses").EnumerateArray()
            .Select(address => address.GetString()!.ToLowerInvariant()).ToArray();
        var evm = Read("evm.json");
        var core = evm.GetProperty("core").GetString()!;
        var bridge = evm.GetProperty("tokenBridge").GetString()!;
        (await EvmUInt(evm, core, "chainId()")).Should().Be(Ethereum);
        (await EvmUInt(evm, core, "evmChainId()")).Should().Be(evm.GetProperty("chainId").GetInt32());
        var index = await EvmUInt(evm, core, "getCurrentGuardianSetIndex()");
        (await EvmGuardianSet(evm, core, (uint)index)).Should().Equal(guardians, "the EVM core trusts exactly the local set");
        (await EvmUInt(evm, bridge, "chainId()")).Should().Be(Ethereum);
        (await EvmWord(evm, bridge, Selector("wormhole()"))).Should().EndWith(core[2..].ToLowerInvariant());

        var solana = Read("solana.json");
        var guardianSet = await SolanaAccount(solana, solana.GetProperty("guardianSet").GetString()!);
        guardianSet.Owner.Should().Be(solana.GetProperty("core").GetString());
        foreach (var guardian in guardians)
            Convert.ToHexString(guardianSet.Data).ToLowerInvariant().Should().Contain(guardian[2..], "the Solana core holds the local set");
        (await SolanaAccount(solana, solana.GetProperty("tokenBridgeConfig").GetString()!)).Owner
            .Should().Be(solana.GetProperty("tokenBridge").GetString());

        var algorand = Read("algorand.json");
        foreach (var (app, digest) in new[] { ("coreAppId", "coreApprovalSha256"), ("tokenBridgeAppId", "tokenBridgeApprovalSha256") })
            (await AlgorandApprovalSha256(algorand, algorand.GetProperty(app).GetString()!))
                .Should().Be(algorand.GetProperty(digest).GetString(), "the deployed TEAL is the program compiled from the pinned source");
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryDirectedRouteTargetTrustsItsSourceTokenBridgeEmitter(int source, int target)
    {
        var expected = Emitter(source);
        var registered = target switch
        {
            Ethereum => await EvmWord(Read("evm.json"), Read("evm.json").GetProperty("tokenBridge").GetString()!,
                Selector("bridgeContracts(uint16)") + source.ToString("x64")),
            Algorand => Read("algorand.json").GetProperty("registered").TryGetProperty(source.ToString(), out var emitter) ? emitter.GetString() : null,
            _ => throw new InvalidOperationException(
                "Solana RegisterChain is not authored (deferred.md candidate WTT-SIX-ROUTE-SOLANA-REGISTRATION): needs the pinned " +
                "verify_signatures + post_vaa + register_chain transaction sequence against the loaded token bridge."),
        };
        registered.Should().NotBeNull().And.EndWith(expected, $"chain {target} must accept chain {source}'s token-bridge emitter");
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void EveryDirectedRouteSettlesARealTransferExactlyOnce(int source, int target)
        => throw new InvalidOperationException(
            $"WTT route {source}->{target} transfer acceptance is not authored (deferred.md candidate WTT-SIX-ROUTE-EXEC): drive " +
            "WttLifecycleService over these deployments with guardian-signed proofs from lib/guardian.mjs and assert one settlement.");

    private string Emitter(int chain) => (chain switch
    {
        Ethereum => Read("evm.json"), Solana => Read("solana.json"), _ => Read("algorand.json"),
    }).GetProperty("tokenBridgeEmitterHex").GetString()!.ToLowerInvariant();

    private static string LocateState()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AZOA.WebAPI.csproj"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Cannot find the repository for the WTT fixture.");
        return Path.Combine(root.FullName, "tests/AZOA.WebAPI.IntegrationTests/Fixtures/Wtt/.state");
    }

    private JsonElement Read(string name)
    {
        var path = Path.Combine(_state, name);
        if (!File.Exists(Path.Combine(_state, "wtt-local.json")) || !File.Exists(path))
            throw new InvalidOperationException($"WTT local deployments are absent ({name}); run Fixtures/Wtt/run-wtt-fixtures.ps1 -Up first.");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
    }

    private static string Selector(string signature) => Sha3Keccack.Current.CalculateHash(signature)[..8];

    private static string Loopback(string url)
        => new Uri(url).Host is "127.0.0.1" or "[::1]" ? url : throw new InvalidOperationException($"Fixture RPC {url} is not numeric loopback.");

    private async Task<string> EvmCall(JsonElement evm, string to, string data)
    {
        using var response = await _http.PostAsJsonAsync(Loopback(evm.GetProperty("rpc").GetString()!), new
        {
            jsonrpc = "2.0", id = 1, method = "eth_call", @params = new object[] { new { to, data = "0x" + data }, "finalized" },
        });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("result").GetString()![2..];
    }

    private async Task<string> EvmWord(JsonElement evm, string to, string data) => "0x" + (await EvmCall(evm, to, data))[..64];

    private async Task<BigInteger> EvmUInt(JsonElement evm, string to, string signature)
        => BigInteger.Parse("0" + (await EvmCall(evm, to, Selector(signature)))[..64], System.Globalization.NumberStyles.HexNumber);

    private async Task<string[]> EvmGuardianSet(JsonElement evm, string core, uint index)
    {
        var words = await EvmCall(evm, core, Selector("getGuardianSet(uint32)") + index.ToString("x64"));
        int Word(int offset) => (int)BigInteger.Parse("0" + words.Substring(offset * 2, 64), System.Globalization.NumberStyles.HexNumber);
        var tuple = Word(0);
        var keys = tuple + Word(tuple);
        return Enumerable.Range(0, Word(keys)).Select(item => "0x" + words.Substring((keys + 32 + item * 32) * 2 + 24, 40)).ToArray();
    }

    private async Task<(string Owner, byte[] Data)> SolanaAccount(JsonElement solana, string address)
    {
        using var response = await _http.PostAsJsonAsync(Loopback(solana.GetProperty("rpc").GetString()!), new
        {
            jsonrpc = "2.0", id = 1, method = "getAccountInfo", @params = new object[] { address, new { encoding = "base64", commitment = "finalized" } },
        });
        var value = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetProperty("value");
        return (value.GetProperty("owner").GetString()!, Convert.FromBase64String(value.GetProperty("data")[0].GetString()!));
    }

    private async Task<string> AlgorandApprovalSha256(JsonElement algorand, string appId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Loopback(algorand.GetProperty("hostAlgod").GetString()!)}/v2/applications/{appId}");
        request.Headers.Add("X-Algo-API-Token", new string('a', 64));
        using var response = await _http.SendAsync(request);
        var program = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("params").GetProperty("approval-program").GetString()!;
        return Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(program))).ToLowerInvariant();
    }
}
