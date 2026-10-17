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

/// <summary>Replacement creation costs commit append-only under DB-time expiry and both original and current authority.</summary>
public sealed partial class SurrealNftLifecycleStoreTests
{
    [Fact]
    public async Task AdmittedReceiptExpiryDoesNotBlockContinuingTheAdmittedGeneration()
    {
        var (row, quote) = await CostCandidate(lifetime: TimeSpan.FromSeconds(3));
        await new SurrealNftProtocolCostQuoteStore(_executor).ClaimAsync(quote);
        var admitted = (await _store.ClaimAsync(row))!;
        await Task.Delay(TimeSpan.FromSeconds(4));
        admitted.Detail = "Preparing after the original offer expired";
        (await _store.UpdateAsync(admitted, admitted.Version, requireActionAuthority: true)).Should().NotBeNull(
            "receipt expiry bounds admission, not continuation of the admitted generation");
    }

    [Fact]
    public async Task ReplacementAcceptanceCommitsOnceAndPreservesTheOriginalCommandAndHistory()
    {
        var (row, original) = await ExpiredGeneration();
        var (accepted, consent) = await ReplacementAcceptance(row, Current(original));
        var saved = await _store.UpdateAsync(accepted, row.Version, requireActionAuthority: true);
        saved.Should().NotBeNull();
        saved!.RequestJson.Should().Be(row.RequestJson);
        saved.TransactionHistoryJson.Should().Be(row.TransactionHistoryJson);
        NftReplacementCostContract.Current(saved)!.Quote.Should().Be(consent.Quote);
        var (second, _) = await ReplacementAcceptance(saved, Current(original));
        (await _store.UpdateAsync(second, saved.Version, requireActionAuthority: true)).Should().BeNull(
            "one generation keeps exactly one accepted replacement offer");
        NftReplacementCostContract.Read((await _store.GetAsync(row.Id))!).Should().ContainSingle();
    }

