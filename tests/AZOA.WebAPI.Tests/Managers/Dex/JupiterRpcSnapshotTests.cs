using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Programs;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed class JupiterRpcSnapshotTests
{
    [Fact]
    public void SnapshotRequiresActualClockAtExactReturnedContext()
    {
        var clock = new byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(clock, 100);
        BinaryPrimitives.WriteInt64LittleEndian(clock.AsSpan(32), 200);
        var account = Account(clock, "Sysvar1111111111111111111111111111111111111");
        var snapshot = Response(100, [account]);
        var result = JupiterRpcSnapshot.Read(JsonSerializer.SerializeToElement(snapshot), [SysVars.ClockKey.Key], 99, SysVars.ClockKey.Key);
        result.ChainTimestamp.Should().Be(200);
        foreach (var mutation in new Action<JsonObject>[] {
            root => root["context"]!["slot"] = 101,
            root => root["value"]![0]!["owner"] = SystemProgram.ProgramIdKey.Key,
            root => root["value"]![0]!["executable"] = true,
            root => root["value"] = new JsonArray(),
        })
        {
            var changed = snapshot.DeepClone().AsObject(); mutation(changed);
            Action read = () => JupiterRpcSnapshot.Read(JsonSerializer.SerializeToElement(changed), [SysVars.ClockKey.Key], 99, SysVars.ClockKey.Key);
            read.Should().Throw<InvalidDataException>();
        }
    }

    [Fact]
    public void AdaptiveOracleCannotBeReplacedByMissingOrSystemOwnedAccount()
    {
        var (address, bytes) = WhirlpoolAccountStateTests.Pool(1024);
        var pool = WhirlpoolAccountState.ReadPool(address, bytes);
        var oracle = WhirlpoolAccountState.OracleAddress(address);
        var accounts = new Dictionary<string, JsonElement> { [oracle] = JsonSerializer.SerializeToElement<object?>(null) };
        Action missing = () => new JupiterRpcSnapshot(100, 100, accounts).ReadOracle(pool);
        missing.Should().Throw<InvalidDataException>();
        accounts[oracle] = JsonSerializer.SerializeToElement(Account(WhirlpoolOracleStateTests.Oracle(pool), WhirlpoolAccountState.Program.Key));
        new JupiterRpcSnapshot(100, 100, accounts).ReadOracle(pool).Should().NotBeNull();
        accounts[oracle] = JsonSerializer.SerializeToElement(Account([], SystemProgram.ProgramIdKey.Key));
        missing.Should().Throw<InvalidDataException>();
    }

    private static JsonObject Response(ulong slot, JsonNode?[] accounts)
        => new() { ["context"] = new JsonObject { ["slot"] = slot }, ["value"] = new JsonArray(accounts) };

    private static JsonObject Account(byte[] bytes, string owner)
        => new() { ["owner"] = owner, ["executable"] = false,
            ["data"] = new JsonArray(Convert.ToBase64String(bytes), "base64") };
}
