using FluentAssertions;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Services.Quest.Handlers;
using AZOA.WebAPI.Tests.Fakes;
using Moq;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// quest-invitations-approval AC1/AC2/AC4/AC5(fork). Exercises the run-start
/// invite gate (LoadStartableQuestAsync chokepoint, shared by ExecuteAsync +
/// StartWorkflowRunAsync) through the public <see cref="QuestManager.ExecuteAsync"/>
/// seam, plus owner-side InviteAvatarAsync/RevokeInviteAsync and the fork IDOR
/// path. Real InMemory stores so the load-mutate-save round trips persist between
/// manager calls; the access-request store is Mocked (no InMemory impl exists).
///
/// Gate assertion strategy: a REJECTED start surfaces the actionable invitation
/// message AND writes no run row; a PERMITTED start writes a run row (proving the
/// gate let it through) — robust regardless of downstream node-execution outcome.
/// </summary>
public class QuestInvitationsTests
{
    private static readonly Guid Owner    = Guid.NewGuid();
    private static readonly Guid Outsider = Guid.NewGuid();
    private static readonly Guid Invited  = Guid.NewGuid();

    /// <summary>The invitation-required rejection is worded around "invitation".</summary>
    private const string InviteRejectionFragment = "invitation";

    // ─── Scaffolding ───

    private static (QuestManager manager, InMemoryQuestStore questStore, InMemoryQuestRunStore runStore)
        Build(Mock<IQuestAccessRequestStore>? accessStore = null)
    {
        var questStore = new InMemoryQuestStore();
        var runStore = new InMemoryQuestRunStore();
        var execStore = new InMemoryQuestNodeExecutionStore();

        var manager = new QuestManager(
            questStore,
            runStore,
            execStore,
            new QuestDagValidator(),
            new QuestDagExecutabilityValidator(),
            // Register the Condition handler so the economic-consent gate classifies
            // the test's Condition node as NON-economic (fail-closed IsEconomicNode
            // treats an UNregistered type as economic — see QuestEconomicManifestBuilder).
            new QuestNodeHandlerRegistry(new IQuestNodeHandler[] { new ConditionNodeHandler() }),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough(),
            configuration: null,
            accessRequestStore: (accessStore ?? new Mock<IQuestAccessRequestStore>()).Object);

        return (manager, questStore, runStore);
    }

