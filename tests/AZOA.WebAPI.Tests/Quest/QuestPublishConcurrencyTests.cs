using FluentAssertions;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Services.Quest.Handlers;
using AZOA.WebAPI.Tests.Fakes;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// F6 unpublish-TOCTOU guard: optimistic-concurrency (version) compare-and-swap on
/// the definition lifecycle. Covers the three race classes named in
/// Managers/AGENTS.md §publish-lifecycle:
///   (a) unpublish racing a run-start,
///   (b) double-publish,
///   (c) stale-version write rejected.
/// Uses the shared <see cref="InMemoryQuestStore"/> whose CAS mirrors the
/// SurrealQuestStore conditional-UPDATE semantics (affected count VERBATIM).
/// </summary>
public class QuestPublishConcurrencyTests
{
    private static readonly Guid AvatarId = Guid.NewGuid();

    private static QuestManager BuildManager(IQuestStore questStore, InMemoryQuestRunStore runStore)
        => new(
            questStore,
            runStore,
            new InMemoryQuestNodeExecutionStore(),
            new QuestDagValidator(),
            new QuestNodeHandlerRegistry(new IQuestNodeHandler[] { new ConditionNodeHandler() }),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough());

    /// <summary>Minimal valid linear 2-node quest in Draft (version 0).</summary>
    private static QuestEntity ValidDraftQuest()
    {
        var entryId    = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        var qid        = Guid.NewGuid();
        return new QuestEntity
        {
            Id       = qid,
            Name     = "Concurrency",
            AvatarId = AvatarId,
            Status   = QuestStatus.Draft,
            Version  = 0,
            Nodes = new List<QuestNode>
            {
                new() { Id = entryId,    Name = "Entry",    NodeType = QuestNodeType.Condition,
                        IsEntry = true, IsTerminal = false, Config = "{}" },
                new() { Id = terminalId, Name = "Terminal", NodeType = QuestNodeType.Condition,
                        IsEntry = false, IsTerminal = true, Config = "{}" },
            },
            Edges = new List<QuestEdge>
            {
                new() { Id = Guid.NewGuid(), QuestId = qid,
                        SourceNodeId = entryId, TargetNodeId = terminalId,
                        EdgeType = QuestEdgeType.Control },
            }
        };
    }

    // ─── (b) double-publish: exactly one wins, the other gets a conflict ───────

    [Fact]
    public async Task TwoConcurrentPublishes_ExactlyOneWins_OtherConflicts()
    {
        var quest = ValidDraftQuest();
        var store = new InMemoryQuestStore();
        await store.UpsertQuestAsync(quest);

        // Two managers over the SAME store race PublishAsync. Both read version 0
        // + Draft; the CAS is the serialization point.
        var mgrA = BuildManager(store, new InMemoryQuestRunStore());
        var mgrB = BuildManager(store, new InMemoryQuestRunStore());

        var results = await Task.WhenAll(
            mgrA.PublishAsync(quest.Id, quest.AvatarId),
            mgrB.PublishAsync(quest.Id, quest.AvatarId));

        var winners = results.Count(r => !r.IsError);
        var losers  = results.Count(r => r.IsError);

        winners.Should().Be(1, "exactly one publish CAS wins the Draft→Active transition");
        losers.Should().Be(1, "the loser is rejected — never a torn double-publish");
        // The loser is rejected via EITHER the early already-Active pre-check (it
        // re-read the winner's committed Active row) OR the CAS conflict (both read
        // Draft, then the loser's compare-and-swap missed). Both are correct "lost
        // the race" outcomes; the invariant is that it does NOT succeed.
        var loserMsg = results.Single(r => r.IsError).Message ?? string.Empty;
        loserMsg.Should().Match(m =>
            m.Contains("conflict", StringComparison.OrdinalIgnoreCase)
            || m.Contains("already Active", StringComparison.OrdinalIgnoreCase));

        // The row is Active exactly once, version bumped exactly once.
        var reloaded = (await store.GetQuestAsync(quest.Id)).Result!;
        reloaded.Status.Should().Be(QuestStatus.Active);
        reloaded.Version.Should().Be(1, "a single winning CAS bumps version once (no double-bump)");
    }

