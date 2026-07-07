using FluentAssertions;
using Moq;
using AZOA.WebAPI.Core;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Managers;

public class SearchManagerTests
{
    private readonly Mock<IAvatarStore> _avatarStore;
    private readonly Mock<IHolonStore> _holonStore;
    private readonly Mock<IWalletStore> _walletStore;
    private readonly Mock<IBlockchainOperationStore> _blockchainOperationStore;
    private readonly Mock<ISTARStore> _starStore;
    private readonly SearchManager _manager;

    public SearchManagerTests()
    {
        _avatarStore = new Mock<IAvatarStore>();
        _holonStore = new Mock<IHolonStore>();
        _walletStore = new Mock<IWalletStore>();
        _blockchainOperationStore = new Mock<IBlockchainOperationStore>();
        _starStore = new Mock<ISTARStore>();
        _manager = new SearchManager(
            _avatarStore.Object,
            _holonStore.Object,
            _walletStore.Object,
            _blockchainOperationStore.Object,
            _starStore.Object);
    }

    [Fact]
    public async Task SearchAsync_ReturnsHitsFromAvatars()
    {
        var avatar = new Avatar { Id = Guid.NewGuid(), Username = "neo", Email = "neo@matrix.com", CreatedDate = DateTime.UtcNow };
        _avatarStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IAvatar>> { Result = new List<IAvatar> { avatar } });
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon>() });
        _walletStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new List<IWallet>() });
        _starStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>> { Result = new List<ISTARODK>() });

        var result = await _manager.SearchAsync(new SearchRequest { Query = "neo", EntityTypes = SearchableEntityType.Avatar }, callerAvatarId: null);

        result.IsError.Should().BeFalse();
        result.Result!.Hits.Should().ContainSingle();
        result.Result!.Hits[0].EntityType.Should().Be(SearchableEntityType.Avatar);
        result.Result!.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task SearchAsync_ReturnsHitsFromHolons()
    {
        // Cross-tenant scoping: a holon is only searchable by its owner (or if public).
        var owner = Guid.NewGuid();
        var holon = new Holon { Id = Guid.NewGuid(), Name = "WorldHolon", Description = "A world", AssetType = "World", AvatarId = owner, CreatedDate = DateTime.UtcNow };
        _avatarStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IAvatar>> { Result = new List<IAvatar>() });
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon> { holon } });
        _walletStore.Setup(p => p.GetByAvatarAsync(owner, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new List<IWallet>() });
        _starStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>> { Result = new List<ISTARODK>() });

        var result = await _manager.SearchAsync(new SearchRequest { Query = "world", EntityTypes = SearchableEntityType.Holon }, callerAvatarId: owner);

        result.IsError.Should().BeFalse();
        result.Result!.Hits.Should().ContainSingle();
        result.Result!.Hits[0].EntityType.Should().Be(SearchableEntityType.Holon);
    }

    [Fact]
    public async Task SearchAsync_CaseInsensitiveQuery()
    {
        var avatar = new Avatar { Id = Guid.NewGuid(), Username = "Neo", Email = "neo@matrix.com", CreatedDate = DateTime.UtcNow };
        _avatarStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IAvatar>> { Result = new List<IAvatar> { avatar } });
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon>() });
        _walletStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new List<IWallet>() });
        _starStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>> { Result = new List<ISTARODK>() });

        var result = await _manager.SearchAsync(new SearchRequest { Query = "NEO", EntityTypes = SearchableEntityType.Avatar }, callerAvatarId: null);

        result.Result!.Hits.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAsync_PaginationWorks()
    {
        var avatars = Enumerable.Range(0, 10).Select(i => new Avatar
        {
            Id = Guid.NewGuid(),
            Username = $"user{i}",
            Email = $"user{i}@test.com",
            CreatedDate = DateTime.UtcNow
        }).Cast<IAvatar>().ToList();

        _avatarStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IAvatar>> { Result = avatars });
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon>() });
        _walletStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new List<IWallet>() });
        _starStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>> { Result = new List<ISTARODK>() });

        var result = await _manager.SearchAsync(new SearchRequest { Query = "user", EntityTypes = SearchableEntityType.Avatar, Page = 1, PageSize = 3 }, callerAvatarId: null);

        result.Result!.TotalCount.Should().Be(10);
        result.Result!.TotalPages.Should().Be(4);
        result.Result!.Hits.Should().HaveCount(3);
        result.Result!.PageSize.Should().Be(3);
    }

    [Fact]
    public async Task SearchAsync_EmptyQueryReturnsAllMatching()
    {
        var avatars = new List<IAvatar>
        {
            new Avatar { Id = Guid.NewGuid(), Username = "alice", Email = "a@x.com", CreatedDate = DateTime.UtcNow },
            new Avatar { Id = Guid.NewGuid(), Username = "bob", Email = "b@x.com", CreatedDate = DateTime.UtcNow }
        };

        _avatarStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IAvatar>> { Result = avatars });
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon>() });
        _walletStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new List<IWallet>() });
        _starStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>> { Result = new List<ISTARODK>() });

        var result = await _manager.SearchAsync(new SearchRequest { Query = "", EntityTypes = SearchableEntityType.Avatar }, callerAvatarId: null);

        result.Result!.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task SearchAsync_FiltersByAssetType()
    {
        var owner = Guid.NewGuid();
        var nftHolon = new Holon { Id = Guid.NewGuid(), Name = "NFT1", AssetType = "NFT", AvatarId = owner, CreatedDate = DateTime.UtcNow };
        var docHolon = new Holon { Id = Guid.NewGuid(), Name = "Doc1", AssetType = "Document", AvatarId = owner, CreatedDate = DateTime.UtcNow };
        _avatarStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IAvatar>> { Result = new List<IAvatar>() });
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon> { nftHolon, docHolon } });
        _walletStore.Setup(p => p.GetByAvatarAsync(owner, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new List<IWallet>() });
        _starStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>> { Result = new List<ISTARODK>() });

        var result = await _manager.SearchAsync(new SearchRequest { Query = "", EntityTypes = SearchableEntityType.Holon, AssetType = "NFT" }, callerAvatarId: owner);

        result.Result!.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task GetFacetsAsync_ReturnsCallerScopedEntityCounts()
    {
        // Facet counts are caller-scoped: Holon/STARODK = owner-or-public, Wallet = caller's own.
        var owner = Guid.NewGuid();
        _avatarStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IAvatar>> { Result = new List<IAvatar> { new Avatar() } });
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon> { new Holon { AvatarId = owner }, new Holon { AvatarId = owner } } });
        _walletStore.Setup(p => p.GetByAvatarAsync(owner, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new List<IWallet> { new Wallet() } });
        _starStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>> { Result = new List<ISTARODK>() });

        var result = await _manager.GetFacetsAsync(callerAvatarId: owner);

        result.Result.Should().HaveCount(4);
        result.Result!.First(f => f.EntityType == SearchableEntityType.Avatar).Count.Should().Be(1);
        result.Result!.First(f => f.EntityType == SearchableEntityType.Holon).Count.Should().Be(2);
    }
}
