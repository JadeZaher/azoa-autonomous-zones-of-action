using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using FluentAssertions;
using SurrealForge.Client.Query;
using GrantRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ConsentGrant;
using KeyRow = AZOA.WebAPI.Persistence.SurrealDb.Models.ApiKey;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

/// <summary>Lapsed requester or participant credentials stall new effects until the same party renews them.</summary>
public sealed partial class SurrealNftLifecycleStoreTests
{
    [Fact]
    public async Task LapsedIssuerCredentialBlocksAcceptanceUntilTheIssuerRenewsWithoutChangingTheCommand()
    {
        var (before, accepted, consent) = await ParticipantCandidate(issuerLifetime: TimeSpan.FromSeconds(3));
        await Task.Delay(TimeSpan.FromSeconds(4));
        (await _store.UpdateAsync(accepted, before.Version, requireActionAuthority: true)).Should().BeNull(
            "an expired original direct credential cannot authorize the next stage");

        var original = JsonSerializer.Deserialize<NftLifecycleCommand>(before.RequestJson)!.ActorAuthority!;
        var stage = new NftStageAcceptance(Guid.NewGuid(), before.Version, before.State, before.Stage, before.PreparationGeneration);
        var stranger = Renewed(before, new NftActorContinuation(stage, DirectAuthority(Guid.NewGuid(), original.Wallet.Id,
            original.Wallet.Address, original.Wallet.ChainType, before.Operation), DateTimeOffset.UtcNow));
        (await _store.UpdateAsync(stranger, before.Version, requireActionAuthority: true)).Should().BeNull("only the original requester may renew");

        var renewal = Renewed(before, new NftActorContinuation(stage, DirectAuthority(original.Credential.ActorUserId, original.Wallet.Id,
            original.Wallet.Address, original.Wallet.ChainType, before.Operation), DateTimeOffset.UtcNow));
        var saved = await _store.UpdateAsync(renewal, before.Version, requireActionAuthority: true);
        saved.Should().NotBeNull();
        saved!.RequestJson.Should().Be(before.RequestJson, "renewal never rewrites the original command authority");
        saved.State.Should().Be(before.State);

        var retried = JsonSerializer.Deserialize<NftLifecycle>(JsonSerializer.Serialize(saved))!;
        retried.Stage = accepted.Stage;
        retried.State = accepted.State;
        retried.StageConsentsJson = JsonSerializer.Serialize(new[] { consent with { Request = consent.Request! with { ExpectedVersion = saved.Version } } });
        (await _store.UpdateAsync(retried, saved.Version, requireActionAuthority: true)).Should().NotBeNull(
            "the participant stage proceeds under the renewed requester authority");
    }

