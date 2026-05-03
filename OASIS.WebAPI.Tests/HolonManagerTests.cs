using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Moq;
using OASIS.WebAPI.Core;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests;

public class HolonManagerTests
{
    private readonly Mock<IOASISStorageProvider> _provider;
    private readonly ProviderContext _providerContext;
    private readonly HolonManager _manager;

    public HolonManagerTests()
    {
        _provider = new Mock<IOASISStorageProvider>();
        _provider.Setup(p => p.ProviderName).Returns("InMemory");

        var config = new ConfigurationBuilder().Build();
        _providerContext = new ProviderContext(new[] { _provider.Object }, config, null);
        _manager = new HolonManager(_providerContext);
    }

    [Fact]
    public async Task CreateAsync_ShouldSetAvatarIdAndSave()
    {
        var holon = new Holon { Id = Guid.NewGuid(), Name = "Test Holon" };
        _provider.Setup(p => p.SaveHolonAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new OASISResult<IHolon> { Result = h });

        var model = new HolonCreateModel { Name = "Test Holon", ProviderName = "InMemory" };
        var avatarId = Guid.NewGuid();

        var result = await _manager.CreateAsync(model, avatarId);

        result.IsError.Should().BeFalse();
        result.Result!.AvatarId.Should().Be(avatarId);
        result.Result.Name.Should().Be("Test Holon");
    }

    [Fact]
    public async Task UpdateAsync_ShouldApplyPartialChanges()
    {
        var holon = new Holon { Id = Guid.NewGuid(), Name = "Old", Description = "Old Desc", IsActive = true };
        _provider.Setup(p => p.LoadHolonAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = holon });
        _provider.Setup(p => p.SaveHolonAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new OASISResult<IHolon> { Result = h });

        var model = new HolonUpdateModel { Name = "New" };
        var result = await _manager.UpdateAsync(holon.Id, model);

