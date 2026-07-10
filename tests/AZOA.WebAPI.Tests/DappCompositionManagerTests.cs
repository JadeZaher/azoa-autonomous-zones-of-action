using System.Text.Json;
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
using QuestNodeDef = AZOA.WebAPI.Models.Quest.QuestNode;
using QuestRunDef = AZOA.WebAPI.Models.Quest.QuestRun;
using QuestDependencyDef = AZOA.WebAPI.Models.Quest.QuestDependency;

namespace AZOA.WebAPI.Tests;

/// <summary>
/// Manager-level tests for the dapp-composition slice. Covers the 5
/// composition validation rules from spec.md, status-machine guardrails
/// (delete in non-Draft, deploy without Generate, etc.), and avatar
/// scoping.
/// </summary>
public class DappCompositionManagerTests
{
    private readonly InMemoryDappSeriesStore _seriesStore = new();
    private readonly Mock<IQuestStore> _questStore = new();
    private readonly Mock<IQuestRunStore> _runStore = new();
    private readonly Mock<IHolonStore> _holonStore = new();
    private readonly Mock<ISTARManager> _starManager = new();
    private readonly DappCompositionManager _manager;
    private readonly Guid _avatarId = Guid.NewGuid();

    public DappCompositionManagerTests()
    {
        _manager = new DappCompositionManager(
            _seriesStore, _questStore.Object, _runStore.Object, _holonStore.Object, _starManager.Object);
    }

    // ── Series CRUD ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ShouldStartInDraftStatus()
    {
        var result = await _manager.CreateAsync(_avatarId,
            new DappSeriesCreateModel { Name = "My dApp" });

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(DappSeries.StatusKind.Draft);
        result.Result.AvatarIdGuid.Should().Be(_avatarId);
        result.Result.Name.Should().Be("My dApp");
    }

    [Fact]
    public async Task CreateAsync_RejectsEmptyName()
    {
        var result = await _manager.CreateAsync(_avatarId, new DappSeriesCreateModel { Name = "" });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Name");
    }

