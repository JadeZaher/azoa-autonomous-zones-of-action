using FluentAssertions;
using Moq;
using AZOA.WebAPI.Persistence.SurrealDb.Models;
using AZOA.WebAPI.Interfaces;
using AZOA.WebAPI.Interfaces.Managers;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using QuestDef = AZOA.WebAPI.Models.Quest.Quest;
using QuestRunDef = AZOA.WebAPI.Models.Quest.QuestRun;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// Regression tests for the duplicate-key 500s in DappCompositionManager:
/// AddQuestAsync must reject a second add of the same quest, and ValidateAsync
/// must surface a clean error (not throw ArgumentException) if pre-existing
/// duplicate (series, quest) rows already exist in the store.
/// </summary>
public class DappCompositionDuplicateQuestTests
{
    private readonly InMemoryDappSeriesStore _seriesStore = new();
    private readonly Mock<IQuestStore> _questStore = new();
    private readonly Mock<IQuestRunStore> _runStore = new();
    private readonly Mock<IHolonStore> _holonStore = new();
    private readonly Mock<ISTARManager> _starManager = new();
    private readonly DappCompositionManager _manager;
    private readonly Guid _avatarId = Guid.NewGuid();

    public DappCompositionDuplicateQuestTests()
    {
        _manager = new DappCompositionManager(
            _seriesStore, _questStore.Object, _runStore.Object, _holonStore.Object, _starManager.Object);
    }

    [Fact]
    public async Task AddQuestAsync_RejectsAddingSameQuestTwice()
    {
        var questId = Guid.NewGuid();
        SetupQuestDef(questId);
        var seriesId = (await _manager.CreateAsync(_avatarId, new DappSeriesCreateModel { Name = "Dup" })).Result!.IdGuid;

        var first = await _manager.AddQuestAsync(seriesId, _avatarId, new DappSeriesAddQuestModel { QuestId = questId, Order = 1 });
        var second = await _manager.AddQuestAsync(seriesId, _avatarId, new DappSeriesAddQuestModel { QuestId = questId, Order = 2 });

        first.IsError.Should().BeFalse();
        second.IsError.Should().BeTrue();
        second.Message.Should().Contain("already in series");
    }

    [Fact]
    public async Task ValidateAsync_WithPreExistingDuplicateRows_ReturnsErrorNotException()
    {
        var questId = Guid.NewGuid();
        SetupQuestDef(questId);
        var seriesId = (await _manager.CreateAsync(_avatarId, new DappSeriesCreateModel { Name = "Corrupt" })).Result!.IdGuid;

        // Bypass AddQuestAsync's guard to simulate two duplicate rows already
        // persisted (the corrupt-state the ToDictionary sites used to crash on).
        await _seriesStore.UpsertSeriesQuestAsync(DappSeriesQuest.NewEntry(seriesId, questId, 1));
        await _seriesStore.UpsertSeriesQuestAsync(DappSeriesQuest.NewEntry(seriesId, questId, 2));
        SetupLatestRunStatus(questId, QuestRunStatus.Succeeded);

        var report = await _manager.ValidateAsync(seriesId, _avatarId);

        report.IsError.Should().BeFalse();
        report.Result!.IsValid.Should().BeFalse();
        report.Result.Diagnostics.Should().Contain(d => d.Contains("more than once"));
    }

    private void SetupQuestDef(Guid questId)
    {
        var quest = new QuestDef { Id = questId, Name = $"Quest {questId:N}", AvatarId = _avatarId };
        _questStore.Setup(s => s.GetQuestAsync(questId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<QuestDef> { Result = quest });
    }

    private void SetupLatestRunStatus(Guid questId, QuestRunStatus status)
    {
        var run = new QuestRunDef { Id = Guid.NewGuid(), QuestId = questId, AvatarId = _avatarId, Status = status, StartedAt = DateTime.UtcNow };
        _runStore.Setup(s => s.GetByQuestIdAsync(questId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<QuestRunDef>> { Result = new[] { run } });
    }
}
