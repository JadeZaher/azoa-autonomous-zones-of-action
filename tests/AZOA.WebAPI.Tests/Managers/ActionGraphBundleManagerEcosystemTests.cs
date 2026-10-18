using FluentAssertions;
using Moq;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Ecosystem;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using PocoEcosystem = AZOA.WebAPI.Persistence.SurrealDb.Models.Ecosystem;
using PocoEcosystemNode = AZOA.WebAPI.Persistence.SurrealDb.Models.EcosystemNode;
using PocoDappSeries = AZOA.WebAPI.Persistence.SurrealDb.Models.DappSeries;

namespace AZOA.WebAPI.Tests.Managers;

/// <summary>D2 ecosystem-tree unit coverage: attach, IDOR ownership, lazy
/// ecosystem creation, tree-walking codegen, and the parent-cycle guard.</summary>
public class ActionGraphBundleManagerEcosystemTests
{
    private readonly Mock<IActionGraphBundleStore> _store = new();
    private readonly Mock<IEcosystemStore> _ecosystemStore = new();
    private readonly Mock<IDappSeriesStore> _dappSeriesStore = new();
    private readonly ActionGraphBundleManager _manager;

    public ActionGraphBundleManagerEcosystemTests()
    {
        _manager = new ActionGraphBundleManager(_store.Object, _ecosystemStore.Object, _dappSeriesStore.Object, Mock.Of<IDeploymentService>());
    }

