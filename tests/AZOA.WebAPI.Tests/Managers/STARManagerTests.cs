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

public class STARManagerTests
{
    private readonly Mock<ISTARStore> _store;
    private readonly STARManager _manager;

    public STARManagerTests()
    {
        _store = new Mock<ISTARStore>();
        _manager = new STARManager(_store.Object);
    }

    [Fact]
    public async Task GetAsync_Existing_ReturnsODK()
    {
        var odk = new STARODK { Id = Guid.NewGuid(), Name = "Test" };
        _store.Setup(p => p.GetByIdAsync(odk.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<ISTARODK> { Result = odk });

        var result = await _manager.GetAsync(odk.Id);

        result.Result.Should().NotBeNull();
        result.Result!.Name.Should().Be("Test");
    }

    [Fact]
    public async Task GetAsync_NonExisting_ReturnsError()
    {
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<ISTARODK> { IsError = true, Message = "Not found" });

        var result = await _manager.GetAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task GetAllAsync_ReturnsList()
    {
        _store.Setup(p => p.GetAllAsync(It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<IEnumerable<ISTARODK>>
                 {
                     Result = new[] { new STARODK { Name = "A" }, new STARODK { Name = "B" } }
                 });

        var result = await _manager.GetAllAsync();

        result.Result.Should().HaveCount(2);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_New_ShouldCreate()
    {
        var avatarId = Guid.NewGuid();
        _store.Setup(p => p.GetByNameAndAvatarAsync("Genesis", avatarId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<ISTARODK> { Result = null });
        _store.Setup(p => p.UpsertAsync(It.IsAny<ISTARODK>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((ISTARODK s, CancellationToken _) => new AZOAResult<ISTARODK> { Result = s });

        var model = new STARODKCreateModel { Name = "Genesis", Description = "Desc" };
        var result = await _manager.CreateOrUpdateAsync(model, avatarId);

        result.IsError.Should().BeFalse();
        result.Result!.Name.Should().Be("Genesis");
        result.Result.Description.Should().Be("Desc");
        result.Result.AvatarId.Should().Be(avatarId, "manager always stamps the authenticated avatar");
    }

    [Fact]
    public async Task CreateOrUpdateAsync_Existing_OwnedByCaller_ShouldUpdate()
    {
        var avatarId = Guid.NewGuid();
        var existing = new STARODK { Id = Guid.NewGuid(), Name = "Existing", Description = "Old", AvatarId = avatarId };
        _store.Setup(p => p.GetByNameAndAvatarAsync("Existing", avatarId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<ISTARODK> { Result = existing });
        _store.Setup(p => p.UpsertAsync(It.IsAny<ISTARODK>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((ISTARODK s, CancellationToken _) => new AZOAResult<ISTARODK> { Result = s });

        var model = new STARODKCreateModel { Name = "Existing", Description = "New" };
        var result = await _manager.CreateOrUpdateAsync(model, avatarId);

        result.IsError.Should().BeFalse();
        result.Result!.Description.Should().Be("New");
    }

    [Fact]
    public async Task CreateOrUpdateAsync_POST_DifferentAvatarSameName_DoesNotLoadOtherRecord()
    {
        // POST IDOR closure: Avatar B sending Name="A's record" must not find
        // A's record via the name lookup -- the store query is scoped by avatarId.
        var avatarB = Guid.NewGuid();
        _store.Setup(p => p.GetByNameAndAvatarAsync("Shared", avatarB, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<ISTARODK> { Result = null });
        _store.Setup(p => p.UpsertAsync(It.IsAny<ISTARODK>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((ISTARODK s, CancellationToken _) => new AZOAResult<ISTARODK> { Result = s });

        var model = new STARODKCreateModel { Name = "Shared", Description = "B's" };
        var result = await _manager.CreateOrUpdateAsync(model, avatarB);

        result.IsError.Should().BeFalse();
        result.Result!.AvatarId.Should().Be(avatarB);
        // GetByIdAsync was NEVER invoked -- the loaded record could only have come from name+avatar lookup.
        _store.Verify(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_PUT_DifferentAvatar_ReturnsForbiddenMarker()
    {
        // PUT IDOR closure: route id resolves to a record owned by a different avatar -> Forbidden.
        var ownerAvatar = Guid.NewGuid();
        var attackerAvatar = Guid.NewGuid();
        var existing = new STARODK { Id = Guid.NewGuid(), Name = "Owned", AvatarId = ownerAvatar };
        _store.Setup(p => p.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<ISTARODK> { Result = existing });

        var model = new STARODKCreateModel { Name = "Hijacked" };
        var result = await _manager.CreateOrUpdateAsync(model, attackerAvatar, routeId: existing.Id);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(STARODKAuthorizationError.Forbidden);
        // Crucially -- no upsert was performed.
        _store.Verify(p => p.UpsertAsync(It.IsAny<ISTARODK>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_PUT_OwnRecord_Updates()
    {
        var avatarId = Guid.NewGuid();
        var existing = new STARODK { Id = Guid.NewGuid(), Name = "Owned", AvatarId = avatarId, Description = "Old" };
        _store.Setup(p => p.GetByIdAsync(existing.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<ISTARODK> { Result = existing });
        _store.Setup(p => p.UpsertAsync(It.IsAny<ISTARODK>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((ISTARODK s, CancellationToken _) => new AZOAResult<ISTARODK> { Result = s });

        var model = new STARODKCreateModel { Name = "Owned", Description = "New" };
        var result = await _manager.CreateOrUpdateAsync(model, avatarId, routeId: existing.Id);

        result.IsError.Should().BeFalse();
        result.Result!.Description.Should().Be("New");
        result.Result.AvatarId.Should().Be(avatarId);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_PUT_UnknownId_ReturnsNotFoundMarker()
    {
        var avatarId = Guid.NewGuid();
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<ISTARODK> { IsError = true, Result = null, Message = "Not found." });

        var model = new STARODKCreateModel { Name = "X" };
        var result = await _manager.CreateOrUpdateAsync(model, avatarId, routeId: Guid.NewGuid());

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(STARODKAuthorizationError.NotFound);
    }

    [Fact]
    public async Task CreateOrUpdateAsync_IgnoresCallerSuppliedAvatarId()
    {
        // Defence in depth: even if the caller pushes a forged AvatarId in the body,
        // the manager stamps the authenticated avatar onto the persisted record.
        var authenticated = Guid.NewGuid();
        var forged        = Guid.NewGuid();
        _store.Setup(p => p.GetByNameAndAvatarAsync(It.IsAny<string>(), authenticated, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<ISTARODK> { Result = null });
        _store.Setup(p => p.UpsertAsync(It.IsAny<ISTARODK>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((ISTARODK s, CancellationToken _) => new AZOAResult<ISTARODK> { Result = s });

        var model = new STARODKCreateModel { Name = "Foo", AvatarId = forged };
        var result = await _manager.CreateOrUpdateAsync(model, authenticated);

        result.Result!.AvatarId.Should().Be(authenticated);
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
        var odk = new STARODK { Id = Guid.NewGuid(), Name = "Test" };
        _store.Setup(p => p.GetByIdAsync(odk.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<ISTARODK> { Result = odk });
        _store.Setup(p => p.UpsertAsync(It.IsAny<ISTARODK>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((ISTARODK s, CancellationToken _) => new AZOAResult<ISTARODK> { Result = s });

        var request = new STARDappGenerationRequest { TargetChain = "Solana", BoundHolonIds = new List<Guid> { Guid.NewGuid() } };
        var result = await _manager.GenerateAsync(odk.Id, request);

        result.IsError.Should().BeFalse();
        result.Result!.GeneratedCode.Should().NotBeNullOrEmpty();
        result.Result.TargetChain.Should().Be("Solana");
    }

    [Fact]
    public async Task GenerateAsync_NonExisting_ShouldReturnError()
    {
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<ISTARODK> { IsError = true, Message = "Not found" });

        var result = await _manager.GenerateAsync(Guid.NewGuid(), new STARDappGenerationRequest());

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task DeployAsync_ShouldSetDeploymentConfig()
    {
        var odk = new STARODK { Id = Guid.NewGuid(), Name = "Test", GeneratedCode = "code", TargetChain = "Algorand" };
        _store.Setup(p => p.GetByIdAsync(odk.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<ISTARODK> { Result = odk });
        _store.Setup(p => p.UpsertAsync(It.IsAny<ISTARODK>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((ISTARODK s, CancellationToken _) => new AZOAResult<ISTARODK> { Result = s });

        var result = await _manager.DeployAsync(odk.Id);

        result.IsError.Should().BeFalse();
        result.Result!.DeploymentConfig.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DeployAsync_WithoutGeneration_ShouldReturnError()
    {
        var odk = new STARODK { Id = Guid.NewGuid(), Name = "Test" };
        _store.Setup(p => p.GetByIdAsync(odk.Id, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<ISTARODK> { Result = odk });

        var result = await _manager.DeployAsync(odk.Id);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("must be generated");
    }

    [Fact]
    public async Task DeployAsync_NonExisting_ShouldReturnError()
    {
        _store.Setup(p => p.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new AZOAResult<ISTARODK> { IsError = true, Message = "Not found" });

        var result = await _manager.DeployAsync(Guid.NewGuid());

        result.IsError.Should().BeTrue();
    }
}
