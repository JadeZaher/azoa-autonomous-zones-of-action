using System.Text.Json;
using FluentAssertions;
using Moq;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using ActionDef = AZOA.WebAPI.Models.Actions.ActionDefinition;
using ActionNodeDef = AZOA.WebAPI.Models.Actions.ActionNode;
using ActionRunDef = AZOA.WebAPI.Models.Actions.ActionRun;
using ActionDependencyDef = AZOA.WebAPI.Models.Actions.ActionDependency;

namespace AZOA.WebAPI.Tests;

/// <summary>
/// Manager-level tests for the dapp-composition slice. Covers the 5
/// composition validation rules from spec.md, status-machine guardrails
/// (delete in non-Draft, deploy without Generate, etc.), and user
/// scoping.
/// </summary>
public class DappCompositionManagerTests
{
    private readonly InMemoryDappSeriesStore _seriesStore = new();
    private readonly Mock<IActionStore> _actionStore = new();
    private readonly Mock<IActionRunStore> _runStore = new();
    private readonly Mock<IHolonStore> _holonStore = new();
    private readonly Mock<ISTARManager> _starManager = new();
    private readonly DappCompositionManager _manager;
    private readonly Guid _userId = Guid.NewGuid();

    public DappCompositionManagerTests()
    {
        _manager = new DappCompositionManager(
            _seriesStore, _actionStore.Object, _runStore.Object, _holonStore.Object, _starManager.Object);
    }

    // ── Series CRUD ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ShouldStartInDraftStatus()
    {
        var result = await _manager.CreateAsync(_userId,
            new DappSeriesCreateModel { Name = "My dApp" });

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(DappSeries.StatusKind.Draft);
        result.Result.UserIdGuid.Should().Be(_userId);
        result.Result.Name.Should().Be("My dApp");
    }

    [Fact]
    public async Task CreateAsync_RejectsEmptyName()
    {
        var result = await _manager.CreateAsync(_userId, new DappSeriesCreateModel { Name = "" });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Name");
    }

