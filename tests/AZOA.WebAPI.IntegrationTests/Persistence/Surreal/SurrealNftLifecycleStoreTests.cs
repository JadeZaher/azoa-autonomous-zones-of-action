using System.Text.Json;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Helpers;
using AZOA.WebAPI.Models.Blockchain;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Signing;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Providers.Stores.Surreal;
using FluentAssertions;
using SurrealForge.Client;
using SurrealForge.Client.Connection;
using SurrealForge.Client.Query;

namespace AZOA.WebAPI.IntegrationTests.Persistence.Surreal;

public sealed partial class SurrealNftLifecycleStoreTests : IAsyncLifetime
{
    private readonly string _namespace = $"test{Guid.NewGuid():N}";
    private ISurrealConnection _connection = null!;
    private ISurrealExecutor _executor = null!;
    private SurrealNftLifecycleStore _store = null!;

    public async Task InitializeAsync()
    {
        await SurrealTestSchema.BootstrapAsync(_namespace, "nft_lifecycle", "nft_asset_authority", "nft_ownership", "nft_protocol_cost_quote",
            "nft_workflow_cost_acceptance", "nft_workflow_cost_retirement", "user", "wallet", "consent_grant", "api_key", "action_definition", "action_node", "action_run", "action_node_execution");
        _connection = new SurrealDbNetConnection(new SurrealConnectionOptions
        {
            Endpoint = SurrealTestDefaults.Endpoint, Namespace = _namespace, Database = "test",
            User = SurrealTestDefaults.User, Password = SurrealTestDefaults.Password,
        });
        _executor = new DefaultSurrealExecutor(_connection);
        _store = new SurrealNftLifecycleStore(_executor);
    }
    public async Task DisposeAsync()
    {
        _connection?.Dispose();
        await SurrealTestSchema.DropAsync(_namespace);
    }

    [Fact]
    public async Task AtomicCommitScopesTwoTypedWritersAndStaleReplayCannotChangeOwnership()
    {
        var lifecycle = await Admittable(Candidate());
        (await _store.ClaimAsync(lifecycle)).Should().NotBeNull();
        var authority = Authority(lifecycle);
        var ownership = Ownership(lifecycle);
        (await _store.CommitAsync(lifecycle, authority, ownership)).Should().BeTrue();
        var storedAuthority = await _store.GetAuthorityAsync(lifecycle.NftId);
        var storedOwnership = await _executor.QuerySingleAsync<NftOwnership>(SurrealQuery<NftOwnership>.Key(lifecycle.NftId));
        storedAuthority!.Id.Should().Be(authority.Id);
        storedAuthority.AssetId.Should().Be("777");
        storedOwnership!.Id.Should().Be(lifecycle.NftId);
        storedOwnership.Name.Should().Be("Ownership projection");
        storedOwnership.TokenId.Should().Be("777");
        (await _store.GetAsync(lifecycle.Id))!.State.Should().Be("Completed");
        ownership.Name = "stale overwrite";
        (await _store.CommitAsync(lifecycle, authority, ownership)).Should().BeFalse();
        (await _executor.QuerySingleAsync<NftOwnership>(SurrealQuery<NftOwnership>.Key(lifecycle.NftId)))!.Name.Should().Be("Ownership projection");
    }

    [Fact]
    public async Task AssetReservationExcludesAnotherOperationAndUnsentCancellationReleasesIt()
    {
        var mint = await Admittable(Candidate());
        (await _store.ClaimAsync(mint)).Should().NotBeNull();
        var authority = Authority(mint);
        await _store.CommitAsync(mint, authority, Ownership(mint));
        var first = Candidate(); first.NftId = mint.NftId; first.Operation = "Transfer"; first.State = "Preparing";
        var second = Candidate(); second.NftId = mint.NftId; second.Operation = "Burn"; second.State = "Preparing";
        (await _store.ClaimAsync(await Admittable(first))).Should().NotBeNull();
        (await _store.ClaimAsync(await Admittable(second))).Should().NotBeNull();
        (await _store.ReserveAuthorityAsync(authority, first)).Should().BeTrue();
        (await _store.ReserveAuthorityAsync(authority, second)).Should().BeFalse();
        (await _store.CancelUnsentAsync(first, authority, RequesterConsent(first))).Should().BeTrue();
        (await _store.GetAuthorityAsync(mint.NftId))!.ActiveLifecycleId.Should().BeNull();
        (await _store.ReserveAuthorityAsync(authority, second)).Should().BeTrue();
        (await _store.ReserveAuthorityAsync(authority, first)).Should().BeFalse("a cancelled lifecycle cannot reserve again");
    }

