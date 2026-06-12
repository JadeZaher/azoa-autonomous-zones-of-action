using FluentAssertions;
using Moq;
using OASIS.WebAPI.Interfaces;
using OASIS.WebAPI.Interfaces.Stores;
using OASIS.WebAPI.Managers;
using OASIS.WebAPI.Models;
using OASIS.WebAPI.Models.Requests;
using OASIS.WebAPI.Models.Responses;

namespace OASIS.WebAPI.Tests.Managers;

public class HolonManagerExtendedTests
{
    private readonly Mock<IHolonStore> _store;
    private readonly HolonManager _manager;

    public HolonManagerExtendedTests()
    {
        _store = new Mock<IHolonStore>();
        _manager = new HolonManager(_store.Object);
    }

    [Fact]
    public async Task GetAsync_Existing_ReturnsHolon()
    {
        var holon = new Holon { Id = Guid.NewGuid(), Name = "Test" };
        _store.Setup(p => p.GetByIdAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = holon });

        var result = await _manager.GetAsync(holon.Id);

        result.Result!.Name.Should().Be("Test");
    }

    [Fact]
    public async Task GetAsync_NonExisting_ReturnsError()
    {
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { IsError = true, Message = "Not found" });

        var result = await _manager.GetAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task GetAllAsync_ReturnsList()
    {
        _store.Setup(p => p.QueryAsync((HolonQueryRequest?)null, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>>
                 {
                     Result = new[] { new Holon { Name = "A" }, new Holon { Name = "B" } }
                 });

        var result = await _manager.GetAllAsync();

        result.Result.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpdateAsync_NonExisting_ReturnsError()
    {
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { IsError = true, Message = "Not found" });

        var result = await _manager.UpdateAsync(Guid.NewGuid(), new HolonUpdateModel { Name = "New" });

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateAsync_ShouldOnlyUpdateProvidedFields()
    {
        var holon = new Holon
        {
            Id = Guid.NewGuid(),
            Name = "Keep",
            Description = "Old",
            Metadata = new Dictionary<string, string> { ["key"] = "val" },
            PeerHolonIds = new List<Guid>(),
            IsActive = true
        };
        _store.Setup(p => p.GetByIdAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = holon });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new OASISResult<IHolon> { Result = h });

        var result = await _manager.UpdateAsync(holon.Id, new HolonUpdateModel
        {
            Name = "New",
            Metadata = new Dictionary<string, string> { ["extra"] = "data" }
        });

        result.IsError.Should().BeFalse();
        result.Result!.Name.Should().Be("New");
        result.Result.Description.Should().Be("Old");
        result.Result.Metadata.Should().ContainKey("key");
        result.Result.Metadata.Should().ContainKey("extra");
    }

    [Fact]
    public async Task DeleteAsync_ReturnsProviderResult()
    {
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = new Holon { Id = Guid.NewGuid() } });
        _store.Setup(p => p.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<bool> { Result = true });

        var result = await _manager.DeleteAsync(Guid.NewGuid());

        result.Result.Should().BeTrue();
    }

    [Fact]
    public async Task InteractAsync_NonExisting_ReturnsError()
    {
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { IsError = true, Message = "Not found" });

        var result = await _manager.InteractAsync(Guid.NewGuid(), new HolonInteractionRequest());

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task InteractAsync_ShouldChangeParent()
    {
        var holon = new Holon { Id = Guid.NewGuid(), ParentHolonId = null };
        var newParent = Guid.NewGuid();
        _store.Setup(p => p.GetByIdAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = holon });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new OASISResult<IHolon> { Result = h });

        var result = await _manager.InteractAsync(holon.Id, new HolonInteractionRequest { NewParentHolonId = newParent });

        result.Result!.ParentHolonId.Should().Be(newParent);
    }

    [Fact]
    public async Task InteractAsync_ShouldRemovePeers()
    {
        var peer = Guid.NewGuid();
        var holon = new Holon { Id = Guid.NewGuid(), PeerHolonIds = new List<Guid> { peer } };
        _store.Setup(p => p.GetByIdAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = holon });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new OASISResult<IHolon> { Result = h });

        var result = await _manager.InteractAsync(holon.Id, new HolonInteractionRequest { RemovePeerHolonIds = new List<Guid> { peer } });

        result.Result!.PeerHolonIds.Should().BeEmpty();
    }

    [Fact]
    public async Task InteractAsync_ShouldRemoveMetadataKeys()
    {
        var holon = new Holon { Id = Guid.NewGuid(), Metadata = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" } };
        _store.Setup(p => p.GetByIdAsync(holon.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IHolon> { Result = holon });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IHolon h, CancellationToken _) => new OASISResult<IHolon> { Result = h });

        var result = await _manager.InteractAsync(holon.Id, new HolonInteractionRequest { RemoveMetadataKeys = new List<string> { "a" } });

        result.Result!.Metadata.Should().NotContainKey("a");
        result.Result.Metadata.Should().ContainKey("b");
    }

    [Fact]
    public async Task QueryAsync_WithNoFilters_ShouldReturnAll()
    {
        _store.Setup(p => p.QueryAsync(It.IsAny<HolonQueryRequest>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new OASISResult<IEnumerable<IHolon>> { Result = new[] { new Holon(), new Holon() } });

        var result = await _manager.QueryAsync(new HolonQueryRequest());

        result.Result.Should().HaveCount(2);
    }
}
