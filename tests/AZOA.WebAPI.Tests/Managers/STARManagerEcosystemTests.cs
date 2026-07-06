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
public class STARManagerEcosystemTests
{
    private readonly Mock<ISTARStore> _store = new();
    private readonly Mock<IEcosystemStore> _ecosystemStore = new();
    private readonly Mock<IDappSeriesStore> _dappSeriesStore = new();
    private readonly STARManager _manager;

    public STARManagerEcosystemTests()
    {
        _manager = new STARManager(_store.Object, _ecosystemStore.Object, _dappSeriesStore.Object);
    }

    private void OwnStar(STARODK star) =>
        _store.Setup(p => p.GetByIdAsync(star.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<ISTARODK> { Result = star });

    private void OwnSeries(Guid seriesId, Guid avatarId) =>
        _dappSeriesStore.Setup(p => p.GetSeriesAsync(seriesId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<PocoDappSeries>
              {
                  Result = new PocoDappSeries { Id = seriesId.ToString("N"), AvatarId = avatarId.ToString("N"), Name = "S" }
              });

    private void CaptureEcosystemUpserts(List<PocoEcosystem> ecos, List<PocoEcosystemNode> nodes)
    {
        _ecosystemStore.Setup(e => e.GetByStarOdkAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AZOAResult<PocoEcosystem> { Result = ecos.LastOrDefault() });
        _ecosystemStore.Setup(e => e.UpsertAsync(It.IsAny<PocoEcosystem>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PocoEcosystem e, CancellationToken _) => { ecos.Add(e); return new AZOAResult<PocoEcosystem> { Result = e }; });
        _ecosystemStore.Setup(e => e.GetNodesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AZOAResult<IEnumerable<PocoEcosystemNode>> { Result = nodes.ToList() });
        _ecosystemStore.Setup(e => e.UpsertNodeAsync(It.IsAny<PocoEcosystemNode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PocoEcosystemNode n, CancellationToken _) => { nodes.Add(n); return new AZOAResult<PocoEcosystemNode> { Result = n }; });
        _store.Setup(p => p.UpsertAsync(It.IsAny<ISTARODK>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ISTARODK s, CancellationToken _) => new AZOAResult<ISTARODK> { Result = s });
    }

    [Fact]
    public async Task AddDappSeries_FirstAttach_LazilyCreatesEcosystem_AndRegeneratesCode()
    {
        var avatar = Guid.NewGuid();
        var star = new STARODK { Id = Guid.NewGuid(), Name = "Star", AvatarId = avatar };
        var seriesId = Guid.NewGuid();
        OwnStar(star);
        OwnSeries(seriesId, avatar);
        var ecos = new List<PocoEcosystem>();
        var nodes = new List<PocoEcosystemNode>();
        CaptureEcosystemUpserts(ecos, nodes);

        var req = new AddDappSeriesRequest { RefId = seriesId, RefKind = EcosystemRefKind.DappSeries };
        var result = await _manager.AddDappSeriesAsync(star.Id, req, avatar);

        result.IsError.Should().BeFalse();
        result.Result!.Roots.Should().HaveCount(1);
        result.Result.Roots[0].Node.RefId.Should().Be(seriesId);
        ecos.Should().HaveCount(1); // lazily created
        // Tree-walking codegen ran onto the STARODK.
        _store.Verify(p => p.UpsertAsync(It.Is<ISTARODK>(s => !string.IsNullOrEmpty(s.GeneratedCode)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AddDappSeries_ForeignStar_ReturnsForbidden_NoWrite()
    {
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var star = new STARODK { Id = Guid.NewGuid(), Name = "Star", AvatarId = owner };
        OwnStar(star);

        var req = new AddDappSeriesRequest { RefId = Guid.NewGuid() };
        var result = await _manager.AddDappSeriesAsync(star.Id, req, attacker);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(STARODKAuthorizationError.Forbidden);
        _ecosystemStore.Verify(e => e.UpsertNodeAsync(It.IsAny<PocoEcosystemNode>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddDappSeries_ForeignSeries_ReturnsForbidden()
    {
        var avatar = Guid.NewGuid();
        var otherAvatar = Guid.NewGuid();
        var star = new STARODK { Id = Guid.NewGuid(), Name = "Star", AvatarId = avatar };
        var seriesId = Guid.NewGuid();
        OwnStar(star);
        // Series owned by a DIFFERENT avatar.
        OwnSeries(seriesId, otherAvatar);

        var req = new AddDappSeriesRequest { RefId = seriesId };
        var result = await _manager.AddDappSeriesAsync(star.Id, req, avatar);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(STARODKAuthorizationError.Forbidden);
    }

    [Fact]
    public async Task AddDappSeries_NestedUnderParent_BuildsTwoLevelTree()
    {
        var avatar = Guid.NewGuid();
        var star = new STARODK { Id = Guid.NewGuid(), Name = "Star", AvatarId = avatar };
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();
        OwnStar(star);
        OwnSeries(s1, avatar);
        OwnSeries(s2, avatar);
        var ecos = new List<PocoEcosystem>();
        var nodes = new List<PocoEcosystemNode>();
        CaptureEcosystemUpserts(ecos, nodes);

        var r1 = await _manager.AddDappSeriesAsync(star.Id, new AddDappSeriesRequest { RefId = s1 }, avatar);
        var rootNodeId = r1.Result!.Roots[0].Node.Id;

        var r2 = await _manager.AddDappSeriesAsync(star.Id,
            new AddDappSeriesRequest { RefId = s2, ParentNodeId = rootNodeId }, avatar);

        r2.IsError.Should().BeFalse();
        r2.Result!.Roots.Should().HaveCount(1);
        r2.Result.Roots[0].Children.Should().HaveCount(1);
        r2.Result.Roots[0].Children[0].Node.RefId.Should().Be(s2);
    }

    [Fact]
    public async Task AddDappSeries_UnknownParent_Rejected()
    {
        var avatar = Guid.NewGuid();
        var star = new STARODK { Id = Guid.NewGuid(), Name = "Star", AvatarId = avatar };
        var seriesId = Guid.NewGuid();
        OwnStar(star);
        OwnSeries(seriesId, avatar);
        var ecos = new List<PocoEcosystem>();
        var nodes = new List<PocoEcosystemNode>();
        CaptureEcosystemUpserts(ecos, nodes);

        var req = new AddDappSeriesRequest { RefId = seriesId, ParentNodeId = Guid.NewGuid() };
        var result = await _manager.AddDappSeriesAsync(star.Id, req, avatar);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Parent node");
    }

    [Fact]
    public async Task GetEcosystem_CycleInParentChain_ReturnsError()
    {
        // Craft two nodes that point at each other (cycle) directly in the store.
        var avatar = Guid.NewGuid();
        var star = new STARODK { Id = Guid.NewGuid(), Name = "Star", AvatarId = avatar };
        OwnStar(star);

        var ecoId = Guid.NewGuid();
        var eco = new PocoEcosystem { Id = ecoId.ToString("N"), Name = "E", StarOdkId = star.Id.ToString("N"), AvatarId = avatar.ToString("N") };
        var a = Guid.NewGuid().ToString("N");
        var b = Guid.NewGuid().ToString("N");
        var nA = new PocoEcosystemNode { Id = a, EcosystemId = eco.Id, ParentNodeId = b, RefId = Guid.NewGuid().ToString("N") };
        var nB = new PocoEcosystemNode { Id = b, EcosystemId = eco.Id, ParentNodeId = a, RefId = Guid.NewGuid().ToString("N") };

        _ecosystemStore.Setup(e => e.GetByStarOdkAsync(star.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<PocoEcosystem> { Result = eco });
        _ecosystemStore.Setup(e => e.GetNodesAsync(ecoId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<PocoEcosystemNode>> { Result = new[] { nA, nB } });

        var result = await _manager.GetEcosystemAsync(star.Id, avatar);

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("cycle");
    }

    [Fact]
    public async Task GetEcosystem_ForeignStar_ReturnsForbidden()
    {
        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var star = new STARODK { Id = Guid.NewGuid(), Name = "Star", AvatarId = owner };
        OwnStar(star);

        var result = await _manager.GetEcosystemAsync(star.Id, attacker);

        result.IsError.Should().BeTrue();
        result.Message.Should().StartWith(STARODKAuthorizationError.Forbidden);
    }
}