    // ─── (c) stale-version write is rejected ──────────────────────────────────

    [Fact]
    public async Task StaleVersionUnpublish_IsRejected()
    {
        var quest = ValidDraftQuest();
        var store = new InMemoryQuestStore();
        await store.UpsertQuestAsync(quest);
        var manager = BuildManager(store, new InMemoryQuestRunStore());

        // Publish moves Draft(v0) → Active(v1).
        var published = await manager.PublishAsync(quest.Id, quest.AvatarId);
        published.IsError.Should().BeFalse();

        // Simulate a stale reader: attempt a direct CAS unpublish at the OLD version 0.
        var staleAffected = await store.TryTransitionQuestStatusAsync(
            quest.Id, QuestStatus.Active, QuestStatus.Draft, expectedVersion: 0);
        staleAffected.Should().Be(0, "a CAS at a stale version must not match");

        // The definition is untouched by the stale attempt.
        var reloaded = (await store.GetQuestAsync(quest.Id)).Result!;
        reloaded.Status.Should().Be(QuestStatus.Active);
        reloaded.Version.Should().Be(1);

        // A fresh-version unpublish still succeeds.
        var unpublished = await manager.UnpublishAsync(quest.Id, quest.AvatarId);
        unpublished.IsError.Should().BeFalse("unpublish at the current version wins");
        unpublished.Result!.Status.Should().Be(QuestStatus.Draft);
        unpublished.Result.Version.Should().Be(2);
    }

    // ─── (a) unpublish racing run-start: run-start never tears ─────────────────

    [Fact]
    public async Task RunStart_AfterConcurrentUnpublish_SeesConflict_NotTornState()
    {
        var quest = ValidDraftQuest();
        var store = new InMemoryQuestStore();
        await store.UpsertQuestAsync(quest);
        var runStore = new InMemoryQuestRunStore();
        var manager  = BuildManager(store, runStore);

        // Publish → Active(v1).
        await manager.PublishAsync(quest.Id, quest.AvatarId);

        // An unpublish commits FIRST (no in-flight runs), moving Active(v1) →
        // Draft(v2). A run-start that had already read Active at v1 must NOT
        // execute against the now-Draft definition.
        var unpublished = await manager.UnpublishAsync(quest.Id, quest.AvatarId);
        unpublished.IsError.Should().BeFalse();

        // Run-start now re-reads Draft and is rejected by the Active gate — no run
        // row is created.
        var run = await manager.ExecuteAsync(quest.Id, quest.AvatarId);
        run.IsError.Should().BeTrue("a Draft quest cannot start a run");
        (await runStore.GetByQuestIdAsync(quest.Id)).Result!.Should().BeEmpty(
            "no torn run row is created when the definition was unpublished");
    }

    [Fact]
    public async Task RunStartConfirm_MissesAfterVersionMoves()
    {
        // Directly exercise the run-start confirm primitive against a moved version:
        // this is the exact seam ExecuteAsync/StartWorkflowRunAsync use to close the
        // unpublish-vs-run-start race.
        var quest = ValidDraftQuest();
        var store = new InMemoryQuestStore();
        await store.UpsertQuestAsync(quest);

        // Reader captured Active at v1.
        await store.TryTransitionQuestStatusAsync(quest.Id, QuestStatus.Draft, QuestStatus.Active, 0);
        var confirmAtV1 = await store.TryConfirmQuestStateAsync(quest.Id, QuestStatus.Active, expectedVersion: 1);
        confirmAtV1.Should().Be(1, "confirm at the read version passes while the row is unchanged");

        // A concurrent unpublish bumps the version.
        await store.TryTransitionQuestStatusAsync(quest.Id, QuestStatus.Active, QuestStatus.Draft, 1);

        // The stale reader's confirm now misses — run-start would reject.
        var confirmStale = await store.TryConfirmQuestStateAsync(quest.Id, QuestStatus.Active, expectedVersion: 1);
        confirmStale.Should().Be(0, "the definition moved since the reader captured v1 — confirm must miss");
    }
}