    [Fact]
    public async Task GetAsync_RejectsCrossAvatarAccess()
    {
        var create = await _manager.CreateAsync(_avatarId, new DappSeriesCreateModel { Name = "Owned" });
        var seriesId = create.Result!.IdGuid;

        var otherAvatar = Guid.NewGuid();
        var get = await _manager.GetAsync(seriesId, otherAvatar);

        get.IsError.Should().BeTrue();
        get.Message.Should().Contain("Forbidden");
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlyTheCallingAvatarsSeries()
    {
        var otherAvatar = Guid.NewGuid();
        var mine = await _manager.CreateAsync(_avatarId, new DappSeriesCreateModel { Name = "Mine" });
        await _manager.CreateAsync(otherAvatar, new DappSeriesCreateModel { Name = "Theirs" });

        var list = await _manager.ListAsync(_avatarId);

        list.IsError.Should().BeFalse();
        list.Result.Should().ContainSingle(s => s.IdGuid == mine.Result!.IdGuid);
        list.Result.Should().NotContain(s => s.AvatarIdGuid == otherAvatar);
    }

    [Fact]
    public async Task DeleteAsync_BlocksDeletionOfReadySeries()
    {
        var create = await _manager.CreateAsync(_avatarId, new DappSeriesCreateModel { Name = "Promote" });
        var seriesId = create.Result!.IdGuid;
        create.Result.Status = DappSeries.StatusKind.Ready;
        await _seriesStore.UpsertSeriesAsync(create.Result);

        var del = await _manager.DeleteAsync(seriesId, _avatarId);

        del.IsError.Should().BeTrue();
        del.Message.Should().Contain("archive");
    }

    [Fact]
    public async Task UpdateAsync_RejectsCrossAvatarWrites()
    {
        var create = await _manager.CreateAsync(_avatarId, new DappSeriesCreateModel { Name = "Owned" });
        var seriesId = create.Result!.IdGuid;

        var result = await _manager.UpdateAsync(seriesId, Guid.NewGuid(), new DappSeriesUpdateModel
        {
            Name = "Intruder edit",
        });

        result.IsError.Should().BeTrue();
        result.Message.Should().Contain("Forbidden");
    }

    // ── Validator 1: All Quests Completed ─────────────────────────────────────

    [Fact]
    public async Task Validate_FailsWhenLatestRunIsNotSucceeded()
    {
        var (seriesId, questId) = await SetupSeriesWithOneQuest();
        SetupLatestRunStatus(questId, QuestRunStatus.Failed);

        var report = await _manager.ValidateAsync(seriesId, _avatarId);

        report.Result!.AllQuestsCompleted.Should().BeFalse();
        report.Result.Diagnostics.Should().Contain(d => d.Contains("Failed"));
    }

    [Fact]
    public async Task Validate_FailsWhenQuestHasNoRunsAtAll()
    {
        var (seriesId, questId) = await SetupSeriesWithOneQuest();
        _runStore.Setup(s => s.GetByQuestIdAsync(questId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<QuestRunDef>> { Result = Array.Empty<QuestRunDef>() });

        var report = await _manager.ValidateAsync(seriesId, _avatarId);

        report.Result!.AllQuestsCompleted.Should().BeFalse();
        report.Result.Diagnostics.Should().Contain(d => d.Contains("never been executed"));
    }

    [Fact]
    public async Task Validate_PassesAllQuestsCompletedWhenLatestRunSucceeded()
    {
        var (seriesId, questId) = await SetupSeriesWithOneQuest();
        SetupLatestRunStatus(questId, QuestRunStatus.Succeeded);

        var report = await _manager.ValidateAsync(seriesId, _avatarId);

        report.Result!.AllQuestsCompleted.Should().BeTrue();
    }

    // ── Validator 2: Chain Completeness ───────────────────────────────────────

    [Fact]
    public async Task Validate_FailsWhenSecondQuestHasNoPriorDependency()
    {
        var (seriesId, questA, questB) = await SetupTwoQuestSeriesWithoutDependency();
        SetupLatestRunStatus(questA, QuestRunStatus.Succeeded);
        SetupLatestRunStatus(questB, QuestRunStatus.Succeeded);

        var report = await _manager.ValidateAsync(seriesId, _avatarId);

        report.Result!.ChainCompleteness.Should().BeFalse();
        report.Result.Diagnostics.Should().Contain(d => d.Contains("no QuestDependency"));
    }

    [Fact]
    public async Task Validate_PassesChainWhenSecondQuestDependsOnFirst()
    {
        var (seriesId, questA, questB) = await SetupTwoQuestSeriesWithDependency();
        SetupLatestRunStatus(questA, QuestRunStatus.Succeeded);
        SetupLatestRunStatus(questB, QuestRunStatus.Succeeded);

        var report = await _manager.ValidateAsync(seriesId, _avatarId);

        report.Result!.ChainCompleteness.Should().BeTrue();
    }

    // ── Validator 4: No Circular Dependencies ─────────────────────────────────

    [Fact]
    public async Task Validate_FailsWhenEarlyQuestDependsOnLaterQuest()
    {
        // questA is order 1, questB is order 2 -- but questA.Dependencies points
        // to questB. That's a circular dep in series order.
        var questA = Guid.NewGuid();
        var questB = Guid.NewGuid();
        SetupQuestDef(questA, dependsOn: new[] { questB });
        SetupQuestDef(questB);

        var create = await _manager.CreateAsync(_avatarId, new DappSeriesCreateModel { Name = "Cycle" });
        var seriesId = create.Result!.IdGuid;
        await _manager.AddQuestAsync(seriesId, _avatarId, new DappSeriesAddQuestModel { QuestId = questA, Order = 1 });
        await _manager.AddQuestAsync(seriesId, _avatarId, new DappSeriesAddQuestModel { QuestId = questB, Order = 2 });
        SetupLatestRunStatus(questA, QuestRunStatus.Succeeded);
        SetupLatestRunStatus(questB, QuestRunStatus.Succeeded);

        var report = await _manager.ValidateAsync(seriesId, _avatarId);

        report.Result!.NoCircularDependencies.Should().BeFalse();
        report.Result.Diagnostics.Should().Contain(d => d.Contains("appears later"));
    }

    // ── Validator 5: Holon Bindings Resolved ──────────────────────────────────

    [Fact]
    public async Task Validate_FailsWhenReferencedHolonDoesNotExist()
    {
        var orphanHolon = Guid.NewGuid();
        var questId = Guid.NewGuid();
        SetupQuestDef(questId, nodes: new[]
        {
            new QuestNodeDef { Id = Guid.NewGuid(), QuestId = questId,
                Config = JsonSerializer.Serialize(new { holonId = orphanHolon.ToString() }) }
        });

        var create = await _manager.CreateAsync(_avatarId, new DappSeriesCreateModel { Name = "Holon ref" });
        var seriesId = create.Result!.IdGuid;
        await _manager.AddQuestAsync(seriesId, _avatarId, new DappSeriesAddQuestModel { QuestId = questId, Order = 1 });
        SetupLatestRunStatus(questId, QuestRunStatus.Succeeded);

        _holonStore.Setup(s => s.GetByIdAsync(orphanHolon, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IHolon> { IsError = true, Message = "not found" });

        var report = await _manager.ValidateAsync(seriesId, _avatarId);

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
        var questId = Guid.NewGuid();
        SetupQuestDef(questId, nodes: new[]
        {
            new QuestNodeDef
            {
                Id = Guid.NewGuid(),
                QuestId = questId,
                NodeType = QuestNodeType.GateCheck,
                // Simulate GateCheckNodeConfig serialised: array-valued "holons" key.
                Config = JsonSerializer.Serialize(new
                {
                    predicate = "true",
                    reads     = new { },
                    holons    = new[] { missingHolonId.ToString() },
                }),
            },
        });

        var create = await _manager.CreateAsync(_avatarId, new DappSeriesCreateModel { Name = "GateCheck array holon" });
        var seriesId = create.Result!.IdGuid;
        await _manager.AddQuestAsync(seriesId, _avatarId, new DappSeriesAddQuestModel { QuestId = questId, Order = 1 });
        SetupLatestRunStatus(questId, QuestRunStatus.Succeeded);

        _holonStore.Setup(s => s.GetByIdAsync(missingHolonId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IHolon> { IsError = true, Message = "not found" });

        var report = await _manager.ValidateAsync(seriesId, _avatarId);

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
        var create = await _manager.CreateAsync(_avatarId, new DappSeriesCreateModel { Name = "X" });
        var seriesId = create.Result!.IdGuid;

        var deploy = await _manager.DeployAsync(seriesId, _avatarId);

        deploy.IsError.Should().BeTrue();
        deploy.Message.Should().Contain("Ready");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupQuestDef(
        Guid questId,
        IEnumerable<Guid>? dependsOn = null,
        IEnumerable<QuestNodeDef>? nodes = null)
    {
        var quest = new QuestDef
        {
            Id = questId,
            Name = $"Quest {questId:N}",
            AvatarId = _avatarId,
            Nodes = nodes?.ToList() ?? new List<QuestNodeDef>(),
            Dependencies = (dependsOn ?? Array.Empty<Guid>())
                .Select(d => new QuestDependencyDef { Id = Guid.NewGuid(), QuestId = questId, DependsOnQuestId = d })
                .ToList(),
        };
        _questStore.Setup(s => s.GetQuestAsync(questId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<QuestDef> { Result = quest });
    }

    private void SetupLatestRunStatus(Guid questId, QuestRunStatus status)
    {
        var run = new QuestRunDef
        {
            Id = Guid.NewGuid(),
            QuestId = questId,
            AvatarId = _avatarId,
            Status = status,
            StartedAt = DateTime.UtcNow,
        };
        _runStore.Setup(s => s.GetByQuestIdAsync(questId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AZOAResult<IEnumerable<QuestRunDef>> { Result = new[] { run } });
    }

    private async Task<(Guid seriesId, Guid questId)> SetupSeriesWithOneQuest()
    {
        var questId = Guid.NewGuid();
        SetupQuestDef(questId);
        var create = await _manager.CreateAsync(_avatarId, new DappSeriesCreateModel { Name = "One quest" });
        var seriesId = create.Result!.IdGuid;
        await _manager.AddQuestAsync(seriesId, _avatarId, new DappSeriesAddQuestModel { QuestId = questId, Order = 1 });
        return (seriesId, questId);
    }

    private async Task<(Guid, Guid, Guid)> SetupTwoQuestSeriesWithoutDependency()
    {
        var questA = Guid.NewGuid();
        var questB = Guid.NewGuid();
        SetupQuestDef(questA);
        SetupQuestDef(questB); // No dependency on questA -- this is the failure case.
        var create = await _manager.CreateAsync(_avatarId, new DappSeriesCreateModel { Name = "Two-no-dep" });
        var seriesId = create.Result!.IdGuid;
        await _manager.AddQuestAsync(seriesId, _avatarId, new DappSeriesAddQuestModel { QuestId = questA, Order = 1 });
        await _manager.AddQuestAsync(seriesId, _avatarId, new DappSeriesAddQuestModel { QuestId = questB, Order = 2 });
        return (seriesId, questA, questB);
    }

    private async Task<(Guid, Guid, Guid)> SetupTwoQuestSeriesWithDependency()
    {
        var questA = Guid.NewGuid();
        var questB = Guid.NewGuid();
        SetupQuestDef(questA);
        SetupQuestDef(questB, dependsOn: new[] { questA });
        var create = await _manager.CreateAsync(_avatarId, new DappSeriesCreateModel { Name = "Two-with-dep" });
        var seriesId = create.Result!.IdGuid;
        await _manager.AddQuestAsync(seriesId, _avatarId, new DappSeriesAddQuestModel { QuestId = questA, Order = 1 });
        await _manager.AddQuestAsync(seriesId, _avatarId, new DappSeriesAddQuestModel { QuestId = questB, Order = 2 });
        return (seriesId, questA, questB);
    }
}