    /// <summary>
    /// A published (public + Active) single-node quest owned by <see cref="Owner"/>.
    /// One Condition node that is both entry and terminal — the minimal DAG that
    /// passes structural validation and never trips the Tier-2 capability gate.
    /// </summary>
    private static QuestEntity PublishedQuest(QuestRunAccess runAccess, params Guid[] invited)
    {
        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Gated",
            AvatarId = Owner,
            IsPublic = true,
            Status = QuestStatus.Active,
            Version = 1,
            PublishedVersionHash = "hash-v1",
            RunAccess = runAccess,
            InvitedAvatarIds = invited.ToList(),
            Nodes = new List<QuestNode>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Name = "only",
                    IsEntry = true,
                    IsTerminal = true,
                    NodeType = QuestNodeType.Condition,
                    Config = "{}"
                }
            }
        };
        return quest;
    }

    private static async Task<int> RunCountAsync(InMemoryQuestRunStore runStore, Guid questId) =>
        (await runStore.GetByQuestIdAsync(questId)).Result!.Count();

    // ═══════════════════════════════════════════════════════════════════
    // AC1 — public + Open quest: no regression, any avatar may start.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_PublicOpenQuest_NonOwner_IsNotInviteGated()
    {
        var (manager, questStore, runStore) = Build();
        var quest = PublishedQuest(QuestRunAccess.Open);
        await questStore.UpsertQuestAsync(quest);

        var result = await manager.ExecuteAsync(quest.Id, Outsider);

        // Open + public ⇒ the invite gate must NOT fire. A run row proves the
        // start was permitted through the LoadStartableQuest chokepoint.
        result.Message.Should().NotContainEquivalentOf(InviteRejectionFragment);
        (await RunCountAsync(runStore, quest.Id)).Should().Be(1, "an Open public quest starts for any avatar");
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC2 — public + InviteOnly quest: gated by the invite set.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_InviteOnlyQuest_NonOwnerNotInvited_IsRejectedWithInviteMessage()
    {
        var (manager, questStore, runStore) = Build();
        var quest = PublishedQuest(QuestRunAccess.InviteOnly); // empty invite set
        await questStore.UpsertQuestAsync(quest);

        var result = await manager.ExecuteAsync(quest.Id, Outsider);

        result.IsError.Should().BeTrue();
        result.Message.Should().ContainEquivalentOf(InviteRejectionFragment,
            "a non-invited runner must be told to request access");
        (await RunCountAsync(runStore, quest.Id)).Should().Be(0,
            "the gate rejects before any run row is written");
    }

    [Fact]
    public async Task Execute_InviteOnlyQuest_NonOwnerWhoIsInvited_Succeeds()
    {
        var (manager, questStore, runStore) = Build();
        var quest = PublishedQuest(QuestRunAccess.InviteOnly, Invited);
        await questStore.UpsertQuestAsync(quest);

        var result = await manager.ExecuteAsync(quest.Id, Invited);

        result.Message.Should().NotContainEquivalentOf(InviteRejectionFragment);
        (await RunCountAsync(runStore, quest.Id)).Should().Be(1,
            "an invited avatar clears the gate and starts a run");
    }

    [Fact]
    public async Task Execute_InviteOnlyQuest_Owner_AlwaysSucceeds_RegardlessOfRunAccess()
    {
        var (manager, questStore, runStore) = Build();
        var quest = PublishedQuest(QuestRunAccess.InviteOnly); // owner NOT in invite list
        await questStore.UpsertQuestAsync(quest);

        var result = await manager.ExecuteAsync(quest.Id, Owner);

        result.Message.Should().NotContainEquivalentOf(InviteRejectionFragment);
        (await RunCountAsync(runStore, quest.Id)).Should().Be(1,
            "the owner is implicitly invited and never gated");
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC4 — owner directly invites/revokes; revoke blocks future starts.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InviteAvatar_AppendsToInvitedSet_AndIsIdempotent()
    {
        var (manager, questStore, _) = Build();
        var quest = PublishedQuest(QuestRunAccess.InviteOnly);
        await questStore.UpsertQuestAsync(quest);

        var first = await manager.InviteAvatarAsync(quest.Id, Owner, Invited);
        first.IsError.Should().BeFalse();

        var afterFirst = (await questStore.GetQuestAsync(quest.Id)).Result!;
        afterFirst.InvitedAvatarIds.Should().ContainSingle().Which.Should().Be(Invited);

        // Idempotent: re-inviting the same avatar does not duplicate.
        var second = await manager.InviteAvatarAsync(quest.Id, Owner, Invited);
        second.IsError.Should().BeFalse();

        var afterSecond = (await questStore.GetQuestAsync(quest.Id)).Result!;
        afterSecond.InvitedAvatarIds.Should().ContainSingle("re-invite must not duplicate");
    }

    [Fact]
    public async Task RevokeInvite_RemovesAvatar_AndBlocksFutureStarts()
    {
        var (manager, questStore, runStore) = Build();
        var quest = PublishedQuest(QuestRunAccess.InviteOnly, Invited);
        await questStore.UpsertQuestAsync(quest);

        var revoke = await manager.RevokeInviteAsync(quest.Id, Owner, Invited);
        revoke.IsError.Should().BeFalse();

        var reloaded = (await questStore.GetQuestAsync(quest.Id)).Result!;
        reloaded.InvitedAvatarIds.Should().NotContain(Invited);

        // The now-uninvited avatar is gated on a fresh start.
        var start = await manager.ExecuteAsync(quest.Id, Invited);
        start.IsError.Should().BeTrue();
        start.Message.Should().ContainEquivalentOf(InviteRejectionFragment);
        (await RunCountAsync(runStore, quest.Id)).Should().Be(0);
    }

    [Fact]
    public async Task InviteAvatar_ByNonOwner_IsRejected()
    {
        var (manager, questStore, _) = Build();
        var quest = PublishedQuest(QuestRunAccess.InviteOnly);
        await questStore.UpsertQuestAsync(quest);

        // AC5: owner-only op — a non-owner cannot mint invites on another's quest.
        var result = await manager.InviteAvatarAsync(quest.Id, Outsider, Invited);

        result.IsError.Should().BeTrue();
        var reloaded = (await questStore.GetQuestAsync(quest.Id)).Result!;
        reloaded.InvitedAvatarIds.Should().BeEmpty("a non-owner invite must not mutate the set");
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC5 — fork IDOR: a non-invited avatar cannot fork an InviteOnly quest.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Fork_InviteOnlyQuest_ByNonInvitedAvatar_IsRejected()
    {
        var (manager, questStore, runStore) = Build();
        var quest = PublishedQuest(QuestRunAccess.InviteOnly);
        await questStore.UpsertQuestAsync(quest);

        // Seed a Running run owned by the outsider on this InviteOnly quest (as if a
        // prior invitation had been revoked, or the run predates the gate). A fork
        // re-runs the quest nodes, so the invite check applies to the originating
        // quest for a non-owner forker.
        var run = new QuestRun
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            AvatarId = Outsider,
            Status = QuestRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runStore.CreateAsync(run);

        var fork = await manager.ForkAsync(run.Id, quest.Nodes[0].Id, "branch", Outsider);

        fork.IsError.Should().BeTrue(
            "a non-invited avatar cannot fork an InviteOnly quest (fork re-runs its nodes)");
    }
}