    [Fact]
    public async Task GetAsync_RejectsCrossUserAccess()
    {
        var create = await _manager.CreateAsync(_userId, new DappSeriesCreateModel { Name = "Owned" });
        var seriesId = create.Result!.IdGuid;

        var otherUser = Guid.NewGuid();
        var get = await _manager.GetAsync(seriesId, otherUser);

        get.IsError.Should().BeTrue();
        get.Message.Should().Contain("Forbidden");
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyTheCallingUsersSeries()
    {
        var otherUser = Guid.NewGuid();
        var mine = await _manager.CreateAsync(_userId, new DappSeriesCreateModel { Name = "Mine" });
        await _manager.CreateAsync(otherUser, new DappSeriesCreateModel { Name = "Theirs" });

        var list = await _manager.ListAsync(_userId);

        list.IsError.Should().BeFalse();
        list.Result.Should().ContainSingle(s => s.IdGuid == mine.Result!.IdGuid);
        list.Result.Should().NotContain(s => s.UserIdGuid == otherUser);
    }

    [Fact]
    public async Task DeleteAsync_BlocksDeletionOfReadySeries()
    {
        var create = await _manager.CreateAsync(_userId, new DappSeriesCreateModel { Name = "Promote" });
        var seriesId = create.Result!.IdGuid;
        create.Result.Status = DappSeries.StatusKind.Ready;
        await _seriesStore.UpsertSeriesAsync(create.Result);

        var del = await _manager.DeleteAsync(seriesId, _userId);

        del.IsError.Should().BeTrue();
        del.Message.Should().Contain("archive");
    }

    [Fact]
    public async Task UpdateAsync_RejectsCrossUserWrites()
    {
        var create = await _manager.CreateAsync(_userId, new DappSeriesCreateModel { Name = "Owned" });
        var seriesId = create.Result!.IdGuid;

        var result = await _manager.UpdateAsync(seriesId, Guid.NewGuid(), new DappSeriesUpdateModel
        {
            Name = "Intruder edit",
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Forbidden");
    }

    // ── Validator 1: All Actions Completed ─────────────────────────────────────

    [Fact]
    public async Task Validate_FailsWhenLatestRunIsNotSucceeded()
    {
        var (seriesId, actionId) = await SetupSeriesWithOneAction();
        SetupLatestRunStatus(actionId, ActionRunStatus.Failed);

        var report = await _manager.ValidateAsync(seriesId, _userId);

        report.Result!.AllActionsCompleted.Should().BeFalse();
        report.Result.Diagnostics.Should().Contain(d => d.Contains("Failed"));
    }

    [Fact]
    public async Task Validate_FailsWhenActionHasNoRunsAtAll()
    {
        var (seriesId, actionId) = await SetupSeriesWithOneAction();
        _runStore.Setup(s => s.GetByActionIdAsync(actionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<ActionRunDef>> { Result = Array.Empty<ActionRunDef>() });

        var report = await _manager.ValidateAsync(seriesId, _userId);

        report.Result!.AllActionsCompleted.Should().BeFalse();
        report.Result.Diagnostics.Should().Contain(d => d.Contains("never been executed"));
    }

    [Fact]
    public async Task Validate_PassesAllActionsCompletedWhenLatestRunSucceeded()
    {
        var (seriesId, actionId) = await SetupSeriesWithOneAction();
        SetupLatestRunStatus(actionId, ActionRunStatus.Succeeded);

        var report = await _manager.ValidateAsync(seriesId, _userId);

        report.Result!.AllActionsCompleted.Should().BeTrue();
    }

    // ── Validator 2: Chain Completeness ───────────────────────────────────────

    [Fact]
    public async Task Validate_FailsWhenSecondActionHasNoPriorDependency()
    {
        var (seriesId, actionA, actionB) = await SetupTwoActionSeriesWithoutDependency();
        SetupLatestRunStatus(actionA, ActionRunStatus.Succeeded);
        SetupLatestRunStatus(actionB, ActionRunStatus.Succeeded);

        var report = await _manager.ValidateAsync(seriesId, _userId);

        report.Result!.ChainCompleteness.Should().BeFalse();
        report.Result.Diagnostics.Should().Contain(d => d.Contains("no ActionDependency"));
    }

    [Fact]
    public async Task Validate_PassesChainWhenSecondActionDependsOnFirst()
    {
        var (seriesId, actionA, actionB) = await SetupTwoActionSeriesWithDependency();
        SetupLatestRunStatus(actionA, ActionRunStatus.Succeeded);
        SetupLatestRunStatus(actionB, ActionRunStatus.Succeeded);

        var report = await _manager.ValidateAsync(seriesId, _userId);

        report.Result!.ChainCompleteness.Should().BeTrue();
    }

    // ── Validator 4: No Circular Dependencies ─────────────────────────────────

    [Fact]
    public async Task Validate_FailsWhenEarlyActionDependsOnLaterAction()
    {
        // actionA is order 1, actionB is order 2 -- but actionA.Dependencies points
        // to actionB. That's a circular dep in series order.
        var actionA = Guid.NewGuid();
        var actionB = Guid.NewGuid();
        SetupActionDef(actionA, dependsOn: new[] { actionB });
        SetupActionDef(actionB);

        var create = await _manager.CreateAsync(_userId, new DappSeriesCreateModel { Name = "Cycle" });
        var seriesId = create.Result!.IdGuid;
        await _manager.AddActionAsync(seriesId, _userId, new DappSeriesAddActionModel { ActionId = actionA, Order = 1 });
        await _manager.AddActionAsync(seriesId, _userId, new DappSeriesAddActionModel { ActionId = actionB, Order = 2 });
        SetupLatestRunStatus(actionA, ActionRunStatus.Succeeded);
        SetupLatestRunStatus(actionB, ActionRunStatus.Succeeded);

        var report = await _manager.ValidateAsync(seriesId, _userId);

        report.Result!.NoCircularDependencies.Should().BeFalse();
        report.Result.Diagnostics.Should().Contain(d => d.Contains("appears later"));
    }

    // ── Validator 5: Holon Bindings Resolved ──────────────────────────────────

    [Fact]
    public async Task Validate_FailsWhenReferencedHolonDoesNotExist()
    {
        var orphanHolon = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        SetupActionDef(actionId, nodes: new[]
        {
            new ActionNodeDef { Id = Guid.NewGuid(), ActionId = actionId,
                Config = JsonSerializer.Serialize(new { holonId = orphanHolon.ToString() }) }
        });

        var create = await _manager.CreateAsync(_userId, new DappSeriesCreateModel { Name = "Holon ref" });
        var seriesId = create.Result!.IdGuid;
        await _manager.AddActionAsync(seriesId, _userId, new DappSeriesAddActionModel { ActionId = actionId, Order = 1 });
        SetupLatestRunStatus(actionId, ActionRunStatus.Succeeded);

        _holonStore.Setup(s => s.GetByIdAsync(orphanHolon, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IHolon> { IsError = true, Message = "not found" });

        var report = await _manager.ValidateAsync(seriesId, _userId);

        report.Result!.HolonBindingsResolved.Should().BeFalse();
        report.Result.Diagnostics.Should().Contain(d => d.Contains(orphanHolon.ToString()));
    }

    // ── FR-5 / AC-5: holon-ref scanner covers array-valued *holon* properties ──

    [Fact]
    public async Task Validate_FailsWhenHolonReferencedViaArrayProperty_DoesNotExist()
    {
        // GateCheckNodeConfig.Holons serialises as "holons": ["<guid>", ...].
        // Before FR-5, ScanForGuids only picked up scalar string values under
        // *holon*-named keys, so this reference was silently ignored and
        // HolonBindingsResolved would incorrectly be true. After the fix, the
        // array elements are also scanned and the missing holon is caught.
        var missingHolonId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        SetupActionDef(actionId, nodes: new[]
        {
            new ActionNodeDef
            {
                Id = Guid.NewGuid(),
                ActionId = actionId,
                NodeType = ActionNodeType.GateCheck,
                // Simulate GateCheckNodeConfig serialised: array-valued "holons" key.
                Config = JsonSerializer.Serialize(new
                {
                    predicate = "true",
                    reads     = new { },
                    holons    = new[] { missingHolonId.ToString() },
                }),
            },
        });

        var create = await _manager.CreateAsync(_userId, new DappSeriesCreateModel { Name = "GateCheck array holon" });
        var seriesId = create.Result!.IdGuid;
        await _manager.AddActionAsync(seriesId, _userId, new DappSeriesAddActionModel { ActionId = actionId, Order = 1 });
        SetupLatestRunStatus(actionId, ActionRunStatus.Succeeded);

        _holonStore.Setup(s => s.GetByIdAsync(missingHolonId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IHolon> { IsError = true, Message = "not found" });

        var report = await _manager.ValidateAsync(seriesId, _userId);

        report.Result!.HolonBindingsResolved.Should().BeFalse(
            "a Guid inside a *holon*-named array property must be treated as a holon reference (FR-5 / AC-5)");
        report.Result.Diagnostics.Should().Contain(
            d => d.Contains(missingHolonId.ToString()),
            "the missing holon id must appear in diagnostics");
    }

    // ── Compose + Generate + Deploy pipeline ──────────────────────────────────

    [Fact]
    public async Task DeployAsync_RejectsWhenSeriesIsNotReady()
    {
        var create = await _manager.CreateAsync(_userId, new DappSeriesCreateModel { Name = "X" });
        var seriesId = create.Result!.IdGuid;

        var deploy = await _manager.DeployAsync(seriesId, _userId);

        deploy.IsError.Should().BeTrue();
        deploy.Message.Should().Contain("Ready");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupActionDef(
        Guid actionId,
        IEnumerable<Guid>? dependsOn = null,
        IEnumerable<ActionNodeDef>? nodes = null)
    {
        var action = new ActionDef
        {
            Id = actionId,
            Name = $"Action {actionId:N}",
            UserId = _userId,
            Nodes = nodes?.ToList() ?? new List<ActionNodeDef>(),
            Dependencies = (dependsOn ?? Array.Empty<Guid>())
                .Select(d => new ActionDependencyDef { Id = Guid.NewGuid(), ActionId = actionId, DependsOnActionId = d })
                .ToList(),
        };
        _actionStore.Setup(s => s.GetActionAsync(actionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ActionDef> { Result = action });
    }

    private void SetupLatestRunStatus(Guid actionId, ActionRunStatus status)
    {
        var run = new ActionRunDef
        {
            Id = Guid.NewGuid(),
            ActionId = actionId,
            UserId = _userId,
            Status = status,
            StartedAt = DateTime.UtcNow,
        };
        _runStore.Setup(s => s.GetByActionIdAsync(actionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<ActionRunDef>> { Result = new[] { run } });
    }

    private async Task<(Guid seriesId, Guid actionId)> SetupSeriesWithOneAction()
    {
        var actionId = Guid.NewGuid();
        SetupActionDef(actionId);
        var create = await _manager.CreateAsync(_userId, new DappSeriesCreateModel { Name = "One action" });
        var seriesId = create.Result!.IdGuid;
        await _manager.AddActionAsync(seriesId, _userId, new DappSeriesAddActionModel { ActionId = actionId, Order = 1 });
        return (seriesId, actionId);
    }

    private async Task<(Guid, Guid, Guid)> SetupTwoActionSeriesWithoutDependency()
    {
        var actionA = Guid.NewGuid();
        var actionB = Guid.NewGuid();
        SetupActionDef(actionA);
        SetupActionDef(actionB); // No dependency on actionA -- this is the failure case.
        var create = await _manager.CreateAsync(_userId, new DappSeriesCreateModel { Name = "Two-no-dep" });
        var seriesId = create.Result!.IdGuid;
        await _manager.AddActionAsync(seriesId, _userId, new DappSeriesAddActionModel { ActionId = actionA, Order = 1 });
        await _manager.AddActionAsync(seriesId, _userId, new DappSeriesAddActionModel { ActionId = actionB, Order = 2 });
        return (seriesId, actionA, actionB);
    }

    private async Task<(Guid, Guid, Guid)> SetupTwoActionSeriesWithDependency()
    {
        var actionA = Guid.NewGuid();
        var actionB = Guid.NewGuid();
        SetupActionDef(actionA);
        SetupActionDef(actionB, dependsOn: new[] { actionA });
        var create = await _manager.CreateAsync(_userId, new DappSeriesCreateModel { Name = "Two-with-dep" });
        var seriesId = create.Result!.IdGuid;
        await _manager.AddActionAsync(seriesId, _userId, new DappSeriesAddActionModel { ActionId = actionA, Order = 1 });
        await _manager.AddActionAsync(seriesId, _userId, new DappSeriesAddActionModel { ActionId = actionB, Order = 2 });
        return (seriesId, actionA, actionB);
    }
}
