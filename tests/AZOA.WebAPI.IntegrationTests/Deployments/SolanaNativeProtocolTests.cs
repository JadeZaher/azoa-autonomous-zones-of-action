using System.Security.Cryptography;
using System.Text.Json;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Deployments;
using FluentAssertions;
using Solnet.Programs;
using Solnet.Wallet;

namespace AZOA.WebAPI.IntegrationTests.Deployments;

public sealed class SolanaNativeProtocolTests
{
    [Fact]
    public async Task RealElf_CreateCallUpgradeAndImmutableAuthority_KeepExactStageHistory()
    {
        await using var rail = new LocalSolanaNativeRail();
        await rail.InitializeAsync();
        var one = Elf("AZOA_SOLANA_NATIVE_ELF_ONE");
        var two = Elf("AZOA_SOLANA_NATIVE_ELF_TWO");
        CanonicalJson.Hash(one).Should().NotBe(CanonicalJson.Hash(two));
        var capacity = (uint)Math.Max(Bytes(one).Length, Bytes(two).Length);
        var create = await rail.QuoteAsync(one, capacity: capacity);
        var deployed = await rail.ExecuteAsync(create);
        deployed.History.Select(item => item.Prepared.Stage).Should().Contain("CreateBuffer").And.Contain("Deploy");
        var program = deployed.Completed.ResourceId!;
        var firstHash = await ProgramHashAsync(rail, program);
        var call = await rail.QuoteAsync(Instruction(program, firstHash), "Call");
        var called = await rail.ExecuteAsync(call);
        called.History.Should().ContainSingle();

        NativeOperationIntent? staleCall = null;
        NativeStagePreparation? staleWire = null;
        var upgrade = await rail.QuoteAsync(two, "Upgrade", program, immutable: true, expectedProgramHash: firstHash);
        var upgraded = await rail.ExecuteAsync(upgrade, async stage =>
        {
            if (stage != "Upgrade") return;
            staleCall = await rail.QuoteAsync(Instruction(program, firstHash), "Call");
            var prepared = await rail.Provider.PrepareNativeStageAsync(staleCall, "Call", 0);
            prepared.IsError.Should().BeFalse(prepared.Message);
            staleWire = prepared.Result!;
        });
        upgraded.History.Select(item => item.Prepared.Stage).Should().Contain("Upgrade").And.Contain("RemoveAuthority");
        (await ProgramHashAsync(rail, program)).Should().NotBe(firstHash);
        var prior = await rail.Provider.ObserveNativeStageAsync(call, called.History.Single().Prepared);
        prior.Result!.Verdict.Should().Be("Confirmed", "later code updates do not rewrite the code executed by a prior finalized call");
        (await rail.Provider.GetNextStageAsync(create, deployed.History)).Result!.Complete.Should().BeTrue();

        staleWire.Should().NotBeNull();
        var staleSent = await rail.Provider.SubmitNativeStageAsync(staleCall!, staleWire!);
        staleSent.IsError.Should().BeFalse(staleSent.Message);
        await rail.FinalizedAsync(staleWire!.TransactionId);
        var staleObserved = await rail.Provider.ObserveNativeStageAsync(staleCall!, staleWire);
        staleObserved.Result!.Verdict.Should().NotBe("Confirmed", "successful execution of different code cannot prove the pinned artifact");

        var newCall = await rail.QuoteAsync(Instruction(program, await ProgramHashAsync(rail, program)), "Call");
        (await rail.ExecuteAsync(newCall)).Completed.Complete.Should().BeTrue();
        (await rail.Provider.GetNextStageAsync(upgrade, upgraded.History.AsEnumerable().Reverse().ToArray())).IsError.Should().BeTrue();
        (await rail.Provider.QuoteNativeOperationAsync(upgrade with { OperationKey = Guid.NewGuid().ToString("N"), Quote = null }))
            .IsError.Should().BeTrue("removed upgrade authority cannot create another upgrade plan");
    }

