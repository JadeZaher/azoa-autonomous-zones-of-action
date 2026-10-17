using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers.Deployments;
using AZOA.WebAPI.Models.Deployments;
using AZOA.WebAPI.Providers.Blockchain.Solana;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Solnet.Programs;
using Solnet.Wallet;
using static AZOA.WebAPI.Tests.TestSupport.SolanaProtocolCostFixture;

namespace AZOA.WebAPI.Tests.Deployments;

public sealed partial class SolanaNativeOperationTests
{
    [Fact]
    public async Task Metadata_quote_reserves_permanent_levy_separately_and_replays_one_guarded_preparation()
    {
        using var fixture = new SolanaDurableFixture(new MemoryPreparations());
        var intent = MetadataIntent(fixture);
        var result = await fixture.Provider.QuoteNativeOperationAsync(intent);
        result.IsError.Should().BeFalse(result.Message);
        result.Result!.LockedRent.Should().Be("5115600"); result.Result.Value.Should().Be("10000000");
        result.Result.NetworkFee.Should().Be("5000");
        using var plan = JsonDocument.Parse(result.Result.PlanJson);
        var instructions = plan.RootElement.GetProperty("Stages")[0].GetProperty("Instructions");
        instructions.GetArrayLength().Should().Be(2);
        instructions[0].GetProperty("Program").GetString().Should().Be(fixture.Configuration[MetadataPrefix + "CostGuardProgram"]);
        Convert.FromBase64String(instructions[0].GetProperty("Data").GetString()!)[0].Should().Be(3);
        instructions[1].GetProperty("Program").GetString().Should().Be(SolanaNftMetadata.Program.Key);
        fixture.Consents.Should().BeEmpty();
        intent = BindMetadataQuote(intent, result.Result);
        var next = (await fixture.Provider.GetNextStageAsync(intent, [])).Result!;
        next.Reservation!.Value.Should().Be("10000000");
        var first = await fixture.Provider.PrepareNativeStageAsync(intent, "CreateMetadata", 0);
        first.IsError.Should().BeFalse(first.Message);
        first.Result!.ReservedValue.Should().Be("10000000"); first.Result.ReservedRent.Should().Be("5115600");
        (await fixture.Provider.PrepareNativeStageAsync(intent, "CreateMetadata", 0)).Result.Should().Be(first.Result);
        fixture.Consents.Should().ContainSingle(); fixture.Sends.Should().Be(0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Provider.SubmitNativeStageAsync(intent,
            first.Result with { ReservedValue = "0" }));
        fixture.Sends.Should().Be(0);
    }

    [Theory]
    [InlineData("rent")]
    [InlineData("code")]
    [InlineData("guard")]
    [InlineData("policy")]
    [InlineData("budget")]
    public async Task Changed_metadata_evidence_refuses_new_preparation_before_custody(string changed)
    {
        var store = new MemoryPreparations();
        using var fixture = new SolanaDurableFixture(store);
        var intent = MetadataIntent(fixture);
        var quote = (await fixture.Provider.QuoteNativeOperationAsync(intent)).Result!;
        intent = BindMetadataQuote(intent, quote);
        if (changed == "rent") fixture.Accounts[SysVars.RentKey.Key] = BinaryAccount("Sysvar1111111111111111111111111111111111111", Rent(3));
        if (changed == "code") PutProgram(fixture, SolanaNftMetadata.Program, [9, 9, 9], false);
        if (changed == "guard") PutProgram(fixture, new PublicKey(fixture.Configuration[MetadataPrefix + "CostGuardProgram"]!), [1, 2, 3], true);
        if (changed == "policy") fixture.Configuration["AZOA:Solana:NativeMetadata:ReviewedExecutables:0:SourceRevision"] = "unreviewed";
        if (changed == "budget") intent = intent with { Budget = intent.Budget with { MaximumValue = "9999999" } };
        var result = await fixture.Provider.PrepareNativeStageAsync(intent, "CreateMetadata", 0);
        result.IsError.Should().BeTrue(); store.Rows.Should().BeEmpty();
        fixture.Consents.Should().BeEmpty(); fixture.Sends.Should().Be(0);
    }

    private const string MetadataPrefix = "AZOA:Solana:NativeMetadata:Networks:Devnet:";
    private static NativeOperationIntent MetadataIntent(SolanaDurableFixture fixture)
    {
        var mint = new Account().PublicKey;
        ConfigureMetadata(fixture, MetadataPrefix, "AZOA:Solana:NativeMetadata:ReviewedExecutables");
        fixture.Accounts[mint.Key] = new { owner = TokenProgram.ProgramIdKey.Key, executable = false,
            data = new { parsed = new { type = "mint", info = new { mintAuthority = fixture.Actor.PublicKey.Key } } } };
        var artifact = new NativeArtifact("solana-account/1", JsonSerializer.SerializeToElement(new SolanaAccountArtifact(
            "CreateMetadata", mint.Key, fixture.Actor.PublicKey.Key, Name: "Example", Uri: "https://example.test/metadata")), "metadata");
        return new(1, Guid.NewGuid().ToString("N"), "Account", "Solana", "Devnet", "", "artifact",
            JsonSerializer.SerializeToElement(new SolanaNativeInput(artifact, fixture.Actor.PublicKey.Key, fixture.Actor.PublicKey.Key, "Account")),
            [new(fixture.Actor.PublicKey.Key, new SigningContext(fixture.ActorId, fixture.ActorWalletId, false, Scope: AzoaScopes.SolanaAccountSign))],
            new("5000", "5115600", "10000000", ""));
    }

    private static NativeOperationIntent BindMetadataQuote(NativeOperationIntent intent, NativeOperationQuote quote)
    {
        intent = intent with { ProtocolIdentity = quote.ProtocolIdentity };
        return intent with { Quote = quote with { QuoteId = "metadata", IntentHash = NativeOperationAuthorization.IntentHash(intent) },
            Budget = intent.Budget with { QuoteId = "metadata" } };
    }

}
