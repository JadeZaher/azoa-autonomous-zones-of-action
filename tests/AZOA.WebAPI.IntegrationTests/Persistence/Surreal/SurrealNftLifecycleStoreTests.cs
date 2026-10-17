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
            "nft_workflow_cost_acceptance", "nft_workflow_cost_retirement", "user", "wallet", "action_definition", "action_node", "action_run", "action_node_execution");
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
        var lifecycle = Candidate();
        await _store.ClaimAsync(lifecycle);
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
        var mint = Candidate();
        await _store.ClaimAsync(mint);
        var authority = Authority(mint);
        await _store.CommitAsync(mint, authority, Ownership(mint));
        var first = Candidate(); first.NftId = mint.NftId; first.Operation = "Transfer"; first.State = "Preparing";
        var second = Candidate(); second.NftId = mint.NftId; second.Operation = "Burn"; second.State = "Preparing";
        await _store.ClaimAsync(first);
        await _store.ClaimAsync(second);
        (await _store.ReserveAuthorityAsync(authority, first)).Should().BeTrue();
        (await _store.ReserveAuthorityAsync(authority, second)).Should().BeFalse();
        (await _store.CancelUnsentAsync(first, authority)).Should().BeTrue();
        (await _store.GetAuthorityAsync(mint.NftId))!.ActiveLifecycleId.Should().BeNull();
        (await _store.ReserveAuthorityAsync(authority, second)).Should().BeTrue();
        (await _store.ReserveAuthorityAsync(authority, first)).Should().BeFalse("a cancelled lifecycle cannot reserve again");
    }

    [Fact]
    public async Task ConcurrentClaimsShareOneDurableAdmissionAndCancellationHistorySurvives()
    {
        var first = Candidate(); first.State = "Preparing";
        var competing = Candidate(); competing.Id = first.Id; competing.State = "Cancelled"; competing.Stage = "Cancellation";
        var results = await Task.WhenAll(_store.ClaimAsync(first), _store.ClaimAsync(competing)).WaitAsync(TimeSpan.FromSeconds(15));
        results.Should().NotContainNulls();
        results[0]!.RequestHash.Should().Be(results[1]!.RequestHash);
        results[0]!.State.Should().Be(results[1]!.State);
        var noEffect = Candidate(); noEffect.State = "Preparing"; noEffect.TransactionHistoryJson = "[{\"verdict\":\"ExpiredAbsent\"}]";
        noEffect.PreparationGeneration = 1;
        await _store.ClaimAsync(noEffect);
        (await _store.CancelUnsentAsync(noEffect, null)).Should().BeTrue();
        (await _store.GetAsync(noEffect.Id))!.TransactionHistoryJson.Should().Be(noEffect.TransactionHistoryJson);
        var priorEffect = Candidate(); priorEffect.State = "Preparing"; priorEffect.ConfirmedStageCount = 1;
        await _store.ClaimAsync(priorEffect);
        (await _store.CancelUnsentAsync(priorEffect, null)).Should().BeFalse();
    }

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