    [Fact]
    public async Task RealMint_AtaMetadataUpdateAndClose_UseNativeModulesAndFinalizedReceipts()
    {
        await using var rail = new LocalSolanaNativeRail();
        await rail.InitializeAsync();
        var owner = rail.Actor.PublicKey;
        var seed = Guid.NewGuid().ToString("N");
        var mint = new PublicKey(SHA256.HashData([.. owner.KeyBytes, .. System.Text.Encoding.UTF8.GetBytes(seed), .. TokenProgram.ProgramIdKey.KeyBytes]));
        var rent = (await rail.RpcAsync("getMinimumBalanceForRentExemption", TokenProgram.MintAccountDataSize, new { commitment = "finalized" })).GetUInt64();
        await rail.SendSetupAsync(SystemProgram.CreateAccountWithSeed(owner, mint, owner, seed, rent,
            (ulong)TokenProgram.MintAccountDataSize, TokenProgram.ProgramIdKey), TokenProgram.InitializeMint(mint, 0, owner, owner));
        var ata = await rail.ExecuteAsync(await rail.QuoteAsync(AccountArtifact(new("CreateAta", mint.Key, owner.Key)), "Account"));
        var address = AssociatedTokenAccountProgram.DeriveAssociatedTokenAccount(owner, mint).Key;
        ata.Completed.ResourceId.Should().Be(address);
        var metadata = await rail.ExecuteAsync(await rail.QuoteAsync(AccountArtifact(new("CreateMetadata", mint.Key, owner.Key,
            Name: "Native fixture", Symbol: "AZOA", Uri: "ipfs://native-fixture-v1", Mutable: true)), "Account"));
        var metadataAddress = metadata.Completed.ResourceId!;
        var current = await AccountBytesAsync(rail, metadataAddress);
        var update = AccountArtifact(new("UpdateMetadata", mint.Key, owner.Key, Name: "Native fixture revised",
            Uri: "ipfs://native-fixture-v2", Mutable: false, ExpectedMetadataHash: Hash(current)));
        var updateIntent = await rail.QuoteAsync(update, "Account");
        await rail.ExecuteAsync(updateIntent);
        Hash(await AccountBytesAsync(rail, metadataAddress)).Should().NotBe(Hash(current));
        var immutableUpdate = AccountArtifact(new("UpdateMetadata", mint.Key, owner.Key, Name: "Forbidden replacement",
            ExpectedMetadataHash: Hash(await AccountBytesAsync(rail, metadataAddress))));
        var rejected = await rail.Provider.QuoteNativeOperationAsync(updateIntent with
        {
            OperationKey = Guid.NewGuid().ToString("N"), Quote = null, ArtifactHash = CanonicalJson.Hash(immutableUpdate),
            Input = JsonSerializer.SerializeToElement(new SolanaNativeInput(immutableUpdate, owner.Key, owner.Key, "Account"))
        });
        rejected.IsError.Should().BeTrue("metadata made immutable cannot be changed by its former authority");
        var close = await rail.QuoteAsync(AccountArtifact(new("CloseTokenAccount", mint.Key, owner.Key, address, owner.Key)), "Account");
        close.Quote!.Disclosure!.RefundDestination.Should().Be(owner.Key);
        (await rail.ExecuteAsync(close)).Completed.Complete.Should().BeTrue();
        (await rail.RpcAsync("getAccountInfo", address, new { commitment = "finalized" })).GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static NativeArtifact Elf(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException("Build and configure the local native ELF fixtures first: " + variable);
        var bytes = File.ReadAllBytes(path);
        return new("solana-elf/1", JsonSerializer.SerializeToElement(new SolanaElfArtifact(Convert.ToBase64String(bytes), SourceDigest: Hash(bytes))), "agave-v3.1.8/platform-tools-v1.52");
    }
    private static byte[] Bytes(NativeArtifact artifact) => Convert.FromBase64String(artifact.Content.Deserialize<SolanaElfArtifact>()!.ElfBase64);
    private static NativeArtifact Instruction(string program, string hash) => new("solana-instruction/1",
        JsonSerializer.SerializeToElement(new SolanaInstructionArtifact(program, [], "", hash)), "local-native-fixture");
    private static NativeArtifact AccountArtifact(SolanaAccountArtifact artifact) => new("solana-account/1", JsonSerializer.SerializeToElement(artifact), "spl-token/metaplex");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static async Task<byte[]> AccountBytesAsync(LocalSolanaNativeRail rail, string address)
        => Convert.FromBase64String((await rail.RpcAsync("getAccountInfo", address, new { encoding = "base64", commitment = "finalized" }))
            .GetProperty("value").GetProperty("data")[0].GetString()!);
    private static async Task<string> ProgramHashAsync(LocalSolanaNativeRail rail, string program)
    {
        var bytes = await AccountBytesAsync(rail, program);
        var data = await AccountBytesAsync(rail, new PublicKey(bytes.AsSpan(4, 32).ToArray()).Key);
        return Hash(data.AsSpan(45).ToArray());
    }
}
