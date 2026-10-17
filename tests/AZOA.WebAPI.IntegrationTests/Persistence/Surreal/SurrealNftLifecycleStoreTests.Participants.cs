using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealNftLifecycleStoreTests
{
    [Theory]
    [InlineData("none", true)]
    [InlineData("issuer-cutoff", false)]
    [InlineData("participant-cutoff", false)]
    [InlineData("participant-wallet", false)]
    public async Task ParticipantAcceptanceAtomicallyRequiresOriginalAndCurrentParticipantAuthority(string change, bool expected)
    {
        var (before, accepted, consent) = await ParticipantCandidate();
        if (change.EndsWith("cutoff", StringComparison.Ordinal))
        {
            var user = change == "issuer-cutoff" ? before.ActorUserId : before.RecipientUserId;
            (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(user)
                .Set(item => item.AuthNotBefore, DateTimeOffset.UtcNow).Build())).EnsureAllOk();
        }
        if (change == "participant-wallet")
            (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<Wallet>(consent.Authority.Wallet.Id.ToString("N"))
                .Set(item => item.Address, "changed-address").Build())).EnsureAllOk();

        var saved = await _store.UpdateAsync(accepted, before.Version, requireActionAuthority: true);

        (saved is not null).Should().Be(expected);
        var actual = (await _store.GetAsync(before.Id))!;
        actual.RequestJson.Should().Be(before.RequestJson);
        actual.StageConsentsJson.Should().Be(expected ? accepted.StageConsentsJson : "[]");
        actual.State.Should().Be(expected ? "Preparing" : "AwaitingRecipientAcceptance");
    }

    [Fact]
    public async Task SavedParticipantRevocationBlocksFirstEgressButNotObservationAndCannotRewriteConsent()
    {
        var (before, accepted, consent) = await ParticipantCandidate();
        var saved = (await _store.UpdateAsync(accepted, before.Version, requireActionAuthority: true))!;
        var originalConsent = saved.StageConsentsJson;
        var command = JsonSerializer.Deserialize<NftLifecycleCommand>(saved.RequestJson)!;
        var intent = NftLifecycleIntentFactory.Create(saved, command, ChainNetwork.Devnet, "platform", Guid.Empty)!;
        saved.PreparedTransactionJson = JsonSerializer.Serialize(new PreparedNftTransaction("exact", "signed", intent, "genesis", "ASA"));
        saved.State = "Prepared";
        saved = (await _store.UpdateAsync(saved, saved.Version))!;
        (await _executor.ExecuteAsync(SurrealWriter.UpdateOnly<User>(before.RecipientUserId)
            .Set(item => item.AuthNotBefore, DateTimeOffset.UtcNow).Build())).EnsureAllOk();
        saved.State = "Submitted";
        (await _store.UpdateAsync(saved, saved.Version, requireActionAuthority: true)).Should().BeNull();
        var stored = (await _store.GetAsync(saved.Id))!;
        stored.State.Should().Be("Prepared");
        stored.StageConsentsJson = JsonSerializer.Serialize(new[] { consent with { AcceptedAt = DateTimeOffset.UtcNow } });
        (await _store.UpdateAsync(stored, stored.Version)).Should().BeNull();
        stored.StageConsentsJson = originalConsent;
        stored.Detail = "Read-only observation remains possible after revocation.";
        (await _store.UpdateAsync(stored, stored.Version))!.Detail.Should().Be(stored.Detail);
    }

    private async Task<(NftLifecycle Before, NftLifecycle Accepted, NftStageConsent Consent)> ParticipantCandidate(TimeSpan? issuerLifetime = null, TimeSpan? participantLifetime = null)
    {
        var actor = Guid.NewGuid(); var recipient = Guid.NewGuid();
        var actorWallet = Guid.NewGuid(); var recipientWallet = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        SigningAdmissionAuthority Authority(Guid user, Guid wallet, string address, TimeSpan? lifetime = null) => new(1, "DirectJwt",
            new(1, "Jwt", user, null, null, now.AddMinutes(-1), now + (lifetime ?? TimeSpan.FromHours(1)), null, null, [], true,
                TokenUse: AzoaClaims.TokenUseLogin), new(wallet, user, address, "Algorand"), [AzoaScopes.NftMint], []);
        foreach (var (user, wallet, address) in new[] { (actor, actorWallet, "issuer"), (recipient, recipientWallet, "holder") })
        {
            (await _executor.ExecuteAsync(SurrealWriter.Create(new User { Id = user.ToString("N"), Username = user.ToString("N"),
                Email = user + "@test.invalid", PasswordHash = "fixture", IsActive = true }))).EnsureAllOk();
            (await _executor.ExecuteAsync(SurrealWriter.Create(new Wallet { Id = wallet.ToString("N"), UserId = "user:" + user.ToString("N"),
                Address = address, ChainType = "Algorand" }))).EnsureAllOk();
        }
        var command = new NftLifecycleCommand(actor, recipient, actorWallet, recipientWallet, "issuer", "holder",
            new UserNFTMintModel { ChainType = "Algorand", MetadataURI = "ipfs://credential", Name = "Credential", IsSoulbound = true },
            -1, Guid.Empty, FeeQuote: new("Algorand", ChainNetwork.Devnet, "Mint", 1, "ALGO", 6, "0", "treasury", actor),
            ActorAuthority: Authority(actor, actorWallet, "issuer", issuerLifetime), PlatformAuthorityAddress: "platform");
        var json = JsonSerializer.Serialize(command);
        var before = new NftLifecycle { Id = Guid.NewGuid().ToString("N"), NftId = Guid.NewGuid().ToString("N"),
            ActorUserId = actor.ToString("N"), RecipientUserId = recipient.ToString("N"), Operation = "Mint", ChainType = "Algorand",
            Network = "Devnet", State = "AwaitingRecipientAcceptance", Stage = "Create", ConfirmedStageCount = 1,
            AssetId = "777", ProtocolIdentity = "genesis", ContractAddress = "ASA", TokenStandard = "ASA",
            RequestJson = json, RequestHash = NftLifecycleIdentity.Fingerprint(json), CreatedAt = now, UpdatedAt = now };
        (await _store.ClaimAsync(before)).Should().NotBeNull();
        var accepted = JsonSerializer.Deserialize<NftLifecycle>(JsonSerializer.Serialize(before))!;
        accepted.Stage = "Accept"; accepted.State = "Preparing";
        var intent = NftLifecycleIntentFactory.Create(accepted, command, ChainNetwork.Devnet, "platform", Guid.Empty)!;
        var consent = new NftStageConsent(before.Id, "Accept", 0, NftStageConsentIdentity.Hash(accepted, intent),
            Authority(recipient, recipientWallet, "holder", participantLifetime), now,
            new(Guid.NewGuid(), before.Version, before.State, before.Stage, before.PreparationGeneration));
        accepted.StageConsentsJson = JsonSerializer.Serialize(new[] { consent });
        return (before, accepted, consent);
    }
}