    private void OwnActionGraphBundle(ActionGraphBundle bundle) =>
        _store.Setup(p => p.GetByIdAsync(bundle.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<IActionGraphBundle> { Result = bundle });

    private void OwnSeries(Guid seriesId, Guid userId) =>
        _dappSeriesStore.Setup(p => p.GetSeriesAsync(seriesId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<PocoDappSeries>
              {
                  Result = new PocoDappSeries { Id = seriesId.ToString("N"), UserId = userId.ToString("N"), Name = "S" }
              });

    private void CaptureEcosystemUpserts(List<PocoEcosystem> ecos, List<PocoEcosystemNode> nodes)
    {
        _ecosystemStore.Setup(e => e.GetByActionGraphBundleAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AZOAResult<PocoEcosystem> { Result = ecos.LastOrDefault() });
        _ecosystemStore.Setup(e => e.UpsertAsync(It.IsAny<PocoEcosystem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PocoEcosystem e, CancellationToken _) => { ecos.Add(e); return new AZOAResult<PocoEcosystem> { Result = e }; });
        _ecosystemStore.Setup(e => e.GetNodesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AZOAResult<IEnumerable<PocoEcosystemNode>> { Result = nodes.ToList() });
        _ecosystemStore.Setup(e => e.UpsertNodeAsync(It.IsAny<PocoEcosystemNode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PocoEcosystemNode n, CancellationToken _) => { nodes.Add(n); return new AZOAResult<PocoEcosystemNode> { Result = n }; });
        _store.Setup(p => p.UpsertAsync(It.IsAny<IActionGraphBundle>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IActionGraphBundle s, CancellationToken _) => new AZOAResult<IActionGraphBundle> { Result = s });
    }

    [Fact]
    public async Task AddDappSeries_FirstAttach_LazilyCreatesEcosystem_AndRegeneratesCode()
    {
        var user = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "ActionGraphBundle", UserId = user };
        var seriesId = Guid.NewGuid();
        OwnActionGraphBundle(bundle);
        OwnSeries(seriesId, user);
        var ecos = new List<PocoEcosystem>();
        var nodes = new List<PocoEcosystemNode>();
        CaptureEcosystemUpserts(ecos, nodes);

        var req = new AddDappSeriesRequest { RefId = seriesId, RefKind = EcosystemRefKind.DappSeries };
        var result = await _manager.AddDappSeriesAsync(bundle.Id, req, user);

        result.IsError.Should().BeFalse();
        result.Result!.Roots.Should().HaveCount(1);
        result.Result.Roots[0].Node.RefId.Should().Be(seriesId);
        ecos.Should().HaveCount(1); // lazily created
        // Tree-walking codegen ran onto the ActionGraphBundle.
        _store.Verify(p => p.UpsertAsync(It.Is<IActionGraphBundle>(s => s.GeneratedCode == null && s.DeploymentConfig == null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddDappSeries_ForeignPrivateActionGraphBundle_ReturnsNotFound_NoWrite()
    {
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "ActionGraphBundle", UserId = owner };
        OwnActionGraphBundle(bundle);

        var req = new AddDappSeriesRequest { RefId = Guid.NewGuid() };
        var result = await _manager.AddDappSeriesAsync(bundle.Id, req, attacker);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(ActionGraphBundleAuthorizationError.NotFound);
        _ecosystemStore.Verify(e => e.UpsertNodeAsync(It.IsAny<PocoEcosystemNode>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddDappSeries_ForeignSeries_ReturnsNotFound()
    {
        var user = Guid.NewGuid();
        var otherUser = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "ActionGraphBundle", UserId = user };
        var seriesId = Guid.NewGuid();
        OwnActionGraphBundle(bundle);
        // Series owned by a DIFFERENT user.
        OwnSeries(seriesId, otherUser);

        var req = new AddDappSeriesRequest { RefId = seriesId };
        var result = await _manager.AddDappSeriesAsync(bundle.Id, req, user);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(ActionGraphBundleAuthorizationError.NotFound);
    }

    [Fact]
    public async Task AddDappSeries_NestedUnderParent_BuildsTwoLevelTree()
    {
        var user = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "ActionGraphBundle", UserId = user };
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        OwnActionGraphBundle(bundle);
        OwnSeries(s1, user);
        OwnSeries(s2, user);
        var ecos = new List<PocoEcosystem>();
        var nodes = new List<PocoEcosystemNode>();
        CaptureEcosystemUpserts(ecos, nodes);

        var r1 = await _manager.AddDappSeriesAsync(bundle.Id, new AddDappSeriesRequest { RefId = s1 }, user);
        var rootNodeId = r1.Result!.Roots[0].Node.Id;

        var r2 = await _manager.AddDappSeriesAsync(bundle.Id,
            new AddDappSeriesRequest { RefId = s2, ParentNodeId = rootNodeId }, user);

        r2.IsError.Should().BeFalse();
        r2.Result!.Roots.Should().HaveCount(1);
        r2.Result.Roots[0].Children.Should().HaveCount(1);
        r2.Result.Roots[0].Children[0].Node.RefId.Should().Be(s2);
    }

    [Fact]
    public async Task AddDappSeries_UnknownParent_Rejected()
    {
        var user = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "ActionGraphBundle", UserId = user };
        var seriesId = Guid.NewGuid();
        OwnActionGraphBundle(bundle);
        OwnSeries(seriesId, user);
        var ecos = new List<PocoEcosystem>();
        var nodes = new List<PocoEcosystemNode>();
        CaptureEcosystemUpserts(ecos, nodes);

        var req = new AddDappSeriesRequest { RefId = seriesId, ParentNodeId = Guid.NewGuid() };
        var result = await _manager.AddDappSeriesAsync(bundle.Id, req, user);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Parent node");
    }

    [Fact]
    public async Task GetEcosystem_CycleInParentChain_ReturnsError()
    {
        // Craft two nodes that point at each other (cycle) directly in the store.
        var user = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "ActionGraphBundle", UserId = user };
        OwnActionGraphBundle(bundle);

        var ecoId = Guid.NewGuid();
        var eco = new PocoEcosystem { Id = ecoId.ToString("N"), Name = "E", ActionGraphBundleId = bundle.Id.ToString("N"), UserId = user.ToString("N") };
        var a = Guid.NewGuid().ToString("N");
        var b = Guid.NewGuid().ToString("N");
        var nA = new PocoEcosystemNode { Id = a, EcosystemId = eco.Id, ParentNodeId = b, RefId = Guid.NewGuid().ToString("N") };
        var nB = new PocoEcosystemNode { Id = b, EcosystemId = eco.Id, ParentNodeId = a, RefId = Guid.NewGuid().ToString("N") };

        _ecosystemStore.Setup(e => e.GetByActionGraphBundleAsync(bundle.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<PocoEcosystem> { Result = eco });
        _ecosystemStore.Setup(e => e.GetNodesAsync(ecoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<PocoEcosystemNode>> { Result = new[] { nA, nB } });

        var result = await _manager.GetEcosystemAsync(bundle.Id, user);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("cycle");
    }

    [Fact]
    public async Task GetEcosystem_ForeignPrivateActionGraphBundle_ReturnsNotFound()
    {
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var bundle = new ActionGraphBundle { Id = Guid.NewGuid(), Name = "ActionGraphBundle", UserId = owner };
        OwnActionGraphBundle(bundle);

        var result = await _manager.GetEcosystemAsync(bundle.Id, attacker);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(ActionGraphBundleAuthorizationError.NotFound);
    }
}
