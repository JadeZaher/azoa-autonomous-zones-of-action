using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Controllers;

/// <summary>
/// Round-4 resource IDOR (audit-2 H1, M6): holon routes driven through
/// <see cref="HolonController"/> over the real <see cref="HolonManager"/> and an
/// in-memory store, asserting what the other user can and cannot do to the store.
/// </summary>
public class HolonIdorFlowTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    private readonly InMemoryHolonStore _store = new();

    private HolonController ControllerFor(Guid userId) => new(new HolonManager(_store), Mock.Of<IBlockchainOperationManager>())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"))
            }
        }
    };

    private Holon Seed(string name, Guid owner, bool isPublic, Guid? parent = null) => _store.Seed(new Holon
    {
        Id = Guid.NewGuid(),
        Name = name,
        UserId = owner,
        IsPublic = isPublic,
        ParentHolonId = parent,
    });

    private static T Payload<T>(ActionResult<T> result) where T : class =>
        (result.Result as ObjectResult)?.Value as T ?? throw new InvalidOperationException("No payload.");

    [Fact]
    public async Task Clone_PublicTemplateWithSubtree_CopiesOnlyDescendantsTheCallerMayRead()
    {
        var root = Seed("template", Owner, isPublic: true);
        var publicChild = Seed("public-step", Owner, isPublic: true, parent: root.Id);
        var privateChild = Seed("private-step", Owner, isPublic: false, parent: root.Id);
        var underPrivate = Seed("public-under-private", Owner, isPublic: true, parent: privateChild.Id);

        var strangerClone = await ControllerFor(Stranger).Clone(root.Id, new HolonCloneRequest { IncludeSubtree = true }, null);

        strangerClone.Result.Should().BeOfType<OkObjectResult>();
        var strangerCopies = _store.Rows.Where(row => row.UserId == Stranger).ToList();
        strangerCopies.Select(row => row.SourceHolonId).Should().BeEquivalentTo(new Guid?[] { root.Id, publicChild.Id },
            "a private descendant and the branch hanging under it are never copied to another user");

        var ownerClone = await ControllerFor(Owner).Clone(root.Id, new HolonCloneRequest { IncludeSubtree = true }, null);

        ownerClone.Result.Should().BeOfType<OkObjectResult>();
        _store.Rows.Where(row => row.UserId == Owner && row.SourceHolonId != null)
            .Select(row => row.SourceHolonId)
            .Should().BeEquivalentTo(new Guid?[] { root.Id, publicChild.Id, privateChild.Id, underPrivate.Id });
    }

    [Fact]
    public async Task Reparenting_UnderAnotherUsersHolon_IsRefusedOnEveryWritePath_AndOwnParentIsAllowed()
    {
        var foreignParent = Seed("their-root", Stranger, isPublic: true);
        var ownParent = Seed("my-root", Owner, isPublic: false);
        var mine = Seed("mine", Owner, isPublic: false);
        var controller = ControllerFor(Owner);

        var create = await controller.Create(new HolonCreateModel { Name = "new", ParentHolonId = foreignParent.Id }, null);
        var update = await controller.Update(mine.Id, new HolonUpdateModel { ParentHolonId = foreignParent.Id }, null);
        var interact = await controller.Interact(mine.Id, new HolonInteractionRequest { NewParentHolonId = foreignParent.Id }, null);
        var move = await controller.MoveSubtree(mine.Id, new MoveSubtreeRequest { NewParentId = foreignParent.Id }, null);
        var clone = await controller.Clone(mine.Id, new HolonCloneRequest { NewParentId = foreignParent.Id }, null);

        foreach (var refusal in new[] { Payload(create).Message, Payload(update).Message, Payload(interact).Message, Payload(move).Message, Payload(clone).Message })
            refusal.Should().Be("Parent holon not found.");
        _store.Rows.Should().NotContain(row => row.ParentHolonId == foreignParent.Id,
            "no write may hang a holon under another user's tree");

        var allowed = await controller.MoveSubtree(mine.Id, new MoveSubtreeRequest { NewParentId = ownParent.Id }, null);

        allowed.Result.Should().BeOfType<OkObjectResult>();
        _store.Row(mine.Id)!.ParentHolonId.Should().Be(ownParent.Id);
    }

    [Fact]
    public async Task Propagate_WritesOnlyTheCallersHolons_InAMixedOwnershipSubtree()
    {
        var root = Seed("root", Owner, isPublic: false);
        var ownChild = Seed("own-child", Owner, isPublic: false, parent: root.Id);
        var foreignChild = Seed("legacy-foreign-child", Stranger, isPublic: false, parent: root.Id);
        var ownUnderForeign = Seed("own-under-foreign", Owner, isPublic: false, parent: foreignChild.Id);

        var result = await ControllerFor(Owner).Propagate(root.Id,
            new HolonPropagateRequest { Property = "IsActive", Value = false, IncludeSelf = true }, null);

        Payload(result).Result.Should().Be(3);
        _store.Row(root.Id)!.IsActive.Should().BeFalse();
        _store.Row(ownChild.Id)!.IsActive.Should().BeFalse();
        _store.Row(ownUnderForeign.Id)!.IsActive.Should().BeFalse();
        _store.Row(foreignChild.Id)!.IsActive.Should().BeTrue("propagation never writes another user's holon");
    }

    [Fact]
    public async Task MutatingAnotherUsersPrivateHolon_ReadsAsNotFound_WhileTheOwnerSucceeds()
    {
        var secret = Seed("secret", Owner, isPublic: false);

        var probe = await ControllerFor(Stranger).Update(secret.Id, new HolonUpdateModel { Name = "pwned" }, null);
        var missing = await ControllerFor(Stranger).Update(Guid.NewGuid(), new HolonUpdateModel { Name = "pwned" }, null);

        Payload(probe).Message.Should().Be(Payload(missing).Message, "a refusal must not confirm the holon exists");
        _store.Row(secret.Id)!.Name.Should().Be("secret");

        var owner = await ControllerFor(Owner).Update(secret.Id, new HolonUpdateModel { Name = "renamed" }, null);

        owner.Result.Should().BeOfType<OkObjectResult>();
        _store.Row(secret.Id)!.Name.Should().Be("renamed");
    }
}
