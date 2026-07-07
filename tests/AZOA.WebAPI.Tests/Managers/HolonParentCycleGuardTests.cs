using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>
/// Pins FR-6 / AC-6a/6b: <c>EnsureNotDescendantAsync</c> is called on every
/// <c>ParentHolonId</c> write path in <see cref="HolonManager"/>.
/// See Managers/AGENTS.md §holon-parent-cycle.
/// </summary>
public class HolonParentCycleGuardTests
{
    private readonly Mock<IHolonStore> _store = new();
    private readonly HolonManager _manager;

    // Fail-closed ownership guard (see Managers/AGENTS.md §fail-closed-avatar):
    // mutating calls now require the caller's avatar to own the target holon.
    // Fixtures are tagged with Owner and Owner is passed as the acting avatarId.
    private static readonly Guid Owner = Guid.NewGuid();

    public HolonParentCycleGuardTests()
    {
        _manager = new HolonManager(_store.Object);
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sets up the store so that querying children of <paramref name="holonId"/>
    /// (directly or transitively) eventually returns <paramref name="descendantId"/>,
    /// making <paramref name="descendantId"/> appear as a descendant. Uses two-level
    /// BFS: first query returns <paramref name="childId"/> as direct child;
    /// second query on that child returns <paramref name="descendantId"/>;
    /// all further queries return empty (stops the BFS).
    /// </summary>
    private void SetupDescendant(Guid holonId, Guid childId, Guid descendantId)
    {
        // GetDescendantsAsync uses QueryAsync with ParentHolonId filter.
        var child      = new Holon { Id = childId,      Name = "Child",      ParentHolonId = holonId };
        var descendant = new Holon { Id = descendantId, Name = "Grandchild", ParentHolonId = childId };

        _store
            .Setup(s => s.QueryAsync(
                It.Is<HolonQueryRequest>(q => q.ParentHolonId == holonId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>>
            {
                Result = new IHolon[] { child },
            });

        _store
            .Setup(s => s.QueryAsync(
                It.Is<HolonQueryRequest>(q => q.ParentHolonId == childId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>>
            {
                Result = new IHolon[] { descendant },
            });

        _store
            .Setup(s => s.QueryAsync(
                It.Is<HolonQueryRequest>(q => q.ParentHolonId == descendantId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });
    }

    private void SetupExisting(Guid holonId)
    {
        var holon = new Holon { Id = holonId, Name = "Existing", AvatarId = Owner };
        _store
            .Setup(s => s.GetByIdAsync(holonId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IHolon> { Result = holon });
    }

    private void SetupUpsertPassthrough()
    {
        _store
            .Setup(s => s.UpsertAsync(It.IsAny<IHolon>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IHolon h, CancellationToken _) => new AZOAResult<IHolon> { Result = h });
    }

    // ─── AC-6a: self-parent rejected via UpdateAsync ──────────────────────
    // CreateAsync self-parent is unreachable through the API (HolonCreateModel
    // has no Id field; the new Holon is assigned a fresh Guid server-side, so
    // a caller cannot pass the not-yet-known Id as ParentHolonId). The guard is
    // defence-in-depth for internal calls — covered by MoveSubtreeAsync_SelfParent_Rejected.
    // This test pins the same self-parent rejection path via UpdateAsync, which
    // IS API-reachable and exercises the identical EnsureNotDescendantAsync branch.

    [Fact]
    public async Task UpdateAsync_SelfParent_Rejected()
    {
        var holonId = Guid.NewGuid();

        SetupExisting(holonId);
        // No descendants — EnsureNotDescendantAsync short-circuits on id == proposedParentId
        // before the BFS. Store returns empty children for completeness.
        _store
            .Setup(s => s.QueryAsync(
                It.Is<HolonQueryRequest>(q => q.ParentHolonId == holonId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });

        var result = await _manager.UpdateAsync(holonId, new HolonUpdateModel
        {
            ParentHolonId = holonId, // self-parent
        });

        result.IsError.Should().BeTrue("a holon cannot be its own parent (AC-6a, self-parent via UpdateAsync)");
        result.Message.Should().ContainEquivalentOf("own parent");
    }

    // ─── AC-6b: UpdateAsync rejects cycle ────────────────────────────────

    [Fact]
    public async Task UpdateAsync_DescendantAsParent_Rejected()
    {
        var parentId     = Guid.NewGuid();
        var childId      = Guid.NewGuid();
        var grandChildId = Guid.NewGuid();

        SetupExisting(parentId);
        SetupDescendant(parentId, childId, grandChildId);
        SetupUpsertPassthrough();

        // Attempt to set grandChildId (a descendant) as the new parent of parentId.
        var result = await _manager.UpdateAsync(parentId, new HolonUpdateModel
        {
            ParentHolonId = grandChildId,
        });

        result.IsError.Should().BeTrue("setting a descendant as parent creates a cycle (AC-6b)");
        result.Message.Should().ContainEquivalentOf("cycle");
    }

    [Fact]
    public async Task UpdateAsync_NonDescendantParent_Allowed()
    {
        var holonId   = Guid.NewGuid();
        var newParent = Guid.NewGuid(); // unrelated holon — no cycle

        SetupExisting(holonId);

        // No descendants configured — GetDescendantsAsync returns empty.
        _store
            .Setup(s => s.QueryAsync(
                It.Is<HolonQueryRequest>(q => q.ParentHolonId == holonId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });

        SetupUpsertPassthrough();

        var result = await _manager.UpdateAsync(holonId, new HolonUpdateModel
        {
            ParentHolonId = newParent,
        });

        result.IsError.Should().BeFalse("unrelated parent causes no cycle");
    }

    // ─── AC-6b: InteractAsync rejects cycle ───────────────────────────────

    [Fact]
    public async Task InteractAsync_DescendantAsNewParent_Rejected()
    {
        var rootId       = Guid.NewGuid();
        var childId      = Guid.NewGuid();
        var grandChildId = Guid.NewGuid();

        SetupExisting(rootId);
        SetupDescendant(rootId, childId, grandChildId);
        SetupUpsertPassthrough();

        var result = await _manager.InteractAsync(rootId, new HolonInteractionRequest
        {
            NewParentHolonId = grandChildId,
        }, Owner);

        result.IsError.Should().BeTrue("InteractAsync must guard cycle on NewParentHolonId (AC-6b)");
        result.Message.Should().ContainEquivalentOf("cycle");
    }

    [Fact]
    public async Task InteractAsync_NoNewParent_NoCycleCheck_Passes()
    {
        var holonId = Guid.NewGuid();
        SetupExisting(holonId);
        SetupUpsertPassthrough();

        // No NewParentHolonId — cycle guard must NOT be called; request passes.
        var result = await _manager.InteractAsync(holonId, new HolonInteractionRequest(), Owner);

        result.IsError.Should().BeFalse("no reparent means no cycle check needed");
    }

    // ─── AC-6a: MoveSubtreeAsync self-parent rejected ────────────────────

    [Fact]
    public async Task MoveSubtreeAsync_SelfParent_Rejected()
    {
        var holonId = Guid.NewGuid();

        // No descendants needed — self-parent check is before the BFS.
        _store
            .Setup(s => s.QueryAsync(
                It.Is<HolonQueryRequest>(q => q.ParentHolonId == holonId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<IHolon>> { Result = Array.Empty<IHolon>() });

        var result = await _manager.MoveSubtreeAsync(holonId, holonId, Owner);

        result.IsError.Should().BeTrue("a holon cannot be its own parent (self-parent cycle)");
        result.Message.Should().ContainEquivalentOf("own parent");
    }

    [Fact]
    public async Task MoveSubtreeAsync_DescendantAsNewParent_Rejected()
    {
        var rootId       = Guid.NewGuid();
        var childId      = Guid.NewGuid();
        var grandChildId = Guid.NewGuid();

        SetupDescendant(rootId, childId, grandChildId);
        SetupExisting(rootId);
        SetupUpsertPassthrough();

        var result = await _manager.MoveSubtreeAsync(rootId, grandChildId, Owner);

        result.IsError.Should().BeTrue("MoveSubtreeAsync must guard cycle (original precedent)");
        result.Message.Should().ContainEquivalentOf("cycle");
    }
}
