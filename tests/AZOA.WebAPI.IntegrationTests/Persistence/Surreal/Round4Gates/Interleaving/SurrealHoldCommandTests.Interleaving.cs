using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Idempotency;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

// Round-4 interleaving gate: partial extension of the Hold rig; see Round4Gates/Interleaving/AGENTS.md.
public sealed partial class SurrealHoldCommandTests
{
    [Theory]
    [InlineData("participant-grant")]
    [InlineData("tenant-api-key")]
    public async Task InterleavedExchangeSourceConsumptionAndRevocationCommitOneLiabilityOrNone(string revoked)
    {
        var (intent, authority) = await ExchangeAsync();
        var source = (await _store.AdmitExchangeSourceAsync(intent, authority))!;
        var revocation = revoked == "participant-grant"
            ? SurrealWriter.UpdateOnly<ConsentGrant>(authority.Grant.Id.ToString("N")).Where(row => row.RevokedAt == null)
                .Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build()
            : SurrealWriter.UpdateOnly<ApiKey>(intent.ApiKeyId.ToString("N")).Where(row => row.RevokedAt == null)
                .Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build();

        var consume = _store.CommitExchangeOpeningAsync(source.Id);
        var revoke = SurrealTransientConflict.RetryOnConflictAsync(async () =>
        {
            var response = await _executor.ExecuteAsync(revocation);
            response.EnsureAllOk();
            return response;
        }, CancellationToken.None);
        await Task.WhenAll(consume, revoke).WaitAsync(TimeSpan.FromSeconds(15));
        var consumeOutcome = await consume;

        var persisted = (await _store.GetExchangeSourceAsync(source.Id))!;
        var liability = await _store.GetHoldAsync(HoldCommandIdentity.Hold(intent.Opening!));
        var replay = await _store.CommitExchangeOpeningAsync(source.Id);
        if (consumeOutcome is { } receipt)
        {
            persisted.State.Should().Be("Committed");
            persisted.Version.Should().Be(1, "the source version is consumed exactly once");
            liability!.EconomicSourceId.Should().Be(source.Id);
            (await Counters()).Held.Should().Be(400);
            replay!.OutcomeJson.Should().Be(receipt.OutcomeJson, "a committed receipt survives the later revocation");
        }
        else
        {
            persisted.State.Should().Be("Admitted", "revocation won before the source was consumed");
            persisted.Version.Should().Be(source.Version);
            liability.Should().BeNull();
            (await Counters()).Held.Should().Be(0);
            replay.Should().BeNull("the loser re-reads the revoked authority and still refuses");
        }
        (await _executor.QueryAsync<HoldLedger>(SurrealQuery<HoldLedger>.From())).Should().HaveCount(consumeOutcome is null ? 0 : 1);
    }

    [Fact]
    public async Task InterleavedMandateFirstEgressAndRevocationNeverAuthorizeUnderARevokedMandate()
    {
        var (command, accepted, mandates, authority, wire) = await PreparedMandateReleaseAsync();
        var revocation = new HoldRecoveryRequest(Guid.NewGuid(), accepted.Version, "Withdraw further automatic custody authorization.");

        var authorize = _store.AuthorizeSendAsync(command);
        var revoke = mandates.RevokeAsync(accepted.Id, revocation, authority.Direct!.Credential);
        await Task.WhenAll(authorize, revoke).WaitAsync(TimeSpan.FromSeconds(15));
        var authorizeOutcome = await authorize;
        var revokeOutcome = await revoke;

        revokeOutcome!.Status.Should().Be("Revoked", "an owner can always withdraw further automatic authorization");
        var uses = await _executor.QueryAsync<HoldMandateUseReceipt>(SurrealQuery<HoldMandateUseReceipt>.From()
            .Where(row => row.OperationId == command.Id));
        uses.Should().OnlyContain(row => row.MandateId == accepted.Id && row.MandateVersion == accepted.Version
            && row.AuthorityJson == accepted.CurrentAuthorityJson, "every use is bound to the pre-revocation mandate");
        var current = (await _store.GetCommandAsync(command.Id))!;
        if (authorizeOutcome is { } authorized)
        {
            authorized.State.Should().NotBe(command.State);
            current.State.Should().Be(authorized.State);
            uses.Should().HaveCount(3, "first egress recorded its mandate use before the revocation committed");
            var proof = new HoldTransferEvidence(1, CanonicalJson.HashCanonical(authorized.PreparedJson!), wire.TransactionId,
                wire.ProtocolIdentity, ChainConfirmation.Confirmed, DateTimeOffset.UtcNow);
            (await _store.CompleteRailAsync(authorized, proof)).Should().NotBeNull("finalized accounting survives revocation");
            (await Counters()).Held.Should().Be(0);
        }
        else
        {
            current.State.Should().Be(command.State, "the refused egress left the prepared command untouched");
            uses.Should().HaveCount(2);
            (await _store.AuthorizeSendAsync(current)).Should().BeNull("the loser re-reads the revoked mandate and still refuses");
            (await Counters()).Held.Should().Be(400);
        }
    }

    /// <summary>Opens a due hold, accepts its release mandate and prepares the mandate-driven release up to first egress.</summary>
    private async Task<(HoldCommand Command, HoldReleaseMandate Accepted, SurrealHoldReleaseMandateStore Mandates,
        HoldCommandAuthority Authority, PreparedValueTransfer Wire)> PreparedMandateReleaseAsync()
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
        var command = (await _store.ClaimRailAsync(release, plan, new HoldCommandAuthority(1, ReleaseMandateId: accepted.Id)))!;
        command = (await _store.BeginPreparationAsync(command))!;
        var stage = JsonSerializer.Deserialize<ValueTransferIntent>(command.StageIntentJson!)!;
        var wire = new PreparedValueTransfer("mandate-tx", "signed-wire", "genesis", stage);
        command = (await _store.SavePreparedAsync(command, wire))!;
        return (command, accepted, mandates, authority, wire);
    }
}
