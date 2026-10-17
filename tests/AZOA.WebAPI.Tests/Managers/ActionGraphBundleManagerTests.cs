using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Managers;

public class ActionGraphBundleManagerTests
{
    private readonly Mock<IActionGraphBundleStore> _store;
    private readonly Mock<IEcosystemStore> _ecosystemStore;
    private readonly Mock<IDappSeriesStore> _dappSeriesStore;
    private readonly ActionGraphBundleManager _manager;

    public ActionGraphBundleManagerTests()
    {
        _store = new Mock<IActionGraphBundleStore>();
        _ecosystemStore = new Mock<IEcosystemStore>();
        _dappSeriesStore = new Mock<IDappSeriesStore>();
        _manager = new ActionGraphBundleManager(_store.Object, _ecosystemStore.Object, _dappSeriesStore.Object);
    }

    [Fact]
    public async Task GetAsync_Existing_ReturnsBundle()
    {
        var owner = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "Test", UserId = owner };
        _store.Setup(p => p.GetByIdAsync(bundle.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = bundle });

        // Owner-or-public read scope: owner reads their own action graph bundle.
        var result = await _manager.GetAsync(bundle.Id, owner);

        result.Result.Should().NotBeNull();
        result.Result!.Name.Should().Be("Test");
    }

    [Fact]
    public async Task GetAsync_NonExisting_ReturnsError()
    {
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { IsError = true, Message = "Not found" });

        var result = await _manager.GetAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task GetAllAsync_ReturnsList()
    {
        var owner = Guid.NewGuid();
        _store.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<IActionGraphBundle>>
                 {
                     Result = new[] { new ActionGraphBundle { Name = "A", UserId = owner }, new ActionGraphBundle { Name = "B", UserId = owner } }
                 });

        var result = await _manager.GetAllAsync(owner);

        result.Result.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_New_ShouldCreate()
    {
        var userId = Guid.NewGuid();
        _store.Setup(p => p.GetByNameAndUserAsync("Genesis", userId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = null });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IActionGraphBundle>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IActionGraphBundle s, CancellationToken _) => new AZOAResult<IActionGraphBundle> { Result = s });

        var model = new ActionGraphBundleCreateModel { Name = "Genesis", Description = "Desc" };
        var result = await _manager.CreateOrUpdateAsync(model, userId);

        result.IsError.Should().BeFalse();
        result.Result!.Name.Should().Be("Genesis");
        result.Result.Description.Should().Be("Desc");
        result.Result.UserId.Should().Be(userId, "manager always stamps the authenticated user");
    }

    [Fact]
    public async Task CreateOrUpdateAsync_Existing_OwnedByCaller_ShouldUpdate()
    {
        var userId = Guid.NewGuid();
        var existing = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "Existing", Description = "Old", UserId = userId };
        _store.Setup(p => p.GetByNameAndUserAsync("Existing", userId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = existing });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IActionGraphBundle>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IActionGraphBundle s, CancellationToken _) => new AZOAResult<IActionGraphBundle> { Result = s });

        var model = new ActionGraphBundleCreateModel { Name = "Existing", Description = "New" };
        var result = await _manager.CreateOrUpdateAsync(model, userId);

        result.IsError.Should().BeFalse();
        result.Result!.Description.Should().Be("New");
    }

    [Fact]
    public async Task CreateOrUpdateAsync_POST_DifferentUserSameName_DoesNotLoadOtherRecord()
    {
        // POST IDOR closure: User B sending Name="A's record" must not find
        // A's record via the name lookup -- the store query is scoped by userId.
        var userB = Guid.NewGuid();
        _store.Setup(p => p.GetByNameAndUserAsync("Shared", userB, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = null });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IActionGraphBundle>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IActionGraphBundle s, CancellationToken _) => new AZOAResult<IActionGraphBundle> { Result = s });

        var model = new ActionGraphBundleCreateModel { Name = "Shared", Description = "B's" };
        var result = await _manager.CreateOrUpdateAsync(model, userB);

        result.IsError.Should().BeFalse();
        result.Result!.UserId.Should().Be(userB);
        // GetByIdAsync was NEVER invoked -- the loaded record could only have come from name+user lookup.
        _store.Verify(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_PUT_DifferentUser_ReturnsForbiddenMarker()
    {
        // PUT IDOR closure: route id resolves to a record owned by a different user -> Forbidden.
        var ownerUser = Guid.NewGuid();
        var attackerUser = Guid.NewGuid();
        var existing = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "Owned", UserId = ownerUser };
        _store.Setup(p => p.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = existing });

        var model = new ActionGraphBundleCreateModel { Name = "Hijacked" };
        var result = await _manager.CreateOrUpdateAsync(model, attackerUser, routeId: existing.Id);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(ActionGraphBundleAuthorizationError.Forbidden);
        // Crucially -- no upsert was performed.
        _store.Verify(p => p.UpsertAsync(It.IsAny<IActionGraphBundle>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_PUT_OwnRecord_Updates()
    {
        var userId = Guid.NewGuid();
        var existing = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "Owned", UserId = userId, Description = "Old" };
        _store.Setup(p => p.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = existing });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IActionGraphBundle>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IActionGraphBundle s, CancellationToken _) => new AZOAResult<IActionGraphBundle> { Result = s });

        var model = new ActionGraphBundleCreateModel { Name = "Owned", Description = "New" };
        var result = await _manager.CreateOrUpdateAsync(model, userId, routeId: existing.Id);

        result.IsError.Should().BeFalse();
        result.Result!.Description.Should().Be("New");
        result.Result.UserId.Should().Be(userId);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_PUT_UnknownId_ReturnsNotFoundMarker()
    {
        var userId = Guid.NewGuid();
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { IsError = true, Result = null, Message = "Not found." });

        var model = new ActionGraphBundleCreateModel { Name = "X" };
        var result = await _manager.CreateOrUpdateAsync(model, userId, routeId: Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(ActionGraphBundleAuthorizationError.NotFound);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_IgnoresCallerSuppliedUserId()
    {
        // Defence in depth: even if the caller pushes a forged UserId in the body,
        // the manager stamps the authenticated user onto the persisted record.
        var authenticated = Guid.NewGuid();
        var forged        = Guid.NewGuid();
        _store.Setup(p => p.GetByNameAndUserAsync(It.IsAny<string>(), authenticated, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = null });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IActionGraphBundle>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IActionGraphBundle s, CancellationToken _) => new AZOAResult<IActionGraphBundle> { Result = s });

        var model = new ActionGraphBundleCreateModel { Name = "Foo", UserId = forged };
        var result = await _manager.CreateOrUpdateAsync(model, authenticated);

        result.Result!.UserId.Should().Be(authenticated);
    }

    [Fact]
    public async Task DeleteAsync_ShouldDelegateToProvider()
    {
        _store.Setup(p => p.DeleteAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<bool> { Result = true });

        var result = await _manager.DeleteAsync(Guid.NewGuid());

        result.Result.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_ShouldSetGeneratedCode()
    {
        var owner = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "Test", UserId = owner };
        _store.Setup(p => p.GetByIdAsync(bundle.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = bundle });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IActionGraphBundle>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IActionGraphBundle s, CancellationToken _) => new AZOAResult<IActionGraphBundle> { Result = s });

        var request = new ActionGraphBundleDappGenerationRequest { TargetChain = "Solana", BoundHolonIds = new List<Guid> { Guid.NewGuid() } };
        var result = await _manager.GenerateAsync(bundle.Id, request, owner);

        result.IsError.Should().BeFalse();
        result.Result!.GeneratedCode.Should().NotBeNullOrEmpty();
        result.Result.TargetChain.Should().Be("Solana");
    }

    [Fact]
    public async Task GenerateAsync_NonExisting_ShouldReturnError()
    {
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { IsError = true, Message = "Not found" });

        var result = await _manager.GenerateAsync(Guid.NewGuid(), new ActionGraphBundleDappGenerationRequest());

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task DeployAsync_ShouldSetDeploymentConfig()
    {
        var owner = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "Test", GeneratedCode = "code", TargetChain = "Algorand", UserId = owner };
        _store.Setup(p => p.GetByIdAsync(bundle.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = bundle });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IActionGraphBundle>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IActionGraphBundle s, CancellationToken _) => new AZOAResult<IActionGraphBundle> { Result = s });

        var result = await _manager.DeployAsync(bundle.Id, owner);

        result.IsError.Should().BeFalse();
        result.Result!.DeploymentConfig.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DeployAsync_WithoutGeneration_ShouldReturnError()
    {
        var owner = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "Test", UserId = owner };
        _store.Setup(p => p.GetByIdAsync(bundle.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = bundle });

        var result = await _manager.DeployAsync(bundle.Id, owner);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("must be generated");
    }

    [Fact]
    public async Task DeployAsync_NonExisting_ShouldReturnError()
    {
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { IsError = true, Message = "Not found" });

        var result = await _manager.DeployAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
    }
}
