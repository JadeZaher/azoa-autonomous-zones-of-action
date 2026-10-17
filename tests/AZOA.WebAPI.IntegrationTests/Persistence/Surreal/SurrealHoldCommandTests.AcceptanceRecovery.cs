using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealHoldCommandTests
{
    [Fact]
    public async Task ProvenNoEffectAcceptanceRotatesQuoteWithoutReusingParticipantConsentOrDiscardingEvidence()
    {
        var opening = Open(); var authority = Authority();
        (await _store.CommitLocalAsync(opening, authority)).Should().NotBeNull();
        var recipient = new SigningWalletWitness(Guid.NewGuid(), _actor, "recipient", "Algorand");
        await Save(new Wallet { Id = recipient.Id.ToString("N"), UserId = "user:" + _actor.ToString("N"),
            Address = recipient.Address, ChainType = recipient.ChainType, WalletType = Wallet.WalletTypeKind.Platform });
        var release = opening with { RequestKey = "release", Kind = "Release", Definition = null };
        var now = DateTimeOffset.UtcNow;
        var quote = new ValueCustodyQuote(1, "", new("Algorand", ChainNetwork.Devnet, "42", "reserve", recipient.Address, "400", "reserve"),
            "genesis", "mechanics", "ALGO", 6, "1000", false, "1000", "100000", now, now.AddMinutes(5));
        quote = quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) };
        var plan = new HoldRailPlan(1, "Release", HoldCommandIdentity.Operation(release), 0, quote, null, recipient,
            SigningContext.PlatformFor("Algorand") with { Scope = AzoaScopes.TransferSign });
        var row = (await _store.ClaimRailAsync(release, plan, authority))!;
        row = (await _store.AuthorizeRailAsync(row, new HoldCommandAuthorization { Id = Guid.NewGuid().ToString("N"),
            OperationId = row.Id, ActorUserId = row.ActorUserId, ExpectedVersion = row.Version, RequestHash = "first-cost-consent",
            QuoteId = quote.QuoteId, MaximumNetworkFeeBaseUnits = "1000", AuthorityJson = row.AuthorityJson, CreatedAt = now }))!;
        row.Should().NotBeNull();
        row.StageIntentJson.Should().NotBeNull(); row.PreparedJson.Should().BeNull();
        var direct = authority.Direct!;
        var acceptanceAuthority = new SigningAdmissionAuthority(1, "DirectJwt", direct.Credential, recipient, direct.RequiredScopes, direct.Grants);
        var intent = new ValueAcceptanceIntent("Algorand", ChainNetwork.Devnet, "42", recipient.Address,
            $"hold:{row.Id}:Acceptance:0", SigningContext.ForUser(_actor, recipient.Id) with { ChainType = "Algorand", Scope = AzoaScopes.TransferSign },
            new(1, row.Id, "Acceptance", 0, quote, recipient.Address, "1000"));
        row = (await _store.AdmitAcceptanceAsync(row, intent, acceptanceAuthority, new(Guid.NewGuid(), row.Version, quote.QuoteId, "1000")))!;
        row = (await _store.AdvanceAcceptanceAsync(row, "Preparing"))!;
        var wire = new PreparedValueAcceptance("expired-acceptance", "saved-wire", "genesis", intent);
        row = (await _store.AdvanceAcceptanceAsync(row, "Prepared", wire))!;
        row = (await _store.AdvanceAcceptanceAsync(row, "Submitted"))!;
        var proof = new HoldTransferEvidence(1, CanonicalJson.HashCanonical(row.AcceptancePreparedJson!), wire.TransactionId,
            wire.ProtocolIdentity, ChainConfirmation.FailedOnChain, now);
        (await _store.AdvanceAcceptanceAsync(row, "FailedNoEffect", evidence: proof with { TransactionId = "wrong" })).Should().BeNull();
        row = (await _store.AdvanceAcceptanceAsync(row, "FailedNoEffect", evidence: proof))!;
        var nextQuote = quote with { QuoteId = "", QuotedAt = now.AddSeconds(1), ExpiresAt = now.AddMinutes(6) };
        nextQuote = nextQuote with { QuoteId = ValueCustodyIdentity.QuoteId(nextQuote) };
        var request = new HoldRecoveryRequest(Guid.NewGuid(), row.Version, "Refresh after proven expired acceptance.");
        var next = (await _store.ReplaceRailAsync(row, plan with { Generation = 1, Quote = nextQuote }, authority, request))!;
        next.Should().NotBeNull(); next.State.Should().Be("AwaitingConsent"); next.Generation.Should().Be(1);
        next.AcceptanceState.Should().BeNull(); next.CurrentAcceptanceAuthorityJson.Should().BeNull(); next.AcceptedAt.Should().BeNull();
        var archived = JsonSerializer.Deserialize<List<JsonElement>>(next.TransactionHistoryJson)!;
        archived.Should().ContainSingle(); archived[0].GetProperty("AcceptancePreparedJson").GetString().Should().Be(row.AcceptancePreparedJson);
        archived[0].GetProperty("AcceptanceEvidenceJson").GetString().Should().Be(row.AcceptanceEvidenceJson);
        archived[0].GetProperty("StageIntentJson").GetString().Should().Be(row.StageIntentJson);
        next.StageIntentJson.Should().BeNull(); next.CurrentAuthorizationId.Should().BeNull();
        (await _store.AdvanceAcceptanceAsync(row, "Preparing")).Should().BeNull();
        (await _store.ReplaceRailAsync(row, plan with { Generation = 1, Quote = nextQuote }, authority, request))!.Version.Should().Be(next.Version);
        (await Counters()).Held.Should().Be(400);
    }
}
