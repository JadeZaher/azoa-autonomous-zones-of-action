using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealHoldCommandTests
{
    [Fact]
    public async Task MandateRequiresPersistedDisclosureAndCannotBeResurrectedByReauthorization()
    {
        var open = Open(); var authority = Authority();
        var receipt = (await _store.CommitLocalAsync(open, authority))!;
        var view = JsonSerializer.Deserialize<HoldCommandView>(receipt.OutcomeJson)!;
        var hold = (await _store.GetHoldAsync(view.HoldRecordId))!;
        var recipient = new SigningWalletWitness(Guid.NewGuid(), _actor, "recipient", "Algorand");
        await Save(new Wallet { Id = recipient.Id.ToString("N"), UserId = "user:" + _actor.ToString("N"),
            Address = recipient.Address, ChainType = recipient.ChainType, WalletType = Wallet.WalletTypeKind.Platform });
        var now = DateTimeOffset.UtcNow;
        var costs = new ValueCustodyCostBreakdown("reserve", "reserve", "1000", "0", "0");
        var quote = new ValueCustodyQuote(1, "", new("Algorand", open.Asset.Network, "42", "reserve", "recipient", "400", "reserve"),
            "genesis", "mechanics", "ALGO", 6, "1000", true, "0", "0", now, now.AddMinutes(5), costs);
        quote = quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) };
        var terms = new HoldReleaseMandateTerms(1, hold.Id, hold.DefinitionHash!, open.Asset, "400", recipient,
            hold.ReleaseNotBefore!.Value, hold.ReleaseNotBefore.Value.AddDays(1), "ALGO", 6, costs);
        var request = new HoldReleaseMandateRequest(Guid.NewGuid(), hold.Version, terms, quote);
        var mandates = new SurrealHoldReleaseMandateStore(_executor);
        (await mandates.AdmitAsync(new("missing", "caller-asserted-quote"), authority.Direct!)).Should().BeNull();
        var offer = (await mandates.OfferAsync(request, authority.Direct!))!;
        offer.Should().NotBeNull();
        (await _store.GetHoldAsync(hold.Id))!.ActiveReleaseMandateId.Should().BeNull();
        (await mandates.AdmitAsync(new(offer.Id, "changed"), authority.Direct!)).Should().BeNull();
        var accepted = (await mandates.AdmitAsync(new(offer.Id, offer.RequestHash), authority.Direct!))!;
        accepted.Status.Should().Be("Active");
        (await _store.GetHoldAsync(hold.Id))!.ActiveReleaseMandateId.Should().Be(accepted.Id);
        (await mandates.OfferAsync(request with { Terms = terms with { QuantityBaseUnits = "399" } }, authority.Direct!)).Should().BeNull();
        var revoke = new HoldRecoveryRequest(Guid.NewGuid(), accepted.Version, "Do not automatically release this hold.");
        var revoked = (await mandates.RevokeAsync(accepted.Id, revoke, authority.Direct!.Credential))!;
        revoked.Status.Should().Be("Revoked");
        (await _store.GetHoldAsync(hold.Id))!.ActiveReleaseMandateId.Should().BeNull();
        (await mandates.RevokeAsync(accepted.Id, revoke, authority.Direct.Credential))!.Version.Should().Be(revoked.Version);
        (await mandates.ReauthorizeAsync(accepted.Id, new(Guid.NewGuid(), revoked.Version, "Retry old mandate"), authority.Direct)).Should().BeNull();
        (await mandates.AdmitAsync(new(offer.Id, offer.RequestHash), authority.Direct))!.Status.Should().Be("Revoked");
        (await Counters()).Held.Should().Be(400);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MandateRevocationBlocksNewEgressButCannotEraseFinalizedAccounting(bool submittedBeforeRevoke)
    {
        var open = Open();
        open = open with { Definition = open.Definition! with { ReleaseNotBefore = DateTimeOffset.UtcNow.AddMinutes(-1) } };
        var authority = Authority();
        (await _store.CommitLocalAsync(open, authority)).Should().NotBeNull();
        var hold = (await _store.GetHoldAsync(HoldCommandIdentity.Hold(open)))!;
        var recipient = new SigningWalletWitness(Guid.NewGuid(), _actor, "recipient", "Algorand");
        await Save(new Wallet { Id = recipient.Id.ToString("N"), UserId = "user:" + _actor.ToString("N"),
            Address = recipient.Address, ChainType = recipient.ChainType, WalletType = Wallet.WalletTypeKind.Platform });
        var now = DateTimeOffset.UtcNow;
        var costs = new ValueCustodyCostBreakdown("reserve", "reserve", "1000", "0", "0");
        var quote = new ValueCustodyQuote(1, "", new("Algorand", open.Asset.Network, "42", "reserve", "recipient", "400", "reserve"),
            "genesis", "mechanics", "ALGO", 6, "1000", true, "0", "0", now, now.AddMinutes(5), costs);
        quote = quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) };
        var terms = new HoldReleaseMandateTerms(1, hold.Id, hold.DefinitionHash!, open.Asset, "400", recipient,
            hold.ReleaseNotBefore!.Value, now.AddDays(1), "ALGO", 6, costs);
        var mandates = new SurrealHoldReleaseMandateStore(_executor);
        var offer = (await mandates.OfferAsync(new(Guid.NewGuid(), hold.Version, terms, quote), authority.Direct!))!;
        var accepted = (await mandates.AdmitAsync(new(offer.Id, offer.RequestHash), authority.Direct!))!;
        var release = open with { RequestKey = "mandate:" + accepted.Id, Kind = "Release", Definition = null };
        var plan = new HoldRailPlan(1, "Release", HoldCommandIdentity.Operation(release), 0, quote, null, recipient,
            SigningContext.PlatformFor("Algorand") with { Scope = AzoaScopes.TransferSign });
        var mandateAuthority = new HoldCommandAuthority(1, ReleaseMandateId: accepted.Id);
        (await _store.ClaimRailAsync(release with { RequestKey = "different-command" }, plan, mandateAuthority)).Should().BeNull();
        (await _store.ClaimRailAsync(release, plan, mandateAuthority with { Direct = authority.Direct })).Should().BeNull();
        var overBudgetQuote = quote with { QuoteId = "", TransferNetworkFeeBaseUnits = "1001",
            TransferCosts = costs with { NetworkFeeBaseUnits = "1001" } };
        overBudgetQuote = overBudgetQuote with { QuoteId = ValueCustodyIdentity.QuoteId(overBudgetQuote) };
        (await _store.ClaimRailAsync(release, plan with { Quote = overBudgetQuote }, mandateAuthority)).Should().BeNull();
        (await _store.GetCommandAsync(plan.CommandId)).Should().BeNull();
        var command = (await _store.ClaimRailAsync(release, plan, mandateAuthority))!;
        command.State.Should().Be("Admitted"); command.StageIntentJson.Should().NotBeNull();
        (await mandates.GetAsync(accepted.Id))!.OperationId.Should().Be(command.Id);
        command = (await _store.BeginPreparationAsync(command))!;
        var stage = JsonSerializer.Deserialize<ValueTransferIntent>(command.StageIntentJson!)!;
        var wire = new PreparedValueTransfer("mandate-tx", "signed-wire", "genesis", stage);
        command = (await _store.SavePreparedAsync(command, wire))!;
        if (submittedBeforeRevoke) command = (await _store.AuthorizeSendAsync(command))!;
        var revoked = (await mandates.RevokeAsync(accepted.Id,
            new(Guid.NewGuid(), accepted.Version, "Withdraw further automatic custody authorization."), authority.Direct!.Credential))!;
        revoked.Status.Should().Be("Revoked");
        var uses = await _executor.QueryAsync<HoldMandateUseReceipt>(SurrealQuery<HoldMandateUseReceipt>.From()
            .Where(row => row.OperationId == command.Id));
        uses.Should().HaveCount(submittedBeforeRevoke ? 3 : 2);
        uses.Should().OnlyContain(row => row.MandateId == accepted.Id && row.MandateVersion == accepted.Version
            && row.AuthorityJson == accepted.CurrentAuthorityJson && row.TermsHash == accepted.TermsHash);
        if (!submittedBeforeRevoke)
        {
            (await _store.AuthorizeSendAsync(command)).Should().BeNull();
            (await Counters()).Held.Should().Be(400);
        }
        else
        {
            var proof = new HoldTransferEvidence(1, CanonicalJson.HashCanonical(command.PreparedJson!), wire.TransactionId,
                wire.ProtocolIdentity, ChainConfirmation.Confirmed, now);
            (await _store.CompleteRailAsync(command, proof)).Should().NotBeNull();
            (await mandates.GetAsync(accepted.Id))!.Status.Should().Be("Completed");
            (await _store.GetHoldAsync(hold.Id))!.State.Should().Be("Released");
            (await Counters()).Held.Should().Be(0);
        }
    }
}
