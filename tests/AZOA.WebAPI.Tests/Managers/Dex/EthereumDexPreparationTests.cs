using System.Text.Json;
using AZOA.WebAPI.Core.Signing;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Dex;
using AZOA.WebAPI.Providers.Blockchain.Ethereum;
using FluentAssertions;

namespace AZOA.WebAPI.Tests.Managers.Dex;

public sealed partial class EthereumDexQuoteTests
{
    [Fact]
    public async Task AllowancePrerequisiteReservesNonceBeforeAnySigningAndReplaysSameOriginalPlan()
    {
        using var fixture = new Fixture { PreparationEnabled = true };
        var quote = (await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!;
        var intent = Intent(quote);
        var first = await fixture.Provider.PlanSwapStageAsync(intent, "ResetAllowance", 0);
        first.IsError.Should().BeFalse(first.Message);
        fixture.Preparations.Snapshot.Should().ContainSingle();
        fixture.Preparations.Snapshot[0].State.Should().Be("Reserved");
        fixture.Preparations.Snapshot[0].SignedTransactionBase64.Should().BeNull();
        fixture.Configuration["AZOA:Ethereum:Dex:Networks:Devnet:Executor"] = Address(20);
        var replay = await fixture.Provider.PlanSwapStageAsync(intent, "ResetAllowance", 0);
        replay.IsError.Should().BeFalse(replay.Message);
        replay.Result.Should().BeEquivalentTo(first.Result);
        fixture.Preparations.Snapshot.Should().ContainSingle();
        fixture.Methods.Should().NotContain(method => method.Contains("send", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SwapReservationWaitsForExactFinalizedAllowanceAndThenPinsPermitAndRealOwnerWire()
    {
        using var fixture = new Fixture { PreparationEnabled = true };
        var quote = (await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!;
        var intent = Intent(quote);
        (await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0)).IsError.Should().BeTrue();
        fixture.Preparations.Snapshot.Should().BeEmpty();
        fixture.CurrentAllowance = 1010;
        var planned = await fixture.Provider.PlanSwapStageAsync(intent, "Swap", 0);
        planned.IsError.Should().BeFalse(planned.Message);
        var plan = planned.Result!;
        var unsigned = Convert.FromBase64String(JsonSerializer.Deserialize<string>(plan.UnsignedPayloadJson)!);
        using var key = new SigningKeyMaterial(fixture.OwnerKey.GetPrivateKeyAsBytes());
        var signed = new EthereumTransactionSigner().Sign(unsigned, key);
        signed.IsError.Should().BeFalse(signed.Message);
        var validated = fixture.Provider.ValidateSignedSwapStage(intent, plan, JsonSerializer.Serialize(Convert.ToBase64String(signed.Result!)));
        validated.IsError.Should().BeFalse(validated.Message);
        validated.Result!.TransactionIds.Should().ContainSingle();
        var tampered = plan with { ReservedNetworkFee = "1" };
        fixture.Provider.ValidateSignedSwapStage(intent, tampered, validated.Result.SignedPayloadJson).IsError.Should().BeTrue();
        fixture.Preparations.Snapshot[0].SignedTransactionBase64.Should().BeNull();
        fixture.Methods.Should().NotContain(method => method.Contains("send", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CanceledSignedReservationCannotBecomeSuccessfulDexPlanReplay()
    {
        using var fixture = new Fixture { PreparationEnabled = true };
        var quote = (await fixture.Provider.QuoteSwapAsync(fixture.Terms)).Result!;
        var intent = Intent(quote);
        (await fixture.Provider.PlanSwapStageAsync(intent, "ResetAllowance", 0)).IsError.Should().BeFalse();
        var row = fixture.Preparations.Snapshot.Single();
        row.State = "Canceling"; row.SignedTransactionBase64 = "AQID";
        await fixture.Preparations.UpdateAsync(row, row.Version);
        (await fixture.Provider.PlanSwapStageAsync(intent, "ResetAllowance", 0)).IsError.Should().BeTrue();
        fixture.Methods.Should().NotContain(method => method.Contains("send", StringComparison.OrdinalIgnoreCase));
    }

    private static SwapIntent Intent(DexQuoteTerms quote)
        => new(1, new string('e', 64), Guid.NewGuid().ToString("N"), CanonicalJson.Hash(quote), quote, new string('f', 64));
}
