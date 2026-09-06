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
using ActionRunDef = AZOA.WebAPI.Models.Actions.ActionRun;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// Regression tests for the duplicate-key 500s in DappCompositionManager:
/// AddActionAsync must reject a second add of the same action, and ValidateAsync
/// must surface a clean error (not throw ArgumentException) if pre-existing
/// duplicate (series, action) rows already exist in the store.
/// </summary>
public class DappCompositionDuplicateActionTests
{
    private readonly InMemoryDappSeriesStore _seriesStore = new();
    private readonly Mock<IActionStore> _actionStore = new();
    private readonly Mock<IActionRunStore> _runStore = new();
    private readonly Mock<IHolonStore> _holonStore = new();
    private readonly Mock<ISTARManager> _starManager = new();
    private readonly DappCompositionManager _manager;
    private readonly Guid _userId = Guid.NewGuid();

    public DappCompositionDuplicateActionTests()
    {
        _manager = new DappCompositionManager(
            _seriesStore, _actionStore.Object, _runStore.Object, _holonStore.Object, _starManager.Object);
    }

    [Fact]
    public async Task AddActionAsync_RejectsAddingSameActionTwice()
    {
        var actionId = Guid.NewGuid();
        SetupActionDef(actionId);
        var seriesId = (await _manager.CreateAsync(_userId, new DappSeriesCreateModel { Name = "Dup" })).Result!.IdGuid;

        var first = await _manager.AddActionAsync(seriesId, _userId, new DappSeriesAddActionModel { ActionId = actionId, Order = 1 });
        var second = await _manager.AddActionAsync(seriesId, _userId, new DappSeriesAddActionModel { ActionId = actionId, Order = 2 });

        first.IsError.Should().BeFalse();
        second.IsError.Should().BeTrue();
        second.Message.Should().Contain("already in series");
    }

    [Fact]
    public async Task ValidateAsync_WithPreExistingDuplicateRows_ReturnsErrorNotException()
    {
        var actionId = Guid.NewGuid();
        SetupActionDef(actionId);
        var seriesId = (await _manager.CreateAsync(_userId, new DappSeriesCreateModel { Name = "Corrupt" })).Result!.IdGuid;

        // Bypass AddActionAsync's guard to simulate two duplicate rows already
        // persisted (the corrupt-state the ToDictionary sites used to crash on).
        await _seriesStore.UpsertSeriesActionAsync(DappSeriesAction.NewEntry(seriesId, actionId, 1));
        await _seriesStore.UpsertSeriesActionAsync(DappSeriesAction.NewEntry(seriesId, actionId, 2));
        SetupLatestRunStatus(actionId, ActionRunStatus.Succeeded);

        var report = await _manager.ValidateAsync(seriesId, _userId);

        report.IsError.Should().BeFalse();
        report.Result!.IsValid.Should().BeFalse();
        report.Result.Diagnostics.Should().Contain(d => d.Contains("more than once"));
    }

    private void SetupActionDef(Guid actionId)
    {
        var action = new ActionDef { Id = actionId, Name = $"Action {actionId:N}", UserId = _userId };
        _actionStore.Setup(s => s.GetActionAsync(actionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<ActionDef> { Result = action });
    }

    private void SetupLatestRunStatus(Guid actionId, ActionRunStatus status)
    {
        var run = new ActionRunDef { Id = Guid.NewGuid(), ActionId = actionId, UserId = _userId, Status = status, StartedAt = DateTime.UtcNow };
        _runStore.Setup(s => s.GetByActionIdAsync(actionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<ActionRunDef>> { Result = new[] { run } });
    }
}
