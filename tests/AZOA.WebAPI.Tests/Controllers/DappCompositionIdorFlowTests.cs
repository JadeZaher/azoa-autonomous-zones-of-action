using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using AZOA.WebAPI.Controllers;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Tests.TestSupport;

namespace AZOA.WebAPI.Tests.Controllers;

/// <summary>
/// Round-4 resource IDOR (composition oracles, ActionGraphBundle refusals): driven
/// through <see cref="DappCompositionController"/> / <see cref="ActionGraphBundleController"/>
/// over the real managers and in-memory stores.
/// </summary>
public class DappCompositionIdorFlowTests
{
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Stranger = Guid.NewGuid();

    private readonly InMemoryDappSeriesStore _series = new();
    private readonly InMemoryActionStore _actions = new();
    private readonly InMemoryActionRunStore _runs = new();
    private readonly InMemoryHolonStore _holons = new();
    private readonly InMemoryBundleStore _bundles = new();

    private DappCompositionManager Composition() => new(
        _series, _actions, _runs, _holons, Mock.Of<IActionGraphBundleManager>(), Mock.Of<IDeploymentStore>());

    private static ControllerContext As(Guid userId) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "Test"))
        }
    };

    private ActionGraphBundleController BundlesAs(Guid userId) => new(new ActionGraphBundleManager(
        _bundles, Mock.Of<IEcosystemStore>(), _series, Mock.Of<IDeploymentService>())) { ControllerContext = As(userId) };

    private Guid SeedActionBindingHolons(params Guid[] holonIds)
    {
        var action = new ActionDefinition
        {
            Id = Guid.NewGuid(), Name = "binds", UserId = Owner,
            Nodes = holonIds.Select(id => new ActionNode
            {
                Id = Guid.NewGuid(), Name = "n", NodeType = ActionNodeType.HolonGet,
                Config = JsonSerializer.Serialize(new { holonId = id.ToString() }),
            }).ToList(),
        };
        _actions.UpsertActionAsync(action).GetAwaiter().GetResult();
        return action.Id;
    }

    [Fact]
    public async Task Validate_TreatsAnotherUsersPrivateHolonExactlyLikeAMissingOne_AndResolvesTheOwnersHolon()
    {
        var foreignPrivate = _holons.Seed(new Holon { Id = Guid.NewGuid(), UserId = Stranger, IsPublic = false });
        var own = _holons.Seed(new Holon { Id = Guid.NewGuid(), UserId = Owner, IsPublic = false });
        var missing = Guid.NewGuid();
        var composition = Composition();
        var series = (await composition.CreateAsync(Owner, new DappSeriesCreateModel { Name = "s" })).Result!;
        var actionId = SeedActionBindingHolons(foreignPrivate.Id, own.Id, missing);
        (await composition.AddActionAsync(series.IdGuid, Owner, new DappSeriesAddActionModel { ActionId = actionId, Order = 1 })).IsError.Should().BeFalse();

        var report = (AZOAResult<CompositionValidationResult>)((OkObjectResult)(await new DappCompositionController(composition)
            { ControllerContext = As(Owner) }.Validate(series.IdGuid, default)).Result!).Value!;

        report.Result!.Diagnostics.Should().Contain($"Referenced holon {foreignPrivate.Id} not found.")
            .And.Contain($"Referenced holon {missing} not found.")
            .And.NotContain(d => d.Contains(own.Id.ToString()));
    }

    [Fact]
    public async Task SeriesAndActionProbes_ReadLikeMisses_ForAnotherUser_WhileTheOwnerProceeds()
    {
        var composition = Composition();
        var ownersSeries = (await composition.CreateAsync(Owner, new DappSeriesCreateModel { Name = "owner" })).Result!;
        var strangersSeries = (await composition.CreateAsync(Stranger, new DappSeriesCreateModel { Name = "stranger" })).Result!;
        var ownersAction = SeedActionBindingHolons();
        var missingId = Guid.NewGuid();

        (await composition.GetAsync(missingId, Stranger)).Message.Should().Be($"DappSeries {missingId} not found.");
        (await composition.GetAsync(ownersSeries.IdGuid, Stranger)).Message.Should().Be($"DappSeries {ownersSeries.IdGuid} not found.",
            "a foreign series reads exactly like a missing one");
        (await composition.AddActionAsync(strangersSeries.IdGuid, Stranger, new DappSeriesAddActionModel { ActionId = ownersAction, Order = 1 })).Message
            .Should().Be($"Action {ownersAction} not found.");
        (await _series.GetActionsBySeriesAsync(strangersSeries.IdGuid)).Result.Should().BeEmpty();

        (await composition.AddActionAsync(ownersSeries.IdGuid, Owner, new DappSeriesAddActionModel { ActionId = ownersAction, Order = 1 }))
            .IsError.Should().BeFalse();
    }

    [Fact]
    public async Task BundleWrites_ByAnotherUser_Are404ForPrivate_403ForPublic_AndNeverLand_WhileTheOwnerWrites()
    {
        var privateBundle = _bundles.Seed(new ActionGraphBundle { Id = Guid.NewGuid(), Name = "private", UserId = Owner });
        var publicBundle = _bundles.Seed(new ActionGraphBundle { Id = Guid.NewGuid(), Name = "public", UserId = Owner, IsPublic = true });
        var stranger = BundlesAs(Stranger);
        var hijack = new ActionGraphBundleCreateModel { Name = "hijacked" };

        (await stranger.Update(privateBundle.Id, hijack, null)).Result.Should().BeOfType<NotFoundObjectResult>();
        (await stranger.Update(publicBundle.Id, hijack, null)).Result
            .Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await stranger.Delete(privateBundle.Id, null)).Result.Should().BeOfType<NotFoundObjectResult>();
        _bundles.Row(privateBundle.Id)!.Name.Should().Be("private");
        _bundles.Row(publicBundle.Id)!.Name.Should().Be("public");

        (await BundlesAs(Owner).Update(privateBundle.Id, new ActionGraphBundleCreateModel { Name = "renamed" }, null))
            .Result.Should().BeOfType<OkObjectResult>();
        _bundles.Row(privateBundle.Id)!.Name.Should().Be("renamed");
        (await BundlesAs(Owner).Delete(privateBundle.Id, null)).Result.Should().BeOfType<OkObjectResult>();
        _bundles.Row(privateBundle.Id).Should().BeNull();
    }

    /// <summary>Copy-on-read in-memory bundle store.</summary>
    private sealed class InMemoryBundleStore : IActionGraphBundleStore
    {
        private readonly Dictionary<Guid, ActionGraphBundle> _rows = new();

        public ActionGraphBundle Seed(ActionGraphBundle bundle) { _rows[bundle.Id] = Copy(bundle); return bundle; }

        public ActionGraphBundle? Row(Guid id) => _rows.TryGetValue(id, out var row) ? Copy(row) : null;

        public Task<AZOAResult<IActionGraphBundle>> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_rows.TryGetValue(id, out var row)
                ? AZOAResult<IActionGraphBundle>.Success(Copy(row))
                : AZOAResult<IActionGraphBundle>.Failure("not found"));

        public Task<AZOAResult<IActionGraphBundle>> GetByNameAndUserAsync(string name, Guid userId, CancellationToken ct = default) =>
            Task.FromResult(_rows.Values.FirstOrDefault(row => row.Name == name && row.UserId == userId) is { } row
                ? AZOAResult<IActionGraphBundle>.Success(Copy(row))
                : AZOAResult<IActionGraphBundle>.Failure("not found"));

        public Task<AZOAResult<IEnumerable<IActionGraphBundle>>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<IEnumerable<IActionGraphBundle>>.Success(_rows.Values.Select(Copy).Cast<IActionGraphBundle>().ToList()));

        public Task<AZOAResult<IActionGraphBundle>> UpsertAsync(IActionGraphBundle bundle, CancellationToken ct = default)
        {
            _rows[bundle.Id] = Copy((ActionGraphBundle)bundle);
            return Task.FromResult(AZOAResult<IActionGraphBundle>.Success(Copy((ActionGraphBundle)bundle)));
        }

        public Task<AZOAResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(AZOAResult<bool>.Success(_rows.Remove(id)));

        private static ActionGraphBundle Copy(ActionGraphBundle row) => new()
        {
            Id = row.Id, Name = row.Name, Description = row.Description, PublicKey = row.PublicKey,
            UserId = row.UserId, IsPublic = row.IsPublic, IsActive = row.IsActive,
            TargetChain = row.TargetChain, GeneratedCode = row.GeneratedCode, DeploymentConfig = row.DeploymentConfig,
            BoundHolonIds = new List<Guid>(row.BoundHolonIds), CreatedDate = row.CreatedDate, ModifiedDate = row.ModifiedDate,
        };
    }
}
