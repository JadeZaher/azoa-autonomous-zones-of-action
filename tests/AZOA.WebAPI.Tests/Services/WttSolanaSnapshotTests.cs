using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Solnet.Programs;
using Solnet.Wallet;
using Xunit;

namespace AZOA.WebAPI.Tests.Services;

public sealed class WttSolanaSnapshotTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("migrated", true)]
    [InlineData("paused", true)]
    [InlineData("invalid-pause", false)]
    [InlineData("truncated-config", false)]
    [InlineData("wrong-owner", false)]
    [InlineData("different-code", false)]
    [InlineData("wrong-core", false)]
    [InlineData("clock-other-slot", false)]
    [InlineData("missing-endpoint", false)]
    [InlineData("no-block", false)]
    public async Task StateUsesOneFinalizedOwnedCodePinnedSnapshot(string mutation, bool accepted)
    {
        using var rpc = new SnapshotRpc(mutation);
        var result = await rpc.Provider.ReadWttGuardianEvidenceAsync(7, 2, new string('a', 64));
        Assert.Equal(accepted, !result.IsError);
        Assert.Equal(1, rpc.SnapshotReads);
        if (accepted)
        {
            Assert.Equal("90:block-proof", result.Result!.BlockIdentity);
            Assert.Equal(200u, result.Result.ChainTimeUnixSeconds);
            Assert.Equal(new string('1', 40), Assert.Single(result.Result.GuardianAddresses));
        }
    }

    private sealed class SnapshotRpc : HttpMessageHandler
    {
        private const string Genesis = "EtWTRABZaYq6iMfeYKouRu166VU2xqa1wcaWoxPkrZBG";
        private readonly PublicKey _core = new Account().PublicKey;
        private readonly PublicKey _token = new Account().PublicKey;
        private readonly byte[] _code = [1, 2, 3, 4];
        private readonly string _mutation;
        internal SolanaProvider Provider { get; }
        internal int SnapshotReads { get; private set; }

        internal SnapshotRpc(string mutation)
        {
            _mutation = mutation;
            var prefix = "AZOA:Solana:Wtt:Networks:Devnet:";
            var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json").AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Blockchain:DefaultNetwork"] = "Devnet", ["Blockchain:Chains:0:ChainType"] = "Solana",
                ["Blockchain:Chains:0:Devnet:NodeUrl"] = "http://wtt.test/", ["AZOA:Solana:RpcRetryMaxAttempts"] = "0",
                ["AZOA:Solana:Networks:Devnet:GenesisHash"] = Genesis, [prefix + "Enabled"] = "true",
                [prefix + "CoreBridge"] = _core.Key, [prefix + "TokenBridge"] = _token.Key,
                [prefix + "CoreProgramDataHash"] = Convert.ToHexStringLower(SHA256.HashData(_code)),
                [prefix + "TokenBridgeProgramDataHash"] = Convert.ToHexStringLower(SHA256.HashData(_code)),
            }).Build();
            Provider = new(configuration, NullLogger<SolanaProvider>.Instance, null, null, null, this);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = json.RootElement; var method = root.GetProperty("method").GetString(); var args = root.GetProperty("params");
            object? result = method switch
            {
                "getGenesisHash" => Genesis,
                "getMultipleAccounts" => Snapshot(args),
                "getBlock" => Block(args),
                _ => throw new InvalidOperationException("Unexpected read or attempted signing/egress: " + method),
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { jsonrpc = "2.0", id = root.GetProperty("id").GetInt32(), result }), System.Text.Encoding.UTF8, "application/json") };
        }

        private object? Block(JsonElement args)
        {
            Assert.Equal(90UL, args[0].GetUInt64());
            Assert.Equal("finalized", args[1].GetProperty("commitment").GetString());
            Assert.Equal("none", args[1].GetProperty("transactionDetails").GetString());
            return _mutation == "no-block" ? null : new { blockhash = "block-proof" };
        }

        private object Snapshot(JsonElement args)
        {
            SnapshotReads++;
            Assert.Equal("finalized", args[1].GetProperty("commitment").GetString());
            Assert.Equal(9, args[0].GetArrayLength());
            Assert.Equal(Pda(_core, "Bridge"u8.ToArray()), args[0][0].GetString());
            Assert.Equal(Pda(_core, "GuardianSet"u8.ToArray(), [0, 0, 0, 7]), args[0][1].GetString());
            Assert.Equal(Pda(_token, "config"u8.ToArray()), args[0][2].GetString());
            Assert.Equal(Pda(_token, [0, 2], Enumerable.Repeat((byte)0xaa, 32).ToArray()), args[0][3].GetString());
            Assert.Equal(SysVars.ClockKey.Key, args[0][8].GetString());
            var bridge = new byte[24]; BinaryPrimitives.WriteUInt32LittleEndian(bridge, 8);
            var guardians = new byte[36]; BinaryPrimitives.WriteUInt32LittleEndian(guardians, 7);
            BinaryPrimitives.WriteUInt32LittleEndian(guardians.AsSpan(4), 1); guardians.AsSpan(8, 20).Fill(0x11);
            BinaryPrimitives.WriteUInt32LittleEndian(guardians.AsSpan(28), 100);
            BinaryPrimitives.WriteUInt32LittleEndian(guardians.AsSpan(32), 300);
            var endpoint = new byte[34]; BinaryPrimitives.WriteUInt16LittleEndian(endpoint, 2); endpoint.AsSpan(2).Fill(0xaa);
            var clock = new byte[40]; BinaryPrimitives.WriteUInt64LittleEndian(clock, _mutation == "clock-other-slot" ? 91UL : 90UL);
            BinaryPrimitives.WriteInt64LittleEndian(clock.AsSpan(32), 200);
            var config = (_mutation == "wrong-core" ? _token : _core).KeyBytes;
            if (_mutation is "migrated" or "paused" or "invalid-pause" or "truncated-config")
            {
                var extended = new byte[_mutation == "truncated-config" ? 136 : 137]; config.CopyTo(extended, 0);
                extended[32] = _mutation == "paused" ? (byte)1 : _mutation == "invalid-pause" ? (byte)2 : (byte)0;
                config = extended;
            }
            return new { context = new { slot = 90 }, value = new object?[]
            {
                AccountData(bridge, _mutation == "wrong-owner" ? _token.Key : _core.Key), AccountData(guardians, _core.Key),
                AccountData(config, _token.Key),
                _mutation == "missing-endpoint" ? null : AccountData(endpoint, _token.Key),
                Program(_core), ProgramData(), Program(_token), ProgramData(),
                AccountData(clock, "Sysvar1111111111111111111111111111111111111"),
            } };
        }

        private object Program(PublicKey key)
        {
            var data = new byte[36]; BinaryPrimitives.WriteUInt32LittleEndian(data, 2);
            SolanaNativeLoader.ProgramData(key).KeyBytes.CopyTo(data, 4);
            return AccountData(data, SolanaNativeLoader.Program.Key, true);
        }

        private object ProgramData()
        {
            var data = new byte[45 + _code.Length]; BinaryPrimitives.WriteUInt32LittleEndian(data, 3); _code.CopyTo(data, 45);
            if (_mutation == "different-code") data[^1]++;
            return AccountData(data, SolanaNativeLoader.Program.Key);
        }

        private static object AccountData(byte[] data, string owner, bool executable = false)
            => new { data = new[] { Convert.ToBase64String(data), "base64" }, owner, executable, lamports = 100UL };

        private static string Pda(PublicKey program, params byte[][] seeds)
        {
            Assert.True(PublicKey.TryFindProgramAddress(seeds, program, out var address, out _));
            return address.Key;
        }
    }
}
