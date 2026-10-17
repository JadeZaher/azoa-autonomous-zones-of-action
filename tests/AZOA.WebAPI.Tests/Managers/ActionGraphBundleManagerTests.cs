using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Models.Deployments;

namespace AZOA.WebAPI.Tests.Managers;

public class ActionGraphBundleManagerTests
{
    private readonly Mock<IActionGraphBundleStore> _store;
    private readonly Mock<IEcosystemStore> _ecosystemStore;
    private readonly Mock<IDappSeriesStore> _dappSeriesStore;
    private readonly ActionGraphBundleManager _manager;
    private readonly Mock<IDeploymentService> _deployments = new();

    public ActionGraphBundleManagerTests()
    {
        _store = new Mock<IActionGraphBundleStore>();
        _ecosystemStore = new Mock<IEcosystemStore>();
        _dappSeriesStore = new Mock<IDappSeriesStore>();
        _manager = new ActionGraphBundleManager(_store.Object, _ecosystemStore.Object, _dappSeriesStore.Object, _deployments.Object);
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
    public async Task CreateOrUpdateAsync_PUT_DifferentUser_PrivateRecord_ReturnsNotFoundMarker()
    {
        // PUT IDOR closure: a private record owned by a different user reads as NotFound (no existence oracle).
        var ownerUser = Guid.NewGuid();
        var attackerUser = Guid.NewGuid();
        var existing = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "Owned", UserId = ownerUser };
        _store.Setup(p => p.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = existing });

        var model = new ActionGraphBundleCreateModel { Name = "Hijacked" };
        var result = await _manager.CreateOrUpdateAsync(model, attackerUser, routeId: existing.Id);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(ActionGraphBundleAuthorizationError.NotFound);
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
    public async Task DeleteAsync_ByOwner_DelegatesToProvider_AndANullCallerIsRefused()
    {
        var owner = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "Owned", UserId = owner };
        _store.Setup(p => p.GetByIdAsync(bundle.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = bundle });
        _store.Setup(p => p.DeleteAsync(bundle.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<bool> { Result = true });

        (await _manager.DeleteAsync(bundle.Id, userId: null)).IsError.Should().BeTrue("a delete without an acting user fails closed");
        (await _manager.DeleteAsync(bundle.Id, owner)).Result.Should().BeTrue();
    }

    [Fact]
    public async Task GenerateAsync_DelegatesExplicitArtifactAndCannotInventCode()
    {
        var owner = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "Test", UserId = owner };
        _store.Setup(p => p.GetByIdAsync(bundle.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = bundle });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IActionGraphBundle>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IActionGraphBundle s, CancellationToken _) => new AZOAResult<IActionGraphBundle> { Result = s });

        var generation = new DeploymentGenerateRequest(new("PlatformRuntime", "Solana", "Devnet"), [Guid.NewGuid()]);
        var request = new ActionGraphBundleDappGenerationRequest { Generation = generation, BoundHolonIds = new List<Guid> { Guid.NewGuid() } };
        _deployments.Setup(service => service.GenerateAsync(bundle.Id, owner, null, generation, default))
            .ReturnsAsync(AZOAResult<DeploymentArtifactView>.Failure("Published action revision is unavailable."));
        var result = await _manager.GenerateAsync(bundle.Id, request, owner);

        result.IsError.Should().BeTrue();
        bundle.GeneratedCode.Should().BeNullOrEmpty();
        _deployments.Verify(service => service.GenerateAsync(bundle.Id, owner, null, generation, default), Times.Once);
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
    public async Task DeployAsync_ReturnsPendingReceiptWithoutInventingTransaction()
    {
        var owner = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "Test", GeneratedCode = "code", TargetChain = "Algorand", UserId = owner,
            DeploymentConfig = "{\"formatVersion\":2,\"artifactId\":\"artifact\"}" };
        _store.Setup(p => p.GetByIdAsync(bundle.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = bundle });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IActionGraphBundle>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((IActionGraphBundle s, CancellationToken _) => new AZOAResult<IActionGraphBundle> { Result = s });

        var request = Start();
        var receipt = new DeploymentReceipt("operation", "artifact", "hash", request.Target, "Prepared", "Execute", null, null, [], null, 1);
        _deployments.Setup(service => service.StartAsync(owner, null, "key", request, default)).ReturnsAsync(AZOAResult<DeploymentReceipt>.Success(receipt));
        var result = await _manager.DeployAsync(bundle.Id, request, "key", owner);

        result.IsError.Should().BeFalse();
        result.Result.Should().Be(receipt);
        result.Result!.TransactionIds.Should().BeEmpty();
        _store.Verify(store => store.UpsertAsync(It.IsAny<IActionGraphBundle>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeployAsync_WithoutGeneration_ShouldReturnError()
    {
        var owner = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "Test", UserId = owner };
        _store.Setup(p => p.GetByIdAsync(bundle.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = bundle });

        var result = await _manager.DeployAsync(bundle.Id, Start(), "key", owner);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Generate");
    }

    [Fact]
    public async Task DeployAsync_NonExisting_ShouldReturnError()
    {
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IActionGraphBundle> { IsError = true, Message = "Not found" });

        var result = await _manager.DeployAsync(Guid.NewGuid(), Start(), "key", Guid.NewGuid());

        result.IsError.Should().BeTrue();
    }

    private static DeploymentStartRequest Start() => new("artifact", "hash", new("PlatformRuntime", "Algorand", "Devnet"));
}
