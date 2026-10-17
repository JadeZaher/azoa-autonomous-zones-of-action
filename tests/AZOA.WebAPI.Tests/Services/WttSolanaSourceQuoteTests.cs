using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Wtt;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Services.Wormhole;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Solnet.Programs;
using Solnet.Wallet;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed partial class WttSolanaSourceQuoteTests
{
    [Fact]
    public async Task QuoteMeasuresClosedGuardedV0MessagesAndReservesCleanupWithoutCustodyOrEgress()
    {
        using var rpc = new SourceRpc(); var intent = await rpc.IntentAsync();
        var quoted = await rpc.Provider.QuoteWttSourceAsync(intent);
        Assert.False(quoted.IsError, quoted.Message);
        Assert.Equal(new[] { "Attest", "Cleanup:Attest", "Transfer", "Cleanup:Transfer" }, quoted.Result!.Stages.Select(stage => stage.Stage));
        Assert.Equal(4, rpc.Simulated.Count);
        foreach (var message in rpc.Simulated)
        {
            var parsed = SolanaMessageEnvelope.Read(message);
            Assert.True(parsed.VersionZero); Assert.Equal(3, parsed.RequiredSignatures);
            var resolved = SolanaMessageEnvelope.Resolve(parsed, [rpc.TableSnapshot]);
            Assert.Equal(rpc.Guard.Key, resolved[parsed.Instructions[2].ProgramIndex].Key);
            Assert.Equal(90, parsed.Instructions[2].Data.Length);
            Assert.Equal(17UL, BinaryPrimitives.ReadUInt64LittleEndian(parsed.Instructions[2].Data.AsSpan(1)));
            Assert.Equal(SystemProgram.ProgramIdKey.Key, resolved[parsed.Instructions[3].ProgramIndex].Key);
            Assert.Equal(rpc.Owner.Key, resolved[parsed.Instructions[3].AccountIndices[0]].Key);
            Assert.NotEqual(rpc.Owner.Key, resolved[parsed.Instructions[3].AccountIndices[1]].Key);
            Assert.True(1 + 64 * parsed.RequiredSignatures + message.Length <= 1232);
        }
        Assert.All(quoted.Result.Stages, stage => Assert.Equal("5000", stage.NetworkFee));
        Assert.All(quoted.Result.Stages.Where(stage => stage.Stage.StartsWith("Cleanup:", StringComparison.Ordinal)),
            stage => { Assert.Equal("0", stage.Value); Assert.Equal("0", stage.LockedDeposit); });
        Assert.All(quoted.Result.Stages.Where(stage => !stage.Stage.StartsWith("Cleanup:", StringComparison.Ordinal)),
            stage => { Assert.Equal("17", stage.Value); Assert.True(ulong.Parse(stage.LockedDeposit) > 0); });
    }

    [Theory]
    [InlineData("guard-mutable")]
    [InlineData("source-delegated")]
    [InlineData("source-frozen")]
    [InlineData("table-deactivating")]
    [InlineData("simulation-failed")]
    [InlineData("fee-cap")]
    [InlineData("rent-cap")]
    public async Task ChangedAuthoritySimulationOrWholePlanCapCannotProduceAnExecutableQuote(string mutation)
    {
        using var rpc = new SourceRpc(); var intent = await rpc.IntentAsync(); rpc.Mutation = mutation;
        if (mutation == "fee-cap") intent = intent with { SourceBudget = intent.SourceBudget with { MaximumNetworkFee = "19999" } };
        if (mutation == "rent-cap") intent = intent with { SourceBudget = intent.SourceBudget with { MaximumLockedDeposit = "0" } };
        var quoted = await rpc.Provider.QuoteWttSourceAsync(intent);
        Assert.True(quoted.IsError);
    }

    private sealed partial class SourceRpc : HttpMessageHandler
    {
        private const string Genesis = "EtWTRABZaYq6iMfeYKouRu166VU2xqa1wcaWoxPkrZBG";
        private readonly PublicKey _core = new Account().PublicKey;
        private readonly PublicKey _token = new Account().PublicKey;
        private readonly PublicKey _mint = new Account().PublicKey;
        private readonly PublicKey _table = new Account().PublicKey;
        private readonly Dictionary<string, object?> _accounts = new(StringComparer.Ordinal);
        private readonly byte[] _code = [1, 2, 3, 4];
        private readonly byte[] _holding;
        private readonly byte[] _guardData = [];
        public Account OwnerAccount { get; } = new();
        public PublicKey Owner => OwnerAccount.PublicKey;
        public PublicKey Guard { get; } = new Account().PublicKey;
        public SolanaProvider Provider { get; }
        public SolanaLookupTableSnapshot TableSnapshot { get; }
        public string? Mutation { get; set; }
        public List<byte[]> Simulated { get; } = [];
        private readonly IConfiguration _configuration;
        public long Height { get; set; } = 100;
        public bool LoseSendResponse { get; set; }
        public List<string> Sent { get; } = [];

        public SourceRpc(bool native = false)
        {
            if (native) _mint = SolanaSeededNativeAccount.Mint;
            var hash = Convert.ToHexStringLower(SHA256.HashData(_code));
            foreach (var program in new[] { _core, _token, Guard })
            {
                var data = new byte[36]; BinaryPrimitives.WriteUInt32LittleEndian(data, 2);
                SolanaNativeLoader.ProgramData(program).KeyBytes.CopyTo(data, 4);
                _accounts[program.Key] = AccountData(data, SolanaNativeLoader.Program.Key, true);
                var code = new byte[45 + _code.Length]; BinaryPrimitives.WriteUInt32LittleEndian(code, 3);
                BinaryPrimitives.WriteUInt64LittleEndian(code.AsSpan(4), 50); _code.CopyTo(code, 45);
                _accounts[SolanaNativeLoader.ProgramData(program).Key] = AccountData(code, SolanaNativeLoader.Program.Key);
                if (program.Key == Guard.Key) _guardData = code;
            }
            var bridge = new byte[24]; BinaryPrimitives.WriteUInt64LittleEndian(bridge.AsSpan(16), 17);
            _accounts[WttSolanaAccounts.Derive(_core, "Bridge"u8.ToArray()).Key] = AccountData(bridge, _core.Key);
            _accounts[WttSolanaAccounts.Derive(_token, "config"u8.ToArray()).Key] = AccountData(_core.KeyBytes, _token.Key);
            var clock = new byte[40]; BinaryPrimitives.WriteUInt64LittleEndian(clock, 90);
            BinaryPrimitives.WriteInt64LittleEndian(clock.AsSpan(32), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _accounts[SysVars.ClockKey.Key] = AccountData(clock, "Sysvar1111111111111111111111111111111111111");
            var rent = new byte[17]; BinaryPrimitives.WriteUInt64LittleEndian(rent, 3480);
            BinaryPrimitives.WriteDoubleLittleEndian(rent.AsSpan(8), 2); rent[16] = 50;
            _accounts[SysVars.RentKey.Key] = AccountData(rent, "Sysvar1111111111111111111111111111111111111");
            var mint = new byte[82]; mint[44] = native ? (byte)9 : (byte)6; mint[45] = 1; BinaryPrimitives.WriteUInt64LittleEndian(mint.AsSpan(36), 1000000);
            _accounts[_mint.Key] = AccountData(mint, TokenProgram.ProgramIdKey.Key);
            _holding = new byte[165]; _mint.KeyBytes.CopyTo(_holding, 0); Owner.KeyBytes.CopyTo(_holding, 32);
            BinaryPrimitives.WriteUInt64LittleEndian(_holding.AsSpan(64), 1000000); _holding[108] = 1;
            if (native)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(_holding.AsSpan(109), 1);
                BinaryPrimitives.WriteUInt64LittleEndian(_holding.AsSpan(113), 2039280);
                _accounts[Owner.Key] = AccountData([], SystemProgram.ProgramIdKey.Key);
            }
            _accounts[AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(Owner, _mint).Key] = AccountData(_holding, TokenProgram.ProgramIdKey.Key);
            var emitter = WttSolanaAccounts.Derive(_token, "emitter"u8.ToArray());
            var keys = _accounts.Keys.Select(value => new PublicKey(value)).Concat(new[]
            {
                WttSolanaAccounts.Derive(_token, _mint.KeyBytes), WttSolanaAccounts.Derive(_token, "authority_signer"u8.ToArray()),
                WttSolanaAccounts.Derive(_token, "custody_signer"u8.ToArray()), emitter,
                WttSolanaAccounts.Derive(_core, "Sequence"u8.ToArray(), emitter.KeyBytes), WttSolanaAccounts.Derive(_core, "fee_collector"u8.ToArray()),
                WttSolanaAccounts.Derive(_token, "meta"u8.ToArray(), _mint.KeyBytes), SolanaNftMetadata.Metadata(_mint),
            }).ToArray();
            var table = new byte[56 + keys.Length * 32]; BinaryPrimitives.WriteUInt32LittleEndian(table, 1);
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(4), ulong.MaxValue);
            BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(12), 80);
            for (var index = 0; index < keys.Length; index++) keys[index].KeyBytes.CopyTo(table, 56 + index * 32);
            TableSnapshot = new(_table.Key, SolanaLookupTableSnapshot.Program, Convert.ToBase64String(table), 90);
            _accounts[_table.Key] = AccountData(table, SolanaLookupTableSnapshot.Program);
            var prefix = "AZOA:Solana:Wtt:Networks:Devnet:";
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "Devnet", ["Blockchain:Chains:0:ChainType"] = "Solana",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://wtt.test/", ["AZOA:Solana:RpcRetryMaxAttempts"] = "0",
                ["AZOA:Solana:Networks:Devnet:GenesisHash"] = Genesis, [prefix + "Enabled"] = "true",
                [prefix + "CoreBridge"] = _core.Key, [prefix + "TokenBridge"] = _token.Key,
                [prefix + "CoreProgramDataHash"] = hash, [prefix + "TokenBridgeProgramDataHash"] = hash,
                [prefix + "CostGuardProgram"] = Guard.Key, [prefix + "CostGuardProgramDataHash"] = hash,
                [prefix + "MaximumComputeUnits"] = "1400000", [prefix + "MicroLamportsPerComputeUnit"] = "0",
                [prefix + "LookupTables:0"] = _table.Key,
            }).Build();
            _configuration = config;
            Provider = new(config, NullLogger<SolanaProvider>.Instance, null, null, null, this);
        }

        public async Task<WttOperationIntent> IntentAsync()
        {
            var source = await Provider.InspectWttSourceAsync(_mint.Key, false); Assert.False(source.IsError, source.Message);
            var actor = Guid.NewGuid(); var context = new SigningContext(actor, Guid.NewGuid(), false, Scope: AzoaScopes.WttSourceSign, ChainType: "Solana");
            var authority = new WttSigningAuthority(Owner.Key, context);
            var target = new WttNetworkDescriptor("Ethereum", ChainNetwork.Devnet, "eip155:31337", "Devnet", 2,
                "0x" + new string('1', 40), "0x" + new string('2', 40), new string('3', 64),
                new(2, new string('4', 64), new string('5', 64), new string('6', 64)), new string('7', 64));
            return new(1, Guid.NewGuid().ToString("N"), actor, null, source.Result.Network, target, source.Result.Asset,
                authority, authority, "0x" + new string('8', 40), new string('8', 64), null, 6,
                WttPayloadCodec.Normalize(_mint.Key == SolanaSeededNativeAccount.Mint.Key ? "1000" : "100", "0", source.Result.Asset.Decimals), 42, 32, false, true,
                new("1000000000", "1000000000", "1000000000"), new("1000000000", "1000000000", "1000000000"));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)); var root = document.RootElement;
            var method = root.GetProperty("method").GetString(); var args = root.GetProperty("params");
            object? result = method switch
            {
                "getGenesisHash" => Genesis,
                "getMultipleAccounts" => Snapshot(args),
                "getLatestBlockhash" => new { context = new { slot = 90 }, value = new { blockhash = new Account().PublicKey.Key, lastValidBlockHeight = 500 } },
                "getBlockHeight" => Height,
                "sendTransaction" => Send(args),
                "getTransaction" => FinalizedTransaction,
                "getBlock" => FinalizedBlock(),
                "getAccountInfo" => PublicationAccount(args),
                "getSignaturesForAddress" => PublicationHistory(args),
                "simulateTransaction" => Simulate(args),
                "getFeeForMessage" => new { context = new { slot = 90 }, value = 5000 },
                _ => throw new InvalidOperationException("Unexpected operation or custody/egress during quote: " + method),
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result }), System.Text.Encoding.UTF8, "application/json") };
        }

        private object Snapshot(JsonElement args)
        {
            Assert.Equal("finalized", args[1].GetProperty("commitment").GetString());
            if (CleanupSnapshot(args) is { } cleanup) return cleanup;
            if (NativeOutputSnapshot(args) is { } nativeOutput) return nativeOutput;
            if (Mutation == "guard-mutable") { _guardData[12] = 1; _accounts[SolanaNativeLoader.ProgramData(Guard).Key] = AccountData(_guardData, SolanaNativeLoader.Program.Key); }
            if (Mutation is "source-delegated" or "source-frozen")
            {
                if (Mutation == "source-delegated") { BinaryPrimitives.WriteUInt32LittleEndian(_holding.AsSpan(72), 1); Guard.KeyBytes.CopyTo(_holding, 76); }
                else _holding[108] = 2;
                _accounts[AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(Owner, _mint).Key] = AccountData(_holding, TokenProgram.ProgramIdKey.Key);
            }
            if (Mutation == "table-deactivating")
            {
                var table = Convert.FromBase64String(TableSnapshot.DataBase64); BinaryPrimitives.WriteUInt64LittleEndian(table.AsSpan(4), 89);
                _accounts[_table.Key] = AccountData(table, SolanaLookupTableSnapshot.Program);
            }
            return new { context = new { slot = 90 }, value = args[0].EnumerateArray().Select(value => _accounts.GetValueOrDefault(value.GetString()!)).ToArray() };
        }

        private object Simulate(JsonElement args)
        {
            Assert.False(args[1].GetProperty("sigVerify").GetBoolean());
            var wire = Convert.FromBase64String(args[0].GetString()!); var message = wire[(1 + wire[0] * 64)..];
            Simulated.Add(message); Assert.All(wire[1..(1 + wire[0] * 64)], value => Assert.Equal((byte)0, value));
            var slot = args[1].TryGetProperty("minContextSlot", out var minimum) ? Math.Max(90L, minimum.GetInt64()) : 90L;
            return new { context = new { slot }, value = new { err = Mutation == "simulation-failed" ? (object)new { InstructionError = new object[] { 2, 1 } } : null, unitsConsumed = 100000 } };
        }

        private string Send(JsonElement args)
        {
            Assert.False(args[1].GetProperty("skipPreflight").GetBoolean());
            Assert.Equal("finalized", args[1].GetProperty("preflightCommitment").GetString());
            var encoded = args[0].GetString()!; Sent.Add(encoded); var wire = Convert.FromBase64String(encoded);
            var id = SolanaMessageEnvelope.VerifyWire(wire, wire[(1 + wire[0] * 64)..]);
            if (LoseSendResponse) { LoseSendResponse = false; throw new HttpRequestException("Simulated loss after accepting exact WTT wire."); }
            return id;
        }

        private static object AccountData(byte[] data, string owner, bool executable = false)
            => new { data = new[] { Convert.ToBase64String(data), "base64" }, owner, executable, lamports = 1000000000UL };
    }
}