        result.IsError.Should().BeFalse();
        result.Result!.Name.Should().Be("New");
        result.Result.Description.Should().Be("Old Desc");
    }

    [Fact]
    public async Task InteractAsync_ShouldModifyPeersAndMetadata()
    {
        var holon = new Holon
        {
            Id = Guid.NewGuid(),
            PeerHolonIds = new List<Guid>(),
            Metadata = new Dictionary<string, string>()
        };
        _provider.Setup(p => p.LoadHolonAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = holon });
        _provider.Setup(p => p.SaveHolonAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new OASISResult<IHolon> { Result = h });

        var request = new HolonInteractionRequest
        {
            AddPeerHolonIds = new List<Guid> { Guid.NewGuid() },
            SetMetadata = new Dictionary<string, string> { ["key"] = "value" }
        };

        var result = await _manager.InteractAsync(holon.Id, request);

        result.IsError.Should().BeFalse();
        result.Result!.PeerHolonIds.Should().HaveCount(1);
        result.Result.Metadata.Should().ContainKey("key");
    }

    [Fact]
    public async Task QueryAsync_WithFilters_ShouldPassQueryToProvider()
    {
        var query = new HolonQueryRequest { Name = "Test", IsActive = true };
        _provider.Setup(p => p.LoadAllHolonsAsync(query, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new List<IHolon>() });

        var result = await _manager.QueryAsync(query);

        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnProviderResult()
    {
        _provider.Setup(p => p.DeleteHolonAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<bool> { Result = true });

        var result = await _manager.DeleteAsync(Guid.NewGuid());

        result.Result.Should().BeTrue();
    }

    [Fact]
    public async Task GetChildrenAsync_ShouldReturnSubHolons()
    {
        var parentId = Guid.NewGuid();
        var child = new Holon { Id = Guid.NewGuid(), ParentHolonId = parentId, Name = "Child" };
        _provider.Setup(p => p.LoadAllHolonsAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == parentId), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new[] { child } });

        var result = await _manager.GetChildrenAsync(parentId);

        result.IsError.Should().BeFalse();
        result.Result.Should().HaveCount(1);
        result.Result!.First().Name.Should().Be("Child");
    }

    [Fact]
    public async Task GetPeersAsync_ShouldReturnLinkedPeers()
    {
        var peerId = Guid.NewGuid();
        var holon = new Holon { Id = Guid.NewGuid(), PeerHolonIds = new List<Guid> { peerId } };
        var peer = new Holon { Id = peerId, Name = "Peer" };

        _provider.Setup(p => p.LoadHolonAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = holon });
        _provider.Setup(p => p.LoadAllHolonsAsync(null, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new[] { peer, new Holon { Id = Guid.NewGuid() } } });

        var result = await _manager.GetPeersAsync(holon.Id);

        result.IsError.Should().BeFalse();
        result.Result.Should().HaveCount(1);
        result.Result!.First().Name.Should().Be("Peer");
    }

    [Fact]
    public async Task GetPeersAsync_NoPeers_ReturnsEmpty()
    {
        var holon = new Holon { Id = Guid.NewGuid(), PeerHolonIds = new List<Guid>() };
        _provider.Setup(p => p.LoadHolonAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = holon });

        var result = await _manager.GetPeersAsync(holon.Id);

        result.IsError.Should().BeFalse();
        result.Result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAncestorsAsync_ShouldWalkParentChain()
    {
        var grandparent = new Holon { Id = Guid.NewGuid(), Name = "Grandparent", ParentHolonId = null };
        var parent = new Holon { Id = Guid.NewGuid(), Name = "Parent", ParentHolonId = grandparent.Id };
        var child = new Holon { Id = Guid.NewGuid(), Name = "Child", ParentHolonId = parent.Id };

        _provider.Setup(p => p.LoadHolonAsync(child.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = child });
        _provider.Setup(p => p.LoadHolonAsync(parent.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = parent });
        _provider.Setup(p => p.LoadHolonAsync(grandparent.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = grandparent });

        var result = await _manager.GetAncestorsAsync(child.Id);

        result.IsError.Should().BeFalse();
        result.Result.Should().HaveCount(2);
        result.Result!.Select(h => h.Name).Should().ContainInOrder("Parent", "Grandparent");
    }

    [Fact]
    public async Task GetAncestorsAsync_CycleGuard_StopsAtLoop()
    {
        var a = new Holon { Id = Guid.NewGuid(), Name = "A" };
        var b = new Holon { Id = Guid.NewGuid(), Name = "B", ParentHolonId = a.Id };
        a.ParentHolonId = b.Id; // cycle

        _provider.Setup(p => p.LoadHolonAsync(b.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = b });
        _provider.Setup(p => p.LoadHolonAsync(a.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = a });

        var result = await _manager.GetAncestorsAsync(b.Id);

        result.IsError.Should().BeFalse();
        result.Result.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetDescendantsAsync_ShouldTraverseSubtree()
    {
        var root = new Holon { Id = Guid.NewGuid(), Name = "Root" };
        var child = new Holon { Id = Guid.NewGuid(), Name = "Child", ParentHolonId = root.Id };
        var grandchild = new Holon { Id = Guid.NewGuid(), Name = "Grandchild", ParentHolonId = child.Id };

        _provider.Setup(p => p.LoadAllHolonsAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == root.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new[] { child } });
        _provider.Setup(p => p.LoadAllHolonsAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == child.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new[] { grandchild } });
        _provider.Setup(p => p.LoadAllHolonsAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == grandchild.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });

        var result = await _manager.GetDescendantsAsync(root.Id);

        result.IsError.Should().BeFalse();
        result.Result.Should().HaveCount(2);
        result.Result!.Select(h => h.Name).Should().Contain("Child", "Grandchild");
    }

    // ─── Holonic functionality tests ───

    [Fact]
    public async Task PropagateAsync_ShouldSetIsActiveOnSubtree()
    {
        var root = new Holon { Id = Guid.NewGuid(), Name = "Root", IsActive = true };
        var child = new Holon { Id = Guid.NewGuid(), Name = "Child", ParentHolonId = root.Id, IsActive = true };
        var grandchild = new Holon { Id = Guid.NewGuid(), Name = "Grandchild", ParentHolonId = child.Id, IsActive = true };

        _provider.Setup(p => p.LoadHolonAsync(root.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = root });
        _provider.Setup(p => p.LoadAllHolonsAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == root.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new[] { child } });
        _provider.Setup(p => p.LoadAllHolonsAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == child.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new[] { grandchild } });
        _provider.Setup(p => p.LoadAllHolonsAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == grandchild.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });
        _provider.Setup(p => p.LoadHolonAsync(child.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = child });
        _provider.Setup(p => p.LoadHolonAsync(grandchild.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = grandchild });
        _provider.Setup(p => p.SaveHolonAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new OASISResult<IHolon> { Result = h });

        var request = new HolonPropagateRequest { Property = "IsActive", Value = false, IncludeSelf = true };
        var result = await _manager.PropagateAsync(root.Id, request);

        result.IsError.Should().BeFalse();
        result.Result.Should().Be(3);
        root.IsActive.Should().BeFalse();
        child.IsActive.Should().BeFalse();
        grandchild.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task ComposeAsync_ShouldComputeSubtreeStats()
    {
        var root = new Holon { Id = Guid.NewGuid(), Name = "Album", AssetType = "Collection", IsActive = true, CreatedDate = DateTime.UtcNow.AddDays(-2) };
        var child = new Holon { Id = Guid.NewGuid(), Name = "Track1", AssetType = "NFT", ChainId = "algo", IsActive = true, CreatedDate = DateTime.UtcNow.AddDays(-1), Metadata = new Dictionary<string, string> { ["genre"] = "ambient" } };
        var grandchild = new Holon { Id = Guid.NewGuid(), Name = "Stem1", AssetType = "Audio", ChainId = "algo", IsActive = true, CreatedDate = DateTime.UtcNow, Metadata = new Dictionary<string, string> { ["genre"] = "ambient" } };

        _provider.Setup(p => p.LoadHolonAsync(root.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = root });
        _provider.Setup(p => p.LoadAllHolonsAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == root.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new[] { child } });
        _provider.Setup(p => p.LoadAllHolonsAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == child.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new[] { grandchild } });
        _provider.Setup(p => p.LoadAllHolonsAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == grandchild.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });

        var result = await _manager.ComposeAsync(root.Id);

        result.IsError.Should().BeFalse();
        result.Result.Should().NotBeNull();
        result.Result!.SourceHolonId.Should().Be(root.Id);
        result.Result.ChildCount.Should().Be(1);
        result.Result.TotalDescendantCount.Should().Be(2);
        result.Result.Depth.Should().Be(2);
        result.Result.AssetTypes.Should().Contain("NFT", "Audio");
        result.Result.ChainIds.Should().ContainSingle("algo");
        result.Result.MetadataKeyFrequency.Should().ContainKey("genre");
        result.Result.AllActive.Should().BeTrue();
    }

    [Fact]
    public async Task CloneAsync_ShouldCreateCopyWithoutSubtree()
    {
        var original = new Holon { Id = Guid.NewGuid(), Name = "Original", Description = "Desc", IsActive = true, Metadata = new Dictionary<string, string>(), PeerHolonIds = new List<Guid>() };
        _provider.Setup(p => p.LoadHolonAsync(original.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = original });
        _provider.Setup(p => p.SaveHolonAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new OASISResult<IHolon> { Result = h });

        var request = new HolonCloneRequest { IncludeSubtree = false };
        var result = await _manager.CloneAsync(original.Id, request, Guid.NewGuid());

        result.IsError.Should().BeFalse();
        result.Result!.Name.Should().Be("Original (Copy)");
        result.Result.Id.Should().NotBe(original.Id);
        result.Result.Metadata.Should().ContainKey("cloned_from");
    }

    [Fact]
    public async Task MoveSubtreeAsync_ShouldUpdateParent()
    {
        var holon = new Holon { Id = Guid.NewGuid(), Name = "Moving", ParentHolonId = Guid.NewGuid() };
        var newParent = Guid.NewGuid();

        _provider.Setup(p => p.LoadAllHolonsAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == holon.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });
        _provider.Setup(p => p.LoadHolonAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = holon });
        _provider.Setup(p => p.SaveHolonAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new OASISResult<IHolon> { Result = h });

        var result = await _manager.MoveSubtreeAsync(holon.Id, newParent);

        result.IsError.Should().BeFalse();
        result.Result.Should().BeTrue();
        holon.ParentHolonId.Should().Be(newParent);
    }

    [Fact]
    public async Task MoveSubtreeAsync_CyclePrevention_ReturnsError()
    {
        var a = new Holon { Id = Guid.NewGuid(), Name = "A" };
        var b = new Holon { Id = Guid.NewGuid(), Name = "B", ParentHolonId = a.Id };

        _provider.Setup(p => p.LoadAllHolonsAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == a.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new[] { b } });
        _provider.Setup(p => p.LoadAllHolonsAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == b.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });

        var result = await _manager.MoveSubtreeAsync(a.Id, b.Id);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Cannot move");
    }
}
