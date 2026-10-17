using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealHoldCommandTests
{
    [Fact]
    public async Task UnsentOutboundCancellationRestoresOriginalLiabilityAndPreservesReceipt()
    {
        var opening = Open();
        var authority = Authority();
        (await _store.CommitLocalAsync(opening, authority)).Should().NotBeNull();
        var recipient = new SigningWalletWitness(Guid.NewGuid(), _actor, "recipient", "Algorand");
        await Save(new Wallet { Id = recipient.Id.ToString("N"), UserId = "user:" + _actor.ToString("N"),
            Address = recipient.Address, ChainType = recipient.ChainType, WalletType = Wallet.WalletTypeKind.Platform });
        var release = opening with { RequestKey = "release", Kind = "Release", Definition = null };
        var now = DateTimeOffset.UtcNow;
        var quote = new ValueCustodyQuote(1, "", new("Algorand", ChainNetwork.Devnet, "42", "reserve", recipient.Address, "400", "reserve"),
            "genesis", "mechanics", "ALGO", 6, "1000", true, "1000", "100000", now, now.AddMinutes(5));
        quote = quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) };
        var plan = new HoldRailPlan(1, "Release", HoldCommandIdentity.Operation(release), 0, quote, null, recipient,
            SigningContext.PlatformFor("Algorand") with { Scope = AzoaScopes.TransferSign });
        var row = (await _store.ClaimRailAsync(release, plan, authority))!;
        row.Should().NotBeNull();
        var request = new HoldRecoveryRequest(Guid.NewGuid(), row.Version, "Release no longer requested.");
        var cancelled = await _store.CancelRailAsync(row.Id, request, authority.Direct!.Credential);
        cancelled!.State.Should().Be("Cancelled");
        var aggregate = (await _store.GetHoldAsync(row.HoldRecordId))!;
        aggregate.State.Should().Be("Held");
        aggregate.ActiveCommandId.Should().BeNull();
        (await Counters()).Held.Should().Be(400);
        (await Counters()).Available.Should().Be(600);
        (await _store.AuthorizeRailAsync(row, new HoldCommandAuthorization { Id = "stale", OperationId = row.Id,
            ActorUserId = row.ActorUserId, ExpectedVersion = row.Version, RequestHash = "stale", QuoteId = quote.QuoteId,
            MaximumNetworkFeeBaseUnits = "1000", AuthorityJson = row.AuthorityJson, CreatedAt = now })).Should().BeNull();
        await Execute(SurrealWriter.UpdateOnly<User>(_actor.ToString("N")).Set(value => value.IsActive, false).Build());
        (await _store.CancelRailAsync(row.Id, request, authority.Direct.Credential))!.Version.Should().Be(cancelled.Version);
        (await _store.CancelRailAsync(row.Id, request with { Reason = "different" }, authority.Direct.Credential)).Should().BeNull();
    }

    [Fact]
    public async Task FinalizedFundingAccountsOnceAfterAuthorityRevocation()
    {
        var wallet = new SigningWalletWitness(Guid.NewGuid(), _actor, "funder-address", "Algorand");
        await Save(new Wallet { Id = wallet.Id.ToString("N"), UserId = "user:" + _actor.ToString("N"),
            Address = wallet.Address, ChainType = wallet.ChainType, WalletType = Wallet.WalletTypeKind.Platform });
        var initial = Open();
        var intent = initial with { Definition = initial.Definition! with { FundingSource = "Funder", FunderUserId = _actor,
            ReservePartition = null, ReleaseNotBefore = null } };
        var direct = Authority().Direct!;
        var authority = new HoldCommandAuthority(1, Direct: direct,
            Sender: new(1, "DirectJwt", direct.Credential, wallet, direct.RequiredScopes, direct.Grants));
        var now = DateTimeOffset.UtcNow;
        var quote = new ValueCustodyQuote(1, "", new("Algorand", ChainNetwork.Devnet, "42", wallet.Address, "reserve", "400", "reserve"),
            "genesis", "mechanics", "ALGO", 6, "1000", true, "1000", "100000", now, now.AddMinutes(5));
        quote = quote with { QuoteId = ValueCustodyIdentity.QuoteId(quote) };
        var plan = new HoldRailPlan(1, "Funding", HoldCommandIdentity.Operation(intent), 0, quote, wallet, null,
            SigningContext.ForUser(_actor, wallet.Id) with { Scope = AzoaScopes.TransferSign, ChainType = "Algorand" });
        var row = (await _store.ClaimRailAsync(intent, plan, authority))!;
        row.Should().NotBeNull();
        (await _store.GetHoldAsync(row.HoldRecordId))!.State.Should().Be("Pending");
        var authorization = new HoldCommandAuthorization { Id = Guid.NewGuid().ToString("N"), OperationId = row.Id,
            ActorUserId = row.ActorUserId, TenantId = row.TenantId, ExpectedVersion = row.Version, RequestHash = "exact-request",
            QuoteId = quote.QuoteId, MaximumNetworkFeeBaseUnits = "1000", AuthorityJson = CanonicalJson.Canonicalize(JsonSerializer.SerializeToElement(authority)),
            CreatedAt = now };
        row = (await _store.AuthorizeRailAsync(row, authorization))!;
        row = (await _store.BeginPreparationAsync(row))!;
        var wire = new PreparedValueTransfer("provider-confirmed-transaction", "saved-provider-wire", "genesis",
            JsonSerializer.Deserialize<ValueTransferIntent>(row.StageIntentJson!)!);
        row = (await _store.SavePreparedAsync(row, wire))!;
        row = (await _store.AuthorizeSendAsync(row))!;
        row.State.Should().Be("Submitted");
        await Execute(SurrealWriter.UpdateOnly<User>(_actor.ToString("N")).Set(value => value.IsActive, false).Build());
        var evidence = new HoldTransferEvidence(1, CanonicalJson.HashCanonical(row.PreparedJson!), wire.TransactionId,
            wire.ProtocolIdentity, ChainConfirmation.Confirmed, DateTimeOffset.UtcNow);
        (await _store.CompleteRailAsync(row, evidence with { TransactionId = "other" })).Should().BeNull();
        var results = await Task.WhenAll(_store.CompleteRailAsync(row, evidence), _store.CompleteRailAsync(row, evidence));
        results.Should().OnlyContain(receipt => receipt != null);
        results[0]!.OutcomeJson.Should().Be(results[1]!.OutcomeJson);
        (await _store.GetHoldAsync(row.HoldRecordId))!.State.Should().Be("Held");
        (await Counters()).Held.Should().Be(0);
        (await Counters()).Available.Should().Be(1000);
    }
}
