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
    private readonly Mock<IUserStore> _userStore;
    private readonly Mock<IHolonStore> _holonStore;
    private readonly Mock<IWalletStore> _walletStore;
    private readonly Mock<IBlockchainOperationStore> _blockchainOperationStore;
    private readonly Mock<ISTARStore> _starStore;
    private readonly SearchManager _manager;

    public SearchManagerTests()
    {
        _userStore = new Mock<IUserStore>();
        _holonStore = new Mock<IHolonStore>();
        _walletStore = new Mock<IWalletStore>();
        _blockchainOperationStore = new Mock<IBlockchainOperationStore>();
        _starStore = new Mock<ISTARStore>();
        _manager = new SearchManager(
            _userStore.Object,
            _holonStore.Object,
            _walletStore.Object,
            _blockchainOperationStore.Object,
            _starStore.Object);
    }

    [Fact]
    public async Task SearchAsync_ReturnsHitsFromUsers()
    {
        var user = new User { Id = Guid.NewGuid(), Username = "neo", Email = "neo@matrix.com", CreatedDate = DateTime.UtcNow };
        _userStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IUser>> { Result = new List<IUser> { user } });
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon>() });
        _walletStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new List<IWallet>() });
        _starStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>> { Result = new List<ISTARODK>() });

        var result = await _manager.SearchAsync(new SearchRequest { Query = "neo", EntityTypes = SearchableEntityType.User }, callerUserId: null);

        result.IsError.Should().BeFalse();
        result.Result!.Hits.Should().ContainSingle();
        result.Result!.Hits[0].EntityType.Should().Be(SearchableEntityType.User);
        result.Result!.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task SearchAsync_ReturnsHitsFromHolons()
    {
        // Cross-tenant scoping: a holon is only searchable by its owner (or if public).
        var owner = Guid.NewGuid();
        var holon = new Holon { Id = Guid.NewGuid(), Name = "WorldHolon", Description = "A world", AssetType = "World", UserId = owner, CreatedDate = DateTime.UtcNow };
        _userStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IUser>> { Result = new List<IUser>() });
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon> { holon } });
        _walletStore.Setup(p => p.GetByUserAsync(owner, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new List<IWallet>() });
        _starStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>> { Result = new List<ISTARODK>() });

        var result = await _manager.SearchAsync(new SearchRequest { Query = "world", EntityTypes = SearchableEntityType.Holon }, callerUserId: owner);

        result.IsError.Should().BeFalse();
        result.Result!.Hits.Should().ContainSingle();
        result.Result!.Hits[0].EntityType.Should().Be(SearchableEntityType.Holon);
    }

    [Fact]
    public async Task SearchAsync_CaseInsensitiveQuery()
    {
        var user = new User { Id = Guid.NewGuid(), Username = "Neo", Email = "neo@matrix.com", CreatedDate = DateTime.UtcNow };
        _userStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IUser>> { Result = new List<IUser> { user } });
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon>() });
        _walletStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new List<IWallet>() });
        _starStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>> { Result = new List<ISTARODK>() });

        var result = await _manager.SearchAsync(new SearchRequest { Query = "NEO", EntityTypes = SearchableEntityType.User }, callerUserId: null);

        result.Result!.Hits.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAsync_PaginationWorks()
    {
        var users = Enumerable.Range(0, 10).Select(i => new User
        {
            Id = Guid.NewGuid(),
            Username = $"user{i}",
            Email = $"user{i}@test.com",
            CreatedDate = DateTime.UtcNow
        }).Cast<IUser>().ToList();

        _userStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IUser>> { Result = users });
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon>() });
        _walletStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new List<IWallet>() });
        _starStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>> { Result = new List<ISTARODK>() });

        var result = await _manager.SearchAsync(new SearchRequest { Query = "user", EntityTypes = SearchableEntityType.User, Page = 1, PageSize = 3 }, callerUserId: null);

        result.Result!.TotalCount.Should().Be(10);
        result.Result!.TotalPages.Should().Be(4);
        result.Result!.Hits.Should().HaveCount(3);
        result.Result!.PageSize.Should().Be(3);
    }

    [Fact]
    public async Task SearchAsync_EmptyQueryReturnsAllMatching()
    {
        var users = new List<IUser>
        {
            new User { Id = Guid.NewGuid(), Username = "alice", Email = "a@x.com", CreatedDate = DateTime.UtcNow },
            new User { Id = Guid.NewGuid(), Username = "bob", Email = "b@x.com", CreatedDate = DateTime.UtcNow }
        };

        _userStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IUser>> { Result = users });
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon>() });
        _walletStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new List<IWallet>() });
        _starStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>> { Result = new List<ISTARODK>() });

        var result = await _manager.SearchAsync(new SearchRequest { Query = "", EntityTypes = SearchableEntityType.User }, callerUserId: null);

        result.Result!.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task SearchAsync_FiltersByAssetType()
    {
        var owner = Guid.NewGuid();
        var nftHolon = new Holon { Id = Guid.NewGuid(), Name = "NFT1", AssetType = "NFT", UserId = owner, CreatedDate = DateTime.UtcNow };
        var docHolon = new Holon { Id = Guid.NewGuid(), Name = "Doc1", AssetType = "Document", UserId = owner, CreatedDate = DateTime.UtcNow };
        _userStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IUser>> { Result = new List<IUser>() });
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon> { nftHolon, docHolon } });
        _walletStore.Setup(p => p.GetByUserAsync(owner, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new List<IWallet>() });
        _starStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>> { Result = new List<ISTARODK>() });

        var result = await _manager.SearchAsync(new SearchRequest { Query = "", EntityTypes = SearchableEntityType.Holon, AssetType = "NFT" }, callerUserId: owner);

        result.Result!.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task GetFacetsAsync_ReturnsCallerScopedEntityCounts()
    {
        // Facet counts are caller-scoped: Holon/STARODK = owner-or-public, Wallet = caller's own.
        var owner = Guid.NewGuid();
        _userStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IUser>> { Result = new List<IUser> { new User() } });
        _holonStore.Setup(p => p.QueryAsync(null, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon> { new Holon { UserId = owner }, new Holon { UserId = owner } } });
        _walletStore.Setup(p => p.GetByUserAsync(owner, default))
            .ReturnsAsync(new AZOAResult<IEnumerable<IWallet>> { Result = new List<IWallet> { new Wallet() } });
        _starStore.Setup(p => p.GetAllAsync(default))
            .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>> { Result = new List<ISTARODK>() });

        var result = await _manager.GetFacetsAsync(callerUserId: owner);

        result.Result.Should().HaveCount(4);
        result.Result!.First(f => f.EntityType == SearchableEntityType.User).Count.Should().Be(1);
        result.Result!.First(f => f.EntityType == SearchableEntityType.Holon).Count.Should().Be(2);
    }
}