    [Fact]
    public async Task ConcurrentClaimsShareOneDurableAdmissionAndCancellationHistorySurvives()
    {
        var first = await Admittable(Candidate()); first.State = "Preparing";
        var competing = await Admittable(Candidate()); competing.Id = first.Id; competing.State = "Cancelled"; competing.Stage = "Cancellation";
        var results = await Task.WhenAll(_store.ClaimAsync(first), _store.ClaimAsync(competing)).WaitAsync(TimeSpan.FromSeconds(15));
        results.Should().NotContainNulls();
        results[0]!.RequestHash.Should().Be(results[1]!.RequestHash);
        results[0]!.State.Should().Be(results[1]!.State);
        var noEffect = Candidate(); noEffect.State = "Preparing"; noEffect.TransactionHistoryJson = "[{\"verdict\":\"ExpiredAbsent\"}]";
        noEffect.PreparationGeneration = 1;
        (await _store.ClaimAsync(await Admittable(noEffect))).Should().NotBeNull();
        (await _store.CancelUnsentAsync(noEffect, null, RequesterConsent(noEffect))).Should().BeTrue();
        (await _store.GetAsync(noEffect.Id))!.TransactionHistoryJson.Should().Be(noEffect.TransactionHistoryJson);
        var priorEffect = Candidate(); priorEffect.State = "Preparing"; priorEffect.ConfirmedStageCount = 1;
        (await _store.ClaimAsync(await Admittable(priorEffect))).Should().NotBeNull();
        (await _store.CancelUnsentAsync(priorEffect, null, RequesterConsent(priorEffect))).Should().BeFalse();
    }

    /// <summary>Binds a fixture row to a real active user, owned wallet and direct credential, as admission now requires.</summary>
    private async Task<NftLifecycle> Admittable(NftLifecycle row)
    {
        var actor = Guid.Parse(row.ActorUserId);
        var authority = await SeedSignerAsync(actor, Guid.NewGuid(), "owner-" + row.ActorUserId, row.ChainType, row.Operation);
        var command = new NftLifecycleCommand(actor, Guid.Parse(row.RecipientUserId), authority.Wallet.Id, Guid.NewGuid(),
            authority.Wallet.Address, "holder", new UserNFTMintModel { ChainType = row.ChainType, MetadataURI = "ipfs://asset", Name = "Ownership projection" },
            -1, Guid.Empty, ActorAuthority: authority);
        row.RequestJson = JsonSerializer.Serialize(command);
        row.RequestHash = NftLifecycleIdentity.Fingerprint(row.RequestJson);
        return row;
    }

    private async Task<SigningAdmissionAuthority> SeedSignerAsync(Guid user, Guid wallet, string address, string chain, string operation)
    {
        (await _executor.ExecuteAsync(SurrealWriter.Create(new User { Id = user.ToString("N"), Username = user.ToString("N"),
            Email = user + "@test.invalid", PasswordHash = "fixture", IsActive = true }))).EnsureAllOk();
        (await _executor.ExecuteAsync(SurrealWriter.Create(new Wallet { Id = wallet.ToString("N"), UserId = "user:" + user.ToString("N"),
            Address = address, ChainType = chain }))).EnsureAllOk();
        return DirectAuthority(user, wallet, address, chain, operation);
    }

    private static SigningAdmissionAuthority DirectAuthority(Guid user, Guid wallet, string address, string chain, string operation,
        DateTimeOffset? issuedAt = null, DateTimeOffset? expiresAt = null)
        => new(1, "DirectJwt", new(1, "Jwt", user, null, null, issuedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
                expiresAt ?? DateTimeOffset.UtcNow.AddHours(1), null, null, [], true, TokenUse: AzoaClaims.TokenUseLogin),
            new(wallet, user, address, chain), [NftSigningScope.ForOperation(operation)], []);

    private static NftLifecycle Candidate() => new()
    {
        Id = Guid.NewGuid().ToString("N"), NftId = Guid.NewGuid().ToString("N"), ActorUserId = Guid.NewGuid().ToString("N"),
        RecipientUserId = Guid.NewGuid().ToString("N"), RequestHash = Guid.NewGuid().ToString("N"), RequestJson = "{}",
        Operation = "Mint", ChainType = "Algorand", Network = "Devnet", State = "Confirmed", Stage = "Create",
        AssetId = "777", ProtocolIdentity = "genesis", ContractAddress = "ASA", TokenStandard = "ASA",
        CreationTransactionId = "confirmed-create", FinalTransactionId = "confirmed-create", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };
    private static NftAssetAuthority Authority(NftLifecycle row) => new()
    {
        Id = Guid.NewGuid().ToString("N"), NftId = row.NftId, ChainType = row.ChainType, Network = row.Network,
        ProtocolIdentity = "genesis", ContractAddress = "ASA", AssetId = "777", TokenStandard = "ASA",
        CreatorUserId = row.ActorUserId, HolderUserId = row.RecipientUserId, OwnerAddress = "holder",
        IssuanceLifecycleId = row.Id, LatestLifecycleId = row.Id, CreationTransactionId = "confirmed-create", LatestTransactionId = "confirmed-create",
        UpdatedAt = row.CreatedAt,
    };
    private static NftOwnership Ownership(NftLifecycle row) => new()
    {
        Id = row.NftId, UserId = row.RecipientUserId, ChainType = row.ChainType, ContractAddress = "ASA", TokenId = "777", TokenStandard = "ASA",
        MetadataUri = "ipfs://asset", Name = "Ownership projection", IsCurrent = true, IsActive = true,
        CurrentOwner = "holder", MintedDate = row.CreatedAt,
    };
}
