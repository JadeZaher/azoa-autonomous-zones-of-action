using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests;

public class HolonManagerTests
{
    private readonly Mock<IHolonStore> _store;
    private readonly HolonManager _manager;

    public HolonManagerTests()
    {
        _store = new Mock<IHolonStore>();
        _manager = new HolonManager(_store.Object);
    }

    [Fact]
    public async Task CreateAsync_ShouldSetAvatarIdAndSave()
    {
        var holon = new Holon { Id = Guid.NewGuid(), Name = "Test Holon" };
        _store.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new AZOAResult<IHolon> { Result = h });

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
        _store.Setup(p => p.GetByIdAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = holon });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new AZOAResult<IHolon> { Result = h });

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
        _store.Setup(p => p.GetByIdAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = holon });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new AZOAResult<IHolon> { Result = h });

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
        _store.Setup(p => p.QueryAsync(query, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new List<IHolon>() });

        var result = await _manager.QueryAsync(query);

        result.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ShouldReturnProviderResult()
    {
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = new Holon { Id = Guid.NewGuid() } });
        _store.Setup(p => p.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<bool> { Result = true });

        var result = await _manager.DeleteAsync(Guid.NewGuid());

        result.Result.Should().BeTrue();
    }

    [Fact]
    public async Task GetChildrenAsync_ShouldReturnSubHolons()
    {
        var parentId = Guid.NewGuid();
        var child = new Holon { Id = Guid.NewGuid(), ParentHolonId = parentId, Name = "Child" };
        _store.Setup(p => p.QueryAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == parentId), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { child } });

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

        _store.Setup(p => p.GetByIdAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = holon });
        _store.Setup(p => p.QueryAsync(null, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { peer, new Holon { Id = Guid.NewGuid() } } });

        var result = await _manager.GetPeersAsync(holon.Id);

        result.IsError.Should().BeFalse();
        result.Result.Should().HaveCount(1);
        result.Result!.First().Name.Should().Be("Peer");
    }

    [Fact]
    public async Task GetPeersAsync_NoPeers_ReturnsEmpty()
    {
        var holon = new Holon { Id = Guid.NewGuid(), PeerHolonIds = new List<Guid>() };
        _store.Setup(p => p.GetByIdAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = holon });

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

        _store.Setup(p => p.GetByIdAsync(child.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = child });
        _store.Setup(p => p.GetByIdAsync(parent.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = parent });
        _store.Setup(p => p.GetByIdAsync(grandparent.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = grandparent });

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

        _store.Setup(p => p.GetByIdAsync(b.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = b });
        _store.Setup(p => p.GetByIdAsync(a.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = a });

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

        _store.Setup(p => p.QueryAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == root.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { child } });
        _store.Setup(p => p.QueryAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == child.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { grandchild } });
        _store.Setup(p => p.QueryAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == grandchild.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });

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

        _store.Setup(p => p.GetByIdAsync(root.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = root });
        _store.Setup(p => p.QueryAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == root.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { child } });
        _store.Setup(p => p.QueryAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == child.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { grandchild } });
        _store.Setup(p => p.QueryAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == grandchild.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });
        _store.Setup(p => p.GetByIdAsync(child.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = child });
        _store.Setup(p => p.GetByIdAsync(grandchild.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = grandchild });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new AZOAResult<IHolon> { Result = h });

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

        _store.Setup(p => p.GetByIdAsync(root.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = root });
        _store.Setup(p => p.QueryAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == root.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { child } });
        _store.Setup(p => p.QueryAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == child.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { grandchild } });
        _store.Setup(p => p.QueryAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == grandchild.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });

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
        _store.Setup(p => p.GetByIdAsync(original.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = original });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new AZOAResult<IHolon> { Result = h });

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

        _store.Setup(p => p.QueryAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == holon.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });
        _store.Setup(p => p.GetByIdAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IHolon> { Result = holon });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new AZOAResult<IHolon> { Result = h });

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

        _store.Setup(p => p.QueryAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == a.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = new[] { b } });
        _store.Setup(p => p.QueryAsync(It.Is<HolonQueryRequest>(q => q.ParentHolonId == b.Id), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });

        var result = await _manager.MoveSubtreeAsync(a.Id, b.Id);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Cannot move");
    }
}
