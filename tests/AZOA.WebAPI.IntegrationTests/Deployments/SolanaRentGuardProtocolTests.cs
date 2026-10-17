using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using FluentAssertions;
using Solnet.Programs;
using Solnet.Rpc.Builders;
using Solnet.Rpc.Models;
using Solnet.Wallet;
using Xunit.Abstractions;

namespace AZOA.WebAPI.IntegrationTests.Deployments;

public sealed class SolanaRentGuardProtocolTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ActualImmutableElfAcceptsOnlyExactRentFrameAndReportsRuntimeCosts()
    {
        await using var rail = new LocalSolanaNativeRail();
        await rail.InitializeAsync();
        var path = Environment.GetEnvironmentVariable("AZOA_SOLANA_WTT_GUARD_ELF")
            ?? throw new InvalidOperationException("Build and configure the pinned local WTT guard ELF before this gate.");
        var expected = Environment.GetEnvironmentVariable("AZOA_SOLANA_WTT_GUARD_ELF_SHA256")
            ?? throw new InvalidOperationException("Independently pin the exact locally built WTT guard ELF SHA256.");
        var bytes = await File.ReadAllBytesAsync(path);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes)); digest.Should().Be(expected);
        var artifact = new NativeArtifact("solana-elf/1", JsonSerializer.SerializeToElement(new SolanaElfArtifact(
            Convert.ToBase64String(bytes), SourceDigest: digest)), "agave-v3.1.8/platform-tools-v1.52");
        var deployed = await rail.ExecuteAsync(await rail.QuoteAsync(artifact, immutable: true, capacity: checked((uint)bytes.Length)));
        var guard = new PublicKey(deployed.Completed.ResourceId!);
        var program = await Account(guard);
        var data = await Account(new PublicKey(program.AsSpan(4, 32).ToArray()));
        data[12].Should().Be(0, "the tested guard must have no upgrade authority");
        output.WriteLine("ELF SHA256 {0}; deployed allocated code SHA256 {1}", digest, Convert.ToHexStringLower(SHA256.HashData(data.AsSpan(45))));
        var rent = await Account(SysVars.RentKey);
        var exact = SolanaRentGuard.Build(guard, rent);
        var simulation = await Simulate(exact);
        simulation.GetProperty("err").ValueKind.Should().Be(JsonValueKind.Null);
        var units = simulation.GetProperty("unitsConsumed").GetUInt64(); units.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(200000);
        output.WriteLine("Actual Rent guard compute units {0}", units);
        await rail.SendSetupAsync(exact);
        foreach (var mutation in new[] { "rent", "short", "trailing", "opcode", "account", "extra", "writable" })
        {
            var bad = SolanaRentGuard.Build(guard, rent);
            switch (mutation)
            {
                case "rent": bad.Data[1] ^= 1; break;
                case "short": bad.Data = bad.Data[..^1]; break;
                case "trailing": bad.Data = [.. bad.Data, 0]; break;
                case "opcode": bad.Data[0] = 7; break;
                case "account": bad.Keys[0] = AccountMeta.ReadOnly(rail.Actor.PublicKey, true); break;
                case "extra": bad.Keys.Add(AccountMeta.ReadOnly(SysVars.ClockKey, false)); break;
                case "writable": bad.Keys[0] = AccountMeta.Writable(SysVars.RentKey, false); break;
            }
            (await Simulate(bad)).GetProperty("err").ValueKind.Should().NotBe(JsonValueKind.Null, mutation);
        }

        async Task<byte[]> Account(PublicKey address) => Convert.FromBase64String((await rail.RpcAsync("getAccountInfo", address.Key,
            new { encoding = "base64", commitment = "finalized" })).GetProperty("value").GetProperty("data")[0].GetString()!);
        async Task<JsonElement> Simulate(TransactionInstruction instruction)
        {
            var hash = (await rail.RpcAsync("getLatestBlockhash", new { commitment = "finalized" })).GetProperty("value").GetProperty("blockhash").GetString()!;
            var wire = new TransactionBuilder().SetRecentBlockHash(hash).SetFeePayer(rail.Actor).AddInstruction(instruction).Build(rail.Actor);
            wire.Length.Should().BeLessThanOrEqualTo(1232); output.WriteLine("Guard packet bytes {0}", wire.Length);
            return (await rail.RpcAsync("simulateTransaction", Convert.ToBase64String(wire),
                new { encoding = "base64", sigVerify = true, commitment = "finalized" })).GetProperty("value");
        }
    }
}