    [Fact]
    public async Task ExpiredReplacementOfferCannotCommitAtDatabaseTime()
    {
        var (row, original) = await ExpiredGeneration();
        var (accepted, _) = await ReplacementAcceptance(row, Current(original), DateTimeOffset.UtcNow.AddMinutes(-10), TimeSpan.FromMinutes(5));
        (await _store.UpdateAsync(accepted, row.Version, requireActionAuthority: true)).Should().BeNull();
        (await _store.GetAsync(row.Id))!.ReplacementCostConsentsJson.Should().Be("[]");
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("original-revoked", false)]
    [InlineData("original-revoked-then-renewed", true)]
    [InlineData("current-expired", false)]
    [InlineData("payer-wallet-moved", false)]
    public async Task ReplacementAcceptanceRequiresTheEffectiveRequesterAndALiveAcceptingCredential(string change, bool expected)
    {
        var (row, original) = await ExpiredGeneration();
        var current = Current(original);
        if (change.StartsWith("original-revoked", StringComparison.Ordinal))
        {
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-10);
            (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(original.Credential.ActorUserId.ToString("N"))
                .Set(user => user.AuthNotBefore, cutoff).Build())).EnsureAllOk();
            current = current with { Credential = current.Credential with { AuthNotBefore = cutoff, IssuedAt = cutoff.AddSeconds(1) } };
        }
        if (change == "original-revoked-then-renewed")
            row = (await _store.UpdateAsync(Renewed(row, new NftActorContinuation(
                new(Guid.NewGuid(), row.Version, row.State, row.Stage, row.PreparationGeneration), current, DateTimeOffset.UtcNow)),
                row.Version, requireActionAuthority: true))!;
        if (change == "current-expired")
            current = current with { Credential = current.Credential with {
                IssuedAt = DateTimeOffset.UtcNow.AddHours(-2), ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) } };
        if (change == "payer-wallet-moved")
            (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<Wallet>(original.Wallet.Id.ToString("N"))
                .Set(wallet => wallet.Address, "moved-address").Build())).EnsureAllOk();
        var (accepted, _) = await ReplacementAcceptance(row, current);

        var saved = await _store.UpdateAsync(accepted, row.Version, requireActionAuthority: true);

        (saved is not null).Should().Be(expected);
        (await _store.GetAsync(row.Id))!.ReplacementCostConsentsJson.Should().Be(expected ? accepted.ReplacementCostConsentsJson : "[]");
    }

    [Fact]
    public async Task AcceptanceAndRetirementOfTheSameRequestHaveExactlyOneWinner()
    {
        var (row, original) = await ExpiredGeneration();
        var (accepted, consent) = await ReplacementAcceptance(row, Current(original));
        var retiring = Copy(row);
        retiring.ReplacementCostRetirementsJson = JsonSerializer.Serialize(new[] {
            new NftReplacementCostRetirement(consent.Request, Current(original), DateTimeOffset.UtcNow) });

        var outcomes = await Task.WhenAll(_store.UpdateAsync(accepted, row.Version, requireActionAuthority: true),
            _store.UpdateAsync(retiring, row.Version)).WaitAsync(TimeSpan.FromSeconds(15));

        outcomes.Count(outcome => outcome is not null).Should().Be(1);
        var stored = (await _store.GetAsync(row.Id))!;
        (NftReplacementCostContract.Read(stored).Count + NftReplacementCostContract.Retirements(stored).Count).Should().Be(1);
        stored.Version.Should().Be(row.Version + 1);
    }

    [Fact]
    public async Task CancellationAndReplacementAcceptanceHaveExactlyOneWinner()
    {
        var (row, original) = await ExpiredGeneration();
        var (accepted, _) = await ReplacementAcceptance(row, Current(original));
        var accepting = _store.UpdateAsync(accepted, row.Version, requireActionAuthority: true);
        var cancelling = _store.CancelUnsentAsync(row, null, RequesterConsent(row));
        await Task.WhenAll(accepting, cancelling).WaitAsync(TimeSpan.FromSeconds(15));
        ((await accepting is not null) ^ await cancelling).Should().BeTrue();
        var stored = (await _store.GetAsync(row.Id))!;
        (stored.State == "Cancelled").Should().Be(stored.ReplacementCostConsentsJson == "[]");
    }

    [Fact]
    public async Task LapsedAcceptingCredentialIsImmutableEvidenceAndDoesNotStallFirstSend()
    {
        var (row, original) = await ExpiredGeneration();
        var shortLived = Current(original) with { Credential = Current(original).Credential with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(3) } };
        var (accepted, _) = await ReplacementAcceptance(row, shortLived);
        var saved = await PreparedReplacement((await _store.UpdateAsync(accepted, row.Version, requireActionAuthority: true))!);
        await Task.Delay(TimeSpan.FromSeconds(4));

        saved.State = "Submitted";
        (await _store.UpdateAsync(saved, saved.Version, requireActionAuthority: true)).Should().NotBeNull(
            "later steps fence the current requester, not the accepting credential checked at its own commit");
    }

    [Fact]
    public async Task RevokedRequesterBlocksReplacementFirstSendButNotObservation()
    {
        var (row, original) = await ExpiredGeneration();
        var (accepted, _) = await ReplacementAcceptance(row, Current(original));
        var saved = await PreparedReplacement((await _store.UpdateAsync(accepted, row.Version, requireActionAuthority: true))!);
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(original.Credential.ActorUserId.ToString("N"))
            .Set(user => user.AuthNotBefore, DateTimeOffset.UtcNow).Build())).EnsureAllOk();

        saved.State = "Submitted";
        (await _store.UpdateAsync(saved, saved.Version, requireActionAuthority: true)).Should().BeNull();
        var stored = (await _store.GetAsync(row.Id))!;
        stored.State.Should().Be("Prepared");
        stored.Detail = "Observation of already-authorized wire continues";
        (await _store.UpdateAsync(stored, stored.Version)).Should().NotBeNull();
    }

    [Fact]
    public async Task RequesterRenewsBeforeReplacementCostsAreAcceptedWithoutDeadlock()
    {
        var (row, original) = await ExpiredGeneration();
        var renewed = await _store.UpdateAsync(Renewed(row, new NftActorContinuation(
            new(Guid.NewGuid(), row.Version, row.State, row.Stage, row.PreparationGeneration), Current(original), DateTimeOffset.UtcNow)),
            row.Version, requireActionAuthority: true);
        renewed.Should().NotBeNull("a renewal-only write does not require the not-yet-accepted replacement costs");

        var (accepted, _) = await ReplacementAcceptance(renewed!, Current(original));
        (await _store.UpdateAsync(accepted, renewed!.Version, requireActionAuthority: true)).Should().NotBeNull();
    }

    [Fact]
    public async Task RenewalAfterConfirmedSoulboundReplacementCreateLetsTheRecipientAccept()
    {
        var (row, original) = await ExpiredGeneration(credential: true);
        var (accepted, _) = await ReplacementAcceptance(row, Current(original));
        var created = (await _store.UpdateAsync(accepted, row.Version, requireActionAuthority: true))!;
        created.AssetId = "replacement-mint"; created.ProtocolIdentity = "genesis"; created.ContractAddress = "metadata-program";
        created.TokenStandard = "MetaplexNonFungible"; created.CreationTransactionId = created.FinalTransactionId = "replacement-create";
        created.ConfirmedStageCount = 1; created.State = "AwaitingRecipientAcceptance";
        created = (await _store.UpdateAsync(created, created.Version))!;

        var renewed = await _store.UpdateAsync(Renewed(created, new NftActorContinuation(
            new(Guid.NewGuid(), created.Version, created.State, created.Stage, created.PreparationGeneration), Current(original), DateTimeOffset.UtcNow)),
            created.Version, requireActionAuthority: true);
        renewed.Should().NotBeNull("the confirmed Create no longer matches its pre-confirmation cost intent, and renewal must not depend on it");

        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(renewed!.RequestJson)!;
        var acceptance = JsonSerializer.Deserialize<NftLifecycle>(JsonSerializer.Serialize(renewed))!;
        acceptance.Stage = "Accept"; acceptance.State = "Preparing";
        var intent = NftLifecycleIntentFactory.Create(acceptance, command, ChainNetwork.Devnet, command.PlatformAuthorityAddress, Guid.Empty)!;
        var holder = DirectAuthority(command.Recipient, command.RecipientWalletId, command.RecipientAddress, "Solana", "Mint");
        acceptance.StageConsentsJson = JsonSerializer.Serialize(new[] { new NftStageConsent(renewed.Id, "Accept", renewed.PreparationGeneration,
            NftStageConsentIdentity.Hash(acceptance, intent), holder, DateTimeOffset.UtcNow,
            new(Guid.NewGuid(), renewed.Version, renewed.State, renewed.Stage, renewed.PreparationGeneration)) });
        (await _store.UpdateAsync(acceptance, renewed.Version, requireActionAuthority: true)).Should().NotBeNull();
    }

    private async Task<(NftLifecycle Row, SigningAdmissionAuthority Original)> ExpiredGeneration(bool credential = false)
    {
        var (row, quote) = await CostCandidate(credential: credential);
        await new SurrealNftProtocolCostQuoteStore(_executor).ClaimAsync(quote);
        var admitted = (await _store.ClaimAsync(row))!;
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(admitted.RequestJson)!;
        var intent = NftLifecycleIntentFactory.Create(admitted, command, ChainNetwork.Devnet, command.PlatformAuthorityAddress, Guid.Empty)!;
        admitted.TransactionHistoryJson = JsonSerializer.Serialize(new[] { new NftTransactionAttempt(
            new PreparedNftTransaction("expired-original", "signed", intent, "genesis", "metadata-program"), "ExpiredAbsent", DateTimeOffset.UtcNow) });
        admitted.PreparationGeneration = 1;
        var expired = (await _store.UpdateAsync(admitted, admitted.Version))!;
        NftReplacementCostContract.Eligible(expired).Should().BeTrue();
        return (expired, command.ActorAuthority!);
    }

    private async Task<(NftLifecycle Accepted, NftReplacementCostConsent Consent)> ReplacementAcceptance(NftLifecycle row,
        SigningAdmissionAuthority accepting, DateTimeOffset? createdAt = null, TimeSpan? lifetime = null)
    {
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!;
        var intent = NftLifecycleIntentFactory.Create(row, command, ChainNetwork.Devnet, command.PlatformAuthorityAddress, command.ActingTenantId)!;
        var start = createdAt ?? DateTimeOffset.UtcNow.AddMinutes(-1);
        var quote = new NftProtocolCostQuote(Guid.NewGuid().ToString("N"), command.Actor, Guid.Empty, command.ActorWalletId,
            command.ActorAddress, "Solana", ChainNetwork.Devnet, "genesis", row.Id,
            NftProtocolCostContract.DraftHash(row.Id, command.Actor, command.Recipient, command.Metadata), "Create",
            row.PreparationGeneration, "MetaplexNonFungible", "{\"revision\":2}", new("5000", "10000000", "10000000"),
            start, start + (lifetime ?? TimeSpan.FromMinutes(6)), NftProtocolCostContract.IntentHash(intent, command.RecipientWalletId));
        await new SurrealNftProtocolCostQuoteStore(_executor).ClaimAsync(quote);
        var request = new NftReplacementCostAcceptance(new(Guid.NewGuid(), row.Version, row.State, row.Stage, row.PreparationGeneration),
            new(quote.QuoteId, NftProtocolCostContract.Hash(quote), quote.Limits));
        var consent = new NftReplacementCostConsent(request, quote, accepting, DateTimeOffset.UtcNow);
        var accepted = Copy(row);
        accepted.ReplacementCostConsentsJson = JsonSerializer.Serialize(NftReplacementCostContract.Read(row).Append(consent));
        return (accepted, consent);
    }

    private async Task<NftLifecycle> PreparedReplacement(NftLifecycle saved)
    {
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(saved.RequestJson)!;
        var intent = NftLifecycleIntentFactory.Create(saved, command, ChainNetwork.Devnet, command.PlatformAuthorityAddress, Guid.Empty)!;
        saved.PreparedTransactionJson = JsonSerializer.Serialize(new PreparedNftTransaction("replacement", "signed", intent, "genesis", "metadata-program"));
        saved.State = "Prepared";
        return (await _store.UpdateAsync(saved, saved.Version))!;
    }

    private static SigningConsentWitness RequesterConsent(NftLifecycle row)
    {
        var original = JsonSerializer.Deserialize<NftLifecycleCommand>(row.RequestJson)!.ActorAuthority!;
        return new(1, original.Credential, [NftSigningScope.ForOperation(row.Operation)], original.Grants);
    }

    private static SigningAdmissionAuthority Current(SigningAdmissionAuthority original)
        => original with { Credential = original.Credential with { IssuedAt = DateTimeOffset.UtcNow.AddSeconds(-5) } };

    private static NftLifecycle Copy(NftLifecycle row) => JsonSerializer.Deserialize<NftLifecycle>(JsonSerializer.Serialize(row))!;
}
