using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text.Json;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nethereum.ABI;
using Nethereum.Signer;
using Nethereum.Util;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed partial class EthereumDexQuoteTests
{
    [Fact]
    public async Task Real_quoter_ABI_is_pinned_to_one_finalized_block_and_discloses_exact_allowance_stages()
    {
        using var fixture = new Fixture();
        var result = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        result.IsError.Should().BeFalse(result.Message);
        result.Result!.ExpectedOutput.Should().Be("2000"); result.Result.MinimumOutput.Should().Be("1990");
        result.Result.PlatformFee.Should().Be(fixture.Terms.PlatformFee);
        result.Result.Prerequisites.Select(effect => effect.Kind).Should().Equal("ResetAllowance", "ApproveExact");
        result.Result.Prerequisites[1].Amount.Should().Be("1010");
        result.Result.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
        fixture.Methods.Should().NotContain(method => method.Contains("send", StringComparison.OrdinalIgnoreCase) || method.Contains("sign", StringComparison.OrdinalIgnoreCase));
        fixture.QuoterCalls.Should().Be(1);
        fixture.CallBlocks.Should().Equal(fixture.QuoteBlock);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("factory")]
    [InlineData("pool")]
    [InlineData("decimals")]
    public async Task Untrusted_deployment_or_asset_evidence_is_refused_without_quote(string mutation)
    {
        using var fixture = new Fixture { Mutation = mutation };
        var result = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        result.IsError.Should().BeTrue(); result.Result.Should().BeNull();
        result.Code.Should().Be("InvalidProviderEvidence");
    }

    [Fact]
    public async Task Missing_independent_policy_key_is_unavailable_and_does_not_use_wallet_secret()
    {
        using var fixture = new Fixture(includePolicyKey: false);
        var result = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        result.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
        fixture.Methods.Should().BeEmpty();
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    public async Task FreshFetchCannotMakeAnOldOrFutureFinalizedHeadIntoCurrentQuote(string mutation)
    {
        using var fixture = new Fixture { Mutation = mutation };
        var result = await fixture.Provider.QuoteSwapAsync(fixture.Terms);
        result.Code.Should().Be(AzoaErrorCodes.DependencyUnavailable);
        result.Result.Should().BeNull();
        fixture.QuoterCalls.Should().Be(0);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        internal readonly EthereumProvider Provider;
        internal readonly DexQuoteTerms Terms;
        internal readonly List<string> Methods = [];
        internal int QuoterCalls;
        internal string? Mutation;
        internal bool PreparationEnabled;
        internal BigInteger CurrentAllowance = 5;
        internal readonly EthECKey OwnerKey = EthECKey.GenerateKey();
        internal readonly FakeEthereumPreparationStore Preparations = new();
        internal readonly IConfigurationRoot Configuration;
        internal readonly Allowances AllowanceStore = new();
        internal readonly List<string> Sent = [];
        internal readonly HashSet<string> CallBlocks = [];
        /// <summary>Scripted chain responses consulted before the quote defaults; return Null for a JSON null result.</summary>
        internal Func<string, JsonElement, object?>? Chain;
        internal static readonly object Null = new();
        internal BigInteger Head = 10;
        internal BigInteger HeadTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60;
        internal string QuoteBlock => _block;
        internal string Genesis => _genesis;
        private readonly ServiceProvider _services;
        private readonly string _policy;
        private readonly string _hash = EthereumHex.Data(new Sha3Keccack().CalculateHash(new byte[] { 0x60, 0x00 }));
        private readonly string _version = "0x" + new string('a', 64);
        private readonly string _block = "0x" + new string('b', 64);
        private readonly string _genesis = "0x" + new string('c', 64);

        internal Fixture(bool includePolicyKey = true)
        {
            var key = EthECKey.GenerateKey(); _policy = key.GetPublicAddress();
            var prefix = "AZOA:Ethereum:Dex:Networks:Devnet:";
            var settings = new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "Devnet", ["Blockchain:Chains:0:ChainType"] = "Ethereum",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://dex-ethereum.test/", ["Blockchain:Chains:0:Devnet:ChainId"] = "31337",
                ["AZOA:Ethereum:RpcRetryMaxAttempts"] = "0", ["AZOA:Ethereum:PlatformPrivateKey"] = "0x" + Convert.ToHexString(key.GetPrivateKeyAsBytes()),
                ["AZOA:Ethereum:AllocatorActivationEpoch"] = "dex-fixture",
                [prefix + "Enabled"] = "true", [prefix + "ArchiveStateConfirmed"] = "true", [prefix + "PoolFee"] = "3000", [prefix + "ExecutionLifetimeSeconds"] = "3600", [prefix + "Executor"] = Address(1),
                [prefix + "MaximumFinalizedLagSeconds"] = "1800", [prefix + "MaximumFutureSkewSeconds"] = "60",
                [prefix + "Router"] = Address(2), [prefix + "Factory"] = Address(3), [prefix + "Quoter"] = Address(4),
                [prefix + "WrappedNative"] = Address(5), [prefix + "PolicySigner"] = _policy,
                [prefix + "PolicyKeyVersion"] = _version, [prefix + "GenesisHash"] = _genesis,
                ["AZOA:Ethereum:Dex:PolicyKeys:" + _version[2..] + ":PrivateKey"] = includePolicyKey ? "0x" + Convert.ToHexString(key.GetPrivateKeyAsBytes()) : null
            };
            foreach (var name in new[] { "Executor", "Router", "Factory", "Quoter", "WrappedNative" }) settings[prefix + name + "CodeHash"] = _hash;
            foreach (var name in new[] { "Reset", "Approve", "Swap", "Cleanup" }) settings[prefix + name + "GasLimit"] = "100000";
            foreach (var address in new[] { Address(6), Address(7) })
            {
                settings[prefix + "Tokens:" + address + ":CodeHash"] = _hash;
                settings[prefix + "Tokens:" + address + ":SourceRevision"] = "fixture-reviewed-token";
                settings[prefix + "Tokens:" + address + ":Standard"] = "ERC20Immutable";
            }
            Configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(settings).Build();
            _services = new ServiceCollection().AddSingleton<IEthereumDexAllowanceStore>(AllowanceStore).BuildServiceProvider();
            Provider = new(Configuration, NullLogger<EthereumProvider>.Instance, null, null,
                _services.GetRequiredService<IServiceScopeFactory>(), this, Preparations);
            var actor = Guid.NewGuid(); var owner = new DexParticipant(actor, Guid.NewGuid(), OwnerKey.GetPublicAddress());
            Preparations.SeedCheckpoint("eip155:31337", owner.Address.ToLowerInvariant(), "dex-fixture");
            Terms = new(1, actor, null, owner, owner, owner.Address, new("Ethereum", "Devnet", "eip155:31337"),
                new(Address(6), "IN", 6, false), new(Address(7), "OUT", 6, false), "1010", "1000", "0", "0", 50,
                new(NodeFeeSchedule.InputBaseUnitsV1, 1, "10", 0, "10", Address(9), _version[2..]),
                new("10000000000000000", "0", "10000000000000000"), "UniswapV3", "classic-exact-input/1",
                JsonSerializer.SerializeToElement(new { }), DateTimeOffset.UtcNow, []);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = document.RootElement; var method = root.GetProperty("method").GetString()!; var args = root.GetProperty("params");
            Methods.Add(method);
            if (method == "eth_sendRawTransaction") Sent.Add(args[0].GetString()!);
            var custom = Chain?.Invoke(method, args);
            if (custom is RpcFailure failure)
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(),
                    error = new { code = -32000, message = failure.Message } })) };
            object? result = custom is not null ? ReferenceEquals(custom, Null) ? null : custom : method switch
            {
                "eth_chainId" => "0x7a69",
                "eth_getBlockByNumber" when args[0].GetString() is "0x0" or "finalized" or "latest" => new
                {
                    number = args[0].GetString() == "0x0" ? "0x0" : EthereumHex.Quantity(Head),
                    hash = args[0].GetString() == "0x0" ? _genesis : BlockHash(Head),
                    parentHash = BlockHash(Head - 1),
                    timestamp = EthereumHex.Quantity(Mutation == "stale" ? new BigInteger(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 4000)
                        : Mutation == "future" ? new BigInteger(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 180) : HeadTime),
                    baseFeePerGas = "0x3b9aca00"
                },
                "eth_maxPriorityFeePerGas" => "0x3b9aca00",
                "eth_getBalance" => "0x3635c9adc5dea00000",
                "eth_getCode" => Mutation == "code" ? "0x6001" : "0x6000",
                "eth_call" => Call(args),
                "eth_estimateGas" when PreparationEnabled => "0x5208",
                "eth_getTransactionCount" when PreparationEnabled => "0x0",
                _ => throw new InvalidOperationException("Unexpected value effect during quote: " + method)
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result })) };
        }

        /// <summary>Finalized block identity by height; height 10 is the quote's pinned block.</summary>
        internal string BlockHash(BigInteger number) => number == 10 ? _block : "0x" + number.ToString("x64", CultureInfo.InvariantCulture);

        private string Call(JsonElement args)
        {
            CallBlocks.Add(args[1].GetProperty("blockHash").GetString()!);
            args[1].GetProperty("requireCanonical").GetBoolean().Should().BeTrue();
            var data = args[0].GetProperty("data").GetString()!;
            bool Is(string signature) => data.StartsWith("0x" + EthereumAbi.Selector(signature), StringComparison.Ordinal);
            string AddressResult(string address) => "0x" + address[2..].PadLeft(64, '0');
            string Number(BigInteger number) => EthereumHex.Data(new ABIEncode().GetABIEncoded(new ABIValue("uint256", number)));
            if (Is("router()")) return AddressResult(Address(2));
            if (Is("factory()")) return AddressResult(Address(Mutation == "factory" ? 20 : 3));
            if (Is("wrappedNative()") || Is("WETH9()")) return AddressResult(Address(5));
            if (Is("policySigner()")) return AddressResult(_policy);
            if (Is("policyKeyVersion()")) return _version;
            if (Is("routerCodeHash()") || Is("factoryCodeHash()") || Is("wrappedNativeCodeHash()")) return _hash;
            if (Is("decimals()")) return Number(Mutation == "decimals" ? 9 : 6);
            if (Is("getPool(address,address,uint24)")) return AddressResult(Address(10));
            if (Is("fee()")) return Number(3000);
            if (Is("token0()")) return AddressResult(Address(Mutation == "pool" ? 21 : 6));
            if (Is("token1()")) return AddressResult(Address(7));
            if (Is("quoteExactInputSingle(address,address,uint24,uint256,uint160)")) { QuoterCalls++; return Number(2000); }
            if (Is("allowance(address,address)")) return Number(CurrentAllowance);
            if (Is("balanceOf(address)")) return Number(100000);
            throw new InvalidOperationException("Unreviewed quote call.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _services.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed record RpcFailure(string Message);

    internal sealed class Allowances : IEthereumDexAllowanceStore
    {
        private readonly Dictionary<string, EthereumDexAllowance> _rows = [];
        internal readonly List<(string Id, string Operation, string Stage)> Released = [];
        public Task<EthereumDexAllowance?> GetAsync(string id, CancellationToken ct = default)
            => Task.FromResult(_rows.GetValueOrDefault(id));
        public Task<EthereumDexAllowance?> ClaimAsync(EthereumDexAllowance candidate, CancellationToken ct = default)
        {
            if (_rows.TryGetValue(candidate.Id, out var prior))
                return Task.FromResult<EthereumDexAllowance?>(prior.OperationId == candidate.OperationId && prior.IntentHash == candidate.IntentHash ? prior : null);
            _rows.Add(candidate.Id, candidate);
            return Task.FromResult<EthereumDexAllowance?>(candidate);
        }
        public Task<bool> ReleaseAsync(string id, string operationId, string stageId, CancellationToken ct = default)
        {
            if (!_rows.TryGetValue(id, out var row) || row.OperationId != operationId) return Task.FromResult(false);
            row.State = "Released"; row.ReleaseStageId = stageId;
            Released.Add((id, operationId, stageId));
            return Task.FromResult(true);
        }
    }

    private static string Address(int value) => "0x" + value.ToString("x40", CultureInfo.InvariantCulture);
}
