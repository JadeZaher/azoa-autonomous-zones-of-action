using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed partial class WttAlgorandSnapshotTests
{
    [Theory]
    [InlineData("exact", false)]
    [InlineData("genesis", true)]
    [InlineData("program", true)]
    [InlineData("core-binding", true)]
    [InlineData("emitter", true)]
    [InlineData("guardian-rekey", true)]
    [InlineData("account-round", true)]
    [InlineData("round-advanced", true)]
    [InlineData("duplicate-global", true)]
    [InlineData("block-genesis", true)]
    public async Task Guardian_evidence_binds_one_finalized_round_and_actual_protocol_state(string mutation, bool rejected)
    {
        using var rpc = new Rpc(mutation);
        var provider = Create(rpc);
        var result = await provider.ReadWttGuardianEvidenceAsync(3, 2, new string('b', 64));
        Assert.Equal(rejected, result.IsError);
        if (!rejected)
        {
            Assert.Equal(3u, result.Result!.GuardianSetIndex);
            Assert.Equal(4u, result.Result.CurrentGuardianSetIndex);
            Assert.Equal(new[] { new string('1', 40) }, result.Result.GuardianAddresses);
            Assert.Equal(1_800_000_000u, result.Result.ExpirationUnixSeconds);
            Assert.Equal("algorand:" + Rpc.Genesis, result.Result.ProtocolIdentity);
        }
    }

    [Theory]
    [InlineData("native", false)]
    [InlineData("original", false)]
    [InlineData("wrapped", false)]
    [InlineData("wrapped-supply", true)]
    [InlineData("wrapped-authority", true)]
    [InlineData("wrapped-storage", true)]
    [InlineData("wrapped-emitter", true)]
    [InlineData("creator-round", true)]
    public async Task Source_inspection_uses_actual_asset_origin_and_same_round(string mutation, bool rejected)
    {
        using var rpc = new Rpc(mutation);
        var provider = Create(rpc);
        var result = await provider.InspectWttSourceAsync(mutation == "native" ? "0" : ulong.MaxValue.ToString(), mutation == "native");
        Assert.Equal(rejected, result.IsError);
        if (rejected) return;
        Assert.Equal("algorand:" + Rpc.Genesis, result.Result.Network.ProtocolIdentity);
        Assert.Equal(mutation == "wrapped" ? (ushort)2 : (ushort)8, result.Result.Asset.OriginChain);
        Assert.Equal(mutation == "native" ? (byte)6 : mutation == "wrapped" ? (byte)8 : (byte)19, result.Result.Asset.Decimals);
        Assert.Equal(mutation == "native" ? (byte)6 : mutation == "wrapped" ? (byte)18 : (byte)8, result.Result.Asset.OriginDecimals);
        if (mutation == "original") Assert.EndsWith("ffffffffffffffff", result.Result.Asset.OriginAddress);
    }

    private static AlgorandProvider Create(Rpc rpc, AZOA.WebAPI.Interfaces.Managers.IKeyCustodyService? custody = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(new byte[] { 1, 2, 3 }));
        const string prefix = "AZOA:Algorand:Wtt:Networks:Devnet:";
        var settings = new Dictionary<string, string?> {
            ["Blockchain:DefaultNetwork"] = "devnet", ["Blockchain:Chains:0:ChainType"] = "Algorand",
            ["Blockchain:Chains:0:Devnet:IsEnabled"] = "true", ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://algod.test/",
            ["Blockchain:Chains:0:Devnet:IndexerUrl"] = "http://indexer.test/",
            [prefix + "Enabled"] = "true", [prefix + "GenesisHash"] = Rpc.Genesis,
            [prefix + "CoreBridge"] = "7", [prefix + "TokenBridge"] = "9",
            [prefix + "CoreApprovalSha256"] = hash, [prefix + "CoreClearSha256"] = hash,
            [prefix + "TokenBridgeApprovalSha256"] = hash, [prefix + "TokenBridgeClearSha256"] = hash
        };
        var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(settings).Build();
        return new AlgorandProvider(config, NullLogger<AlgorandProvider>.Instance,
            custody is null ? null : new AZOA.WebAPI.Core.Signing.TransactionSignerFactory([new AlgorandTransactionSigner()]),
            null, custody, scopeFactory, httpMessageHandler: rpc);
    }

    [Theory]
    [InlineData("exact", false)]
    [InlineData("fee-cap", true)]
    [InlineData("deposit-cap", true)]
    [InlineData("value-cap", true)]
    [InlineData("rekey", true)]
    public async Task Source_quote_covers_real_storage_and_publication_without_custody(string mutation, bool rejected)
    {
        using var rpc = new Rpc("quote-" + mutation);
        var provider = Create(rpc);
        var inspected = await provider.InspectWttSourceAsync("0", true);
        Assert.False(inspected.IsError, inspected.Message);
        var actor = Guid.NewGuid();
        var context = SigningContext.ForUser(actor, Guid.NewGuid()).ForChain("Algorand") with { Scope = AzoaScopes.WttSourceSign };
        var source = inspected.Result.Network;
        var target = source with { ChainType = "Ethereum", ProtocolChain = 2, ProtocolIdentity = "eip155:31337" };
        var intent = new WttOperationIntent(1, "bridge-test", actor, null, source, target, inspected.Result.Asset,
            new(Rpc.Recipient, context), new(Rpc.Recipient, context), "recipient", new string('a', 64), null, 6,
            WttPayloadCodec.Normalize("10000", "0", 6), 0, 0, false, false,
            new(mutation == "fee-cap" ? "0" : "1000000", mutation == "deposit-cap" ? "0" : "3000000", mutation == "value-cap" ? "0" : "100000"),
            new("1000000", "3000000", "100000"));
        var result = await provider.QuoteWttSourceAsync(intent);
        Assert.Equal(rejected, result.IsError);
        if (rejected) return;
        Assert.Equal(new[] { "EmitterStorage", "CustodyStorage", "Transfer" }, result.Result!.Stages.Select(stage => stage.Stage));
        Assert.Equal("1002000", result.Result.Stages[0].LockedDeposit);
        Assert.Equal("1002000", result.Result.Stages[1].LockedDeposit);
        Assert.Equal("11000", result.Result.Stages[2].Value);
        Assert.All(result.Result.Stages, stage => Assert.True(ulong.Parse(stage.NetworkFee) > 0));
        Assert.Equal("Source", result.Result.Phase);
    }

    [Theory]
    [InlineData("wrapped", true, true)]
    [InlineData("unregistered", false, false)]
    [InlineData("wrapped-recipient-frozen", true, false)]
    [InlineData("wrapped-recipient-missing", true, false)]
    public async Task Target_inspection_requires_real_registration_and_recipient_holding(string mutation, bool registered, bool ready)
    {
        using var rpc = new Rpc(mutation);
        var provider = Create(rpc);
        var origin = new WttAssetDescriptor("source", false, 18, 2, new string('c', 64), 18,
            new string('0', 64), new string('0', 64), "behavior");
        var result = await provider.InspectWttTargetAsync(origin, Rpc.Recipient, false);
        Assert.False(result.IsError, result.Message);
        Assert.Equal(registered, result.Result!.Registered);
        Assert.Equal(ready, result.Result.RecipientReady);
        Assert.Equal(registered ? ulong.MaxValue.ToString() : null, result.Result.AssetAddress);
        Assert.Equal(8, result.Result.Decimals);
        var forbiddenUnwrap = await provider.InspectWttTargetAsync(origin, Rpc.Recipient, true);
        Assert.True(forbiddenUnwrap.IsError);
    }

    private sealed class Rpc(string mutation, string? ownerAddress = null) : HttpMessageHandler
    {
        internal static readonly string Genesis = Convert.ToBase64String(Enumerable.Repeat((byte)4, 32).ToArray());
        internal static readonly string Recipient = new global::Algorand.Address(Enumerable.Repeat((byte)9, 32).ToArray()).ToString();
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Submit;
        internal Func<HttpRequestMessage, HttpResponseMessage?>? ReadOverride;
        internal string VerifierAddress = WttAlgorandTargetWire.VerifierAddress;
        private int statusReads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v2/transactions" && Submit is not null)
                return Submit(request, ct);
            Assert.Equal(HttpMethod.Get, request.Method);
            if (ReadOverride?.Invoke(request) is { } overridden) return Task.FromResult(overridden);
            var path = request.RequestUri!.AbsolutePath;
            object body;
            var wrapped = mutation.StartsWith("wrapped", StringComparison.Ordinal);
            var creator = wrapped ? WttAlgorandStorage.Address(9, 2, Enumerable.Repeat((byte)0xcc, 32).ToArray())
                : new global::Algorand.Address(Enumerable.Repeat((byte)3, 32).ToArray()).ToString();
            if (path == "/v2/status") body = new Dictionary<string, object> { ["last-round"] = ++statusReads > 1 && mutation == "round-advanced" ? 501 : 500 };
            else if (path == "/v2/transactions/params") body = new Dictionary<string, object> {
                ["genesis-hash"] = mutation == "genesis" ? "other" : Genesis, ["genesis-id"] = "localnet-v1",
                ["min-fee"] = 1000, ["fee"] = 0, ["last-round"] = 500 };
            else if (path == "/v2/blocks/500/hash") body = new { blockHash = new string('A', 52) };
            else if (path == "/v2/blocks/500") body = new { block = new { rnd = 500, ts = 1_799_999_990,
                gh = mutation == "block-genesis" ? "other" : Genesis } };
            else if (path.StartsWith("/v2/applications/", StringComparison.Ordinal))
            {
                var core = path.EndsWith("/7", StringComparison.Ordinal);
                var entries = new List<object>();
                if (core) { entries.Add(Integer("currentGuardianSetIndex"u8, 4)); entries.Add(Integer("MessageFee"u8, 1000));
                    entries.Add(Bytes("vphash"u8, new global::Algorand.Address(VerifierAddress).Bytes)); }
                else
                {
                    entries.Add(Integer("coreid"u8, mutation == "core-binding" ? 8UL : 7UL));
                    entries.Add(Bytes("coreAddr"u8, global::Algorand.Address.ForApplication(7).Bytes));
                    entries.Add(Bytes([.. "Chain"u8, 0, 2], Enumerable.Repeat(mutation == "emitter" ? (byte)0xaa : (byte)0xbb, 32).ToArray()));
                }
                if (mutation == "duplicate-global") entries.Add(entries[0]);
                body = new { id = core ? 7 : 9, @params = new Dictionary<string, object> {
                    ["approval-program"] = mutation == "program" ? "BAUG" : "AQID", ["clear-state-program"] = "AQID", ["global-state"] = entries } };
            }
            else if (path == "/v2/assets/" + ulong.MaxValue)
            {
                var bridge = global::Algorand.Address.ForApplication(9).ToString();
                body = new { index = ulong.MaxValue, @params = new Dictionary<string, object> {
                    ["creator"] = creator, ["decimals"] = wrapped ? 8 : 19,
                    ["total"] = wrapped && mutation != "wrapped-supply" ? 18446744073709550000UL : ulong.MaxValue,
                    ["manager"] = mutation == "wrapped-authority" ? creator : bridge,
                    ["reserve"] = creator, ["freeze"] = bridge, ["clawback"] = bridge,
                    ["unit-name"] = "TEST", ["name"] = "Protocol test" } };
            }
            else if (path == "/v2/accounts/" + (ownerAddress ?? Recipient))
            {
                if (mutation == "wrapped-recipient-missing") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                var participant = new Dictionary<string, object> { ["address"] = ownerAddress ?? Recipient, ["round"] = 500,
                    ["amount"] = 10_000_000, ["min-balance"] = 100_000,
                    ["assets"] = new[] { new Dictionary<string, object> { ["asset-id"] = ulong.MaxValue,
                        ["amount"] = 0UL, ["is-frozen"] = mutation == "wrapped-recipient-frozen" } } };
                if (mutation == "quote-rekey") participant["auth-addr"] = creator;
                body = participant;
            }
            else if (mutation == "unregistered" && path == "/v2/accounts/" + WttAlgorandStorage.Address(9, 2, Enumerable.Repeat((byte)0xcc, 32).ToArray()))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            else if (path == "/v2/accounts/" + creator)
            {
                var state = new Dictionary<string, object> { ["address"] = creator,
                    ["round"] = mutation == "creator-round" ? 499 : 500 };
                if (wrapped)
                {
                    state["auth-addr"] = global::Algorand.Address.ForApplication(9).ToString();
                    var blob = new byte[1905];
                    BinaryPrimitives.WriteUInt64BigEndian(blob, mutation == "wrapped-storage" ? 1UL : ulong.MaxValue);
                    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(16), 2);
                    blob.AsSpan(18, 32).Fill(mutation == "wrapped-emitter" ? (byte)0xaa : (byte)0xbb);
                    blob[59] = 2; blob.AsSpan(60, 32).Fill(0xcc);
                    BinaryPrimitives.WriteUInt16BigEndian(blob.AsSpan(92), 2); blob[94] = 18;
                    var pages = Enumerable.Range(0, 15).Select(page => Bytes([(byte)page], blob.AsSpan(page * 127, 127))).ToList();
                    pages.Add(Bytes("meta"u8, "asset"u8));
                    state["apps-local-state"] = new[] { new Dictionary<string, object> { ["id"] = 9, ["key-value"] = pages } };
                }
                body = state;
            }
            else if (mutation.StartsWith("quote-", StringComparison.Ordinal) && path.StartsWith("/v2/accounts/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            else if (path.StartsWith("/v2/accounts/", StringComparison.Ordinal))
            {
                var address = WttAlgorandStorage.Address(7, 3, "guardian"u8);
                Assert.Equal("/v2/accounts/" + address, path);
                var blob = new byte[1905]; blob[0] = 1; blob.AsSpan(1, 20).Fill(0x11);
                BinaryPrimitives.WriteUInt64BigEndian(blob.AsSpan(1000), 1_800_000_000);
                var pages = Enumerable.Range(0, 15).Select(page => Bytes([(byte)page], blob.AsSpan(page * 127, 127))).ToList();
                pages.Add(Bytes("meta"u8, "guardian"u8));
                body = new Dictionary<string, object> { ["address"] = address,
                    ["auth-addr"] = global::Algorand.Address.ForApplication(mutation == "guardian-rekey" ? 8UL : 7UL).ToString(),
                    ["round"] = mutation == "account-round" ? 499 : 500,
                    ["apps-local-state"] = new[] { new Dictionary<string, object> { ["id"] = 7, ["key-value"] = pages } } };
            }
            else throw new InvalidOperationException("Unexpected WTT read: " + path);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") });
        }
        private static object Integer(ReadOnlySpan<byte> key, ulong value)
            => new { key = Convert.ToBase64String(key), value = new { type = 2, @uint = value, bytes = "" } };
        private static object Bytes(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
            => new { key = Convert.ToBase64String(key), value = new { type = 1, @uint = 0, bytes = Convert.ToBase64String(value) } };
    }
}
