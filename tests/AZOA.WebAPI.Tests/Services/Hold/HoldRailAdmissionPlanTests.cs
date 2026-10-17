using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Services.Hold;
using FluentAssertions;
using System.Text.Json;

namespace AZOA.WebAPI.Tests.Services.Hold;

public sealed class HoldRailAdmissionPlanTests
{
    [Theory]
    [InlineData("confirmed", true)]
    [InlineData("pending", false)]
    [InlineData("wrong-wire", false)]
    [InlineData("wrong-protocol", false)]
    [InlineData("wrong-amount", false)]
    public void FundingBecomesHeldOnlyFromExactFinalizedOriginalWire(string variation, bool succeeds)
    {
        var (intent, plan, reserve) = Funding();
        var admission = HoldRailAdmissionPlan.Create(intent, plan, new(1), null, reserve, DateTimeOffset.UtcNow).Result!;
        var command = admission.Command;
        var transfer = new ValueTransferIntent(intent.Asset.ChainType, intent.Asset.Network, intent.Asset.AssetId,
            plan.Quote.Request.Sender, plan.Quote.Request.Recipient, plan.Quote.Request.QuantityBaseUnits,
            plan.SenderSigningContext, $"hold:{command.Id}:Funding:0", new(1, command.Id, "Funding", 0,
                plan.Quote, plan.Quote.Request.Sender, "1000"));
        var wire = new PreparedValueTransfer("final-transaction", "persisted-provider-wire", intent.Asset.ProtocolIdentity, transfer);
        command.State = "Submitted";
        command.StageIntentJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(transfer));
        command.PreparedJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(wire));
        var evidence = new HoldTransferEvidence(1, CanonicalJson.HashCanonical(command.PreparedJson), wire.TransactionId,
            wire.ProtocolIdentity, ChainConfirmation.Confirmed, DateTimeOffset.UtcNow);
        if (variation == "pending") evidence = evidence with { Confirmation = ChainConfirmation.Pending };
        if (variation == "wrong-wire") evidence = evidence with { PreparedHash = "other" };
        if (variation == "wrong-protocol") evidence = evidence with { ProtocolIdentity = "other" };
        if (variation == "wrong-amount") admission.Aggregate.Amount = "399";
        var result = HoldRailCompletionPlan.Create(command, evidence, admission.Aggregate, reserve);
        result.IsError.Should().Be(!succeeds);
        if (!succeeds) return;
        result.Result!.Aggregate.State.Should().Be("Held");
        result.Result.Aggregate.ActiveCommandId.Should().BeNull();
        result.Result.Reserve.Should().BeNull("actual funder deposits do not draw a reserve partition");
        result.Result.Receipt.EvidenceJson.Should().NotBeNull();
        admission.Aggregate.State.Should().Be("Pending");
    }

    [Fact]
    public void UnpaidFundingReservesPendingLiabilityWithoutInventingFinality()
    {
        var (intent, plan, reserve) = Funding();
        var result = HoldRailAdmissionPlan.Create(intent, plan, new(1), null, reserve, DateTimeOffset.UtcNow);
        result.IsError.Should().BeFalse();
        result.Result!.Aggregate.State.Should().Be("Pending");
        result.Result.Aggregate.ActiveCommandId.Should().Be(plan.CommandId);
        result.Result.Command.State.Should().Be("AwaitingConsent");
        result.Result.Command.PreparedJson.Should().BeNull();
        result.Result.Command.AcceptedAt.Should().BeNull();
    }

    [Theory]
    [InlineData("funder")]
    [InlineData("wallet-owner")]
    [InlineData("custody-tenant")]
    [InlineData("recipient")]
    [InlineData("protocol")]
    [InlineData("quantity")]
    public void CallerCannotSubstituteFundingIdentityOrQuote(string change)
    {
        var (intent, plan, reserve) = Funding();
        if (change == "funder") intent = intent with { Definition = intent.Definition! with { FunderUserId = Guid.NewGuid() } };
        if (change == "wallet-owner") plan = plan with { SenderWallet = plan.SenderWallet! with { OwnerUserId = Guid.NewGuid() } };
        if (change == "custody-tenant") intent = intent with { CustodyTenantId = Guid.NewGuid() };
        if (change == "recipient") plan = plan with { Quote = plan.Quote with { Request = plan.Quote.Request with { Recipient = "attacker" } } };
        if (change == "protocol") plan = plan with { Quote = plan.Quote with { ProtocolIdentity = "other-genesis" } };
        if (change == "quantity") plan = plan with { Quote = plan.Quote with { Request = plan.Quote.Request with { QuantityBaseUnits = "399" } } };
        plan = plan with { Quote = plan.Quote with { QuoteId = ValueCustodyIdentity.QuoteId(plan.Quote) } };
        HoldRailAdmissionPlan.Create(intent, plan, new(1), null, reserve, DateTimeOffset.UtcNow).IsError.Should().BeTrue();
    }

    private static (HoldCommandIntent, HoldRailPlan, PlatformReserveLedger) Funding()
    {
        var actor = Guid.NewGuid();
        var wallet = new SigningWalletWitness(Guid.NewGuid(), actor, "sender", "Algorand");
        var asset = new HoldAssetIdentity("Algorand", ChainNetwork.Devnet, "genesis", "42", "holding");
        var definition = new HoldDefinition(asset, "400", "Funder", Guid.NewGuid(), actor, null, null, false, null);
        var intent = new HoldCommandIntent(1, "fund", "Open", "liability", actor, actor, null, null, asset, definition);
        var now = DateTimeOffset.UtcNow;
        var quote = new ValueCustodyQuote(1, "", new("Algorand", ChainNetwork.Devnet, "42", "sender", "holding", "400", "holding"),
            "genesis", "mechanics", "ALGO", 6, "1000", true, "1000", "100000", now, now.AddMinutes(5));
        quote = quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) };
        var plan = new HoldRailPlan(1, "Funding", HoldCommandIdentity.Operation(intent), 0, quote, wallet, null,
            SigningContext.ForUser(actor, wallet.Id) with { Scope = AzoaScopes.TransferSign, ChainType = "Algorand" });
        var reserve = new PlatformReserveLedger { Id = "reserve", Chain = "Algorand", Network = "Devnet",
            ProtocolIdentity = "genesis", AssetId = "42", ReserveAddress = "holding" };
        return (intent, plan, reserve);
    }
}
