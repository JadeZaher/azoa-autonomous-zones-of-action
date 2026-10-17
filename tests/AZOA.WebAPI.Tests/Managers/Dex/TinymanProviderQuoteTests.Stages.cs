using System.Text.Json;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Providers.Blockchain.Algorand;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed partial class TinymanProviderQuoteTests
{
    [Fact]
    public async Task FinalSwapEvidenceCompletesWithoutRequotingOrSigning()
    {
        using var fixture = new Fixture();
        var (intent, prepared) = await SignedSwapAsync(fixture);
        var wire = JsonSerializer.Deserialize<TinymanWireParameters>(prepared.Plan.RecoveryMetadata)!;
        using var block = JsonDocument.Parse(TinymanSwapReceiptTests.Block(wire).ToJsonString());
        TinymanSwapReceipt.TryRead(wire, prepared.TransactionIds, block.RootElement, out var observation).Should().BeTrue();
        fixture.Rpc.Change = "code";
        var next = await fixture.Provider.GetNextSwapStageAsync(intent, [new(prepared, observation!)]);
        next.IsError.Should().BeFalse(next.Message);
        next.Result!.Complete.Should().BeTrue();
        next.Result.Stage.Should().BeNull();
        fixture.Rpc.Posts.Should().Be(0);
        fixture.Custody.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task OptInConfirmationMovesToSwapWithRentStillUnknownAndRetainedEffectVisible()
    {
        using var fixture = new Fixture();
        var (intent, prepared) = await SignedSwapAsync(fixture, true);
        var wire = JsonSerializer.Deserialize<TinymanWireParameters>(prepared.Plan.RecoveryMetadata)!;
        using var block = JsonDocument.Parse(TinymanSwapReceiptTests.Block(wire).ToJsonString());
        TinymanSwapReceipt.TryRead(wire, prepared.TransactionIds, block.RootElement, out var observation).Should().BeTrue();
        observation!.LockedBalance.Should().BeNull();
        var next = await fixture.Provider.GetNextSwapStageAsync(intent, [new(prepared, observation)]);
        next.Result!.Complete.Should().BeFalse();
        next.Result.Stage.Should().Be("Swap");
        next.Result.RetainedEffects.Should().ContainSingle(effect => effect.Kind == "OptIn" && effect.RetainedOnSwapFailure);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("NeedsOperator")]
    public async Task UnresolvedStageCannotAuthorizeAnotherStage(string verdict)
    {
        using var fixture = new Fixture();
        var (intent, prepared) = await SignedSwapAsync(fixture);
        var observation = new SwapStageObservation(verdict, prepared.TransactionIds, intent.Terms.Network.ProtocolIdentity);
        (await fixture.Provider.GetNextSwapStageAsync(intent, [new(prepared, observation)])).IsError.Should().BeTrue();
    }

    [Fact]
    public async Task DuplicateTerminalStageAndIncompleteEconomicSettlementAreRefused()
    {
        using var fixture = new Fixture();
        var (intent, prepared) = await SignedSwapAsync(fixture);
        var wire = JsonSerializer.Deserialize<TinymanWireParameters>(prepared.Plan.RecoveryMetadata)!;
        using var block = JsonDocument.Parse(TinymanSwapReceiptTests.Block(wire).ToJsonString());
        TinymanSwapReceipt.TryRead(wire, prepared.TransactionIds, block.RootElement, out var observation).Should().BeTrue();
        (await fixture.Provider.GetNextSwapStageAsync(intent, [new(prepared, observation!), new(prepared, observation!)])).IsError.Should().BeTrue();
        (await fixture.Provider.GetNextSwapStageAsync(intent, [new(prepared, observation! with { ActualOutput = "0" })])).IsError.Should().BeTrue();
        fixture.Rpc.Posts.Should().Be(0);
    }
}