    [Fact]
    public async Task LapsedRequesterRenewsWhileARefundAwaitsTheHolderAndTheHolderThenAuthorizes()
    {
        var sender = Guid.NewGuid(); var holder = Guid.NewGuid(); var senderWallet = Guid.NewGuid(); var holderWallet = Guid.NewGuid();
        await SeedSignerAsync(sender, senderWallet, "sender", "Algorand", "Refund");
        var holderAuthority = await SeedSignerAsync(holder, holderWallet, "holder", "Algorand", "Refund");
        var now = DateTimeOffset.UtcNow;
        var command = new NftLifecycleCommand(sender, holder, senderWallet, holderWallet, "sender", "holder",
            new UserNFTMintModel { ChainType = "Algorand", MetadataURI = "ipfs://asset", Name = "Returned", IsTransferable = true }, 0, Guid.Empty,
            SourceLifecycleId: Guid.NewGuid().ToString("N"), FeeQuote: new("Algorand", ChainNetwork.Devnet, "Refund", 1, "ALGO", 6, "0", "treasury", sender),
            ActorAuthority: DirectAuthority(sender, senderWallet, "sender", "Algorand", "Refund", expiresAt: now.AddSeconds(3)));
        var json = JsonSerializer.Serialize(command);
        var before = new NftLifecycle { Id = Guid.NewGuid().ToString("N"), NftId = Guid.NewGuid().ToString("N"), ActorUserId = sender.ToString("N"),
            RecipientUserId = holder.ToString("N"), Operation = "Refund", ChainType = "Algorand", Network = "Devnet",
            State = "AwaitingRefundAuthorization", Stage = "Transfer", AssetId = "777", ProtocolIdentity = "genesis", ContractAddress = "ASA",
            TokenStandard = "ASA", CreationTransactionId = "create", RequestJson = json, RequestHash = NftLifecycleIdentity.Fingerprint(json),
            CreatedAt = now, UpdatedAt = now };
        (await _store.ClaimAsync(before)).Should().NotBeNull();
        await Task.Delay(TimeSpan.FromSeconds(4));
        (await _store.UpdateAsync(HolderAuthorization(before, command, holderAuthority), before.Version, requireActionAuthority: true))
            .Should().BeNull("the lapsed requester witness still fences the holder's stage");

        var renewed = await _store.UpdateAsync(Renewed(before, new NftActorContinuation(
            new(Guid.NewGuid(), before.Version, before.State, before.Stage, before.PreparationGeneration),
            DirectAuthority(sender, senderWallet, "sender", "Algorand", "Refund"), DateTimeOffset.UtcNow)), before.Version, requireActionAuthority: true);
        renewed.Should().NotBeNull("renewal does not need the holder's not-yet-given consent");
        (await _store.UpdateAsync(HolderAuthorization(renewed!, command, holderAuthority), renewed!.Version, requireActionAuthority: true))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task LapsedRecipientConsentBeforePreparationIsRenewedOnlyByThatRecipient()
    {
        var (before, accepted, consent) = await ParticipantCandidate(participantLifetime: TimeSpan.FromSeconds(3));
        var saved = (await _store.UpdateAsync(accepted, before.Version, requireActionAuthority: true))!;
        var holder = consent.Authority;
        (await _store.UpdateAsync(ParticipantRenewal(saved, consent, DirectAuthority(holder.Credential.ActorUserId, holder.Wallet.Id,
            holder.Wallet.Address, holder.Wallet.ChainType, "Mint")), saved.Version, requireActionAuthority: true))
            .Should().BeNull("a live participant credential cannot be superseded");
        await Task.Delay(TimeSpan.FromSeconds(4));
        saved.Detail = "Preparing the accepted stage";
        (await _store.UpdateAsync(saved, saved.Version, requireActionAuthority: true)).Should().BeNull(
            "the lapsed participant credential cannot authorize preparation");

        var stored = (await _store.GetAsync(saved.Id))!;
        var wallet = consent.Authority.Wallet;
        (await _store.UpdateAsync(ParticipantRenewal(stored, consent, DirectAuthority(Guid.NewGuid(), wallet.Id, wallet.Address, wallet.ChainType, "Mint")),
            stored.Version, requireActionAuthority: true)).Should().BeNull("only the participant who accepted may renew");
        var renewed = await _store.UpdateAsync(ParticipantRenewal(stored, consent,
            DirectAuthority(consent.Authority.Credential.ActorUserId, wallet.Id, wallet.Address, wallet.ChainType, "Mint")),
            stored.Version, requireActionAuthority: true);
        renewed.Should().NotBeNull();
        NftStageConsentIdentity.Read(renewed!).Should().HaveCount(2);
        renewed!.Detail = "Preparing under the renewed consent";
        (await _store.UpdateAsync(renewed, renewed.Version, requireActionAuthority: true)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("jwt", false)]
    [InlineData("jwt", true)]
    [InlineData("api-key", false)]
    [InlineData("api-key", true)]
    [InlineData("tenant-grant", false)]
    [InlineData("tenant-grant", true)]
    public async Task RenewalCommitsOnlyWhileTheRenewingCredentialIsStillLiveAtCommit(string kind, bool revokeAfterBinding)
    {
        var (row, renewal, revoke) = await RenewableCandidate(kind);
        if (revokeAfterBinding) await revoke();

        var saved = await _store.UpdateAsync(renewal, row.Version, requireActionAuthority: true);

        (saved is not null).Should().Be(!revokeAfterBinding);
        (await _store.GetAsync(row.Id))!.ActorContinuationsJson.Should().Be(revokeAfterBinding ? "[]" : renewal.ActorContinuationsJson);
    }

    private async Task<(NftLifecycle Row, NftLifecycle Renewal, Func<Task> Revoke)> RenewableCandidate(string kind)
    {
        var actor = Guid.NewGuid(); var wallet = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var original = await SeedSignerAsync(actor, wallet, "owner", "Algorand", "Mint");
        var tenant = Guid.Empty;
        SigningAdmissionAuthority fresh;
        Func<Task> revoke;
        if (kind == "api-key")
        {
            var key = Guid.NewGuid(); var hash = new string('b', 64);
            await Execute(SurrealWriter.Create(new KeyRow { Id = key.ToString("N"), UserId = $"user:{actor:N}", KeyHash = hash,
                KeyPrefix = "fixture", IsActive = true, Scopes = [AzoaScopes.NftMint], CreatedDate = now }));
            fresh = new(1, "DirectApiKey", new(1, "ApiKey", actor, null, null, null, null, key, hash, [AzoaScopes.NftMint], false, [AzoaScopes.NftMint]),
                new(wallet, actor, "owner", "Algorand"), [AzoaScopes.NftMint], []);
            revoke = () => Execute(SurrealWriter.UpdateOnly<KeyRow>(key.ToString("N")).Where(seed => true).Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build());
        }
        else if (kind == "tenant-grant")
        {
            tenant = Guid.NewGuid(); var grant = Guid.NewGuid();
            await Execute(SurrealWriter.Create(new User { Id = tenant.ToString("N"), Username = tenant.ToString("N"),
                Email = tenant + "@test.invalid", PasswordHash = "fixture", IsActive = true }));
            await Execute(SurrealWriter.Create(new GrantRow { Id = grant.ToString("N"), GrantorUserId = $"user:{actor:N}", TenantId = $"user:{tenant:N}",
                Scopes = AzoaScopes.NftMint, Origin = "UserExplicit", GrantedAt = now }));
            SigningAdmissionAuthority Child(DateTimeOffset issued) => new(1, "DirectJwt", new(1, "Jwt", actor, tenant, null, issued, issued.AddHours(1),
                null, null, [AzoaScopes.NftMint], false, TokenUse: AzoaClaims.TokenUseChild), new(wallet, actor, "owner", "Algorand"),
                [AzoaScopes.NftMint], [new(grant, AzoaScopes.NftMint, AzoaScopes.NftMint, now, null)]);
            original = Child(now.AddMinutes(-1));
            fresh = Child(now.AddSeconds(-5));
            revoke = () => Execute(SurrealWriter.UpdateOnly<GrantRow>(grant.ToString("N")).Where(seed => true).Set(row => row.RevokedAt, DateTimeOffset.UtcNow).Build());
        }
        else
        {
            fresh = DirectAuthority(actor, wallet, "owner", "Algorand", "Mint", issuedAt: now.AddSeconds(-5));
            revoke = () => Execute(SurrealWriter.UpdateOnly<User>(actor.ToString("N")).Where(seed => true).Set(row => row.AuthNotBefore, DateTimeOffset.UtcNow).Build());
        }
        var command = new NftLifecycleCommand(actor, actor, wallet, wallet, "owner", "owner",
            new UserNFTMintModel { ChainType = "Algorand", MetadataURI = "ipfs://asset", Name = "Renewable" }, -1, tenant,
            FeeQuote: new("Algorand", ChainNetwork.Devnet, "Mint", 1, "ALGO", 6, "0", "treasury", actor), ActorAuthority: original);
        var json = JsonSerializer.Serialize(command);
        var row = new NftLifecycle { Id = Guid.NewGuid().ToString("N"), NftId = Guid.NewGuid().ToString("N"), ActorUserId = actor.ToString("N"),
            RecipientUserId = actor.ToString("N"), Operation = "Mint", ChainType = "Algorand", Network = "Devnet", State = "Preparing",
            Stage = "Create", RequestJson = json, RequestHash = NftLifecycleIdentity.Fingerprint(json), CreatedAt = now, UpdatedAt = now };
        var admitted = (await _store.ClaimAsync(row))!;
        var renewal = Renewed(admitted, new NftActorContinuation(
            new(Guid.NewGuid(), admitted.Version, admitted.State, admitted.Stage, admitted.PreparationGeneration), fresh, now));
        return (admitted, renewal, revoke);
    }

    private static NftLifecycle HolderAuthorization(NftLifecycle before, NftLifecycleCommand command, SigningAdmissionAuthority holder)
    {
        var accepted = JsonSerializer.Deserialize<NftLifecycle>(JsonSerializer.Serialize(before))!;
        accepted.State = "Preparing";
        var intent = NftLifecycleIntentFactory.Create(accepted, command, ChainNetwork.Devnet, null, Guid.Empty)!;
        accepted.StageConsentsJson = JsonSerializer.Serialize(new[] { new NftStageConsent(before.Id, before.Stage, before.PreparationGeneration,
            NftStageConsentIdentity.Hash(accepted, intent), holder, DateTimeOffset.UtcNow,
            new(Guid.NewGuid(), before.Version, before.State, before.Stage, before.PreparationGeneration)) });
        return accepted;
    }

    private static NftLifecycle ParticipantRenewal(NftLifecycle row, NftStageConsent held, SigningAdmissionAuthority fresh)
    {
        var copy = JsonSerializer.Deserialize<NftLifecycle>(JsonSerializer.Serialize(row))!;
        var renewal = held with { Authority = fresh, AcceptedAt = DateTimeOffset.UtcNow,
            Request = new(Guid.NewGuid(), row.Version, row.State, row.Stage, row.PreparationGeneration) };
        copy.StageConsentsJson = JsonSerializer.Serialize(NftStageConsentIdentity.Read(row).Append(renewal));
        return copy;
    }

    private static NftLifecycle Renewed(NftLifecycle row, NftActorContinuation renewal)
    {
        var copy = JsonSerializer.Deserialize<NftLifecycle>(JsonSerializer.Serialize(row))!;
        copy.ActorContinuationsJson = JsonSerializer.Serialize(NftActorContinuationIdentity.Read(row).Append(renewal));
        return copy;
    }

    private async Task Execute(SurrealQuery query) => (await _executor.ExecuteAsync(query)).EnsureAllOk();
}
