using FluentAssertions;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Services.Actions.Handlers;
using AZOA.WebAPI.Tests.Fakes;
using Moq;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// action-invitations-approval AC1/AC2/AC4/AC5(fork). Exercises the run-start
/// invite gate (LoadStartableActionAsync chokepoint, shared by ExecuteAsync +
/// StartWorkflowRunAsync) through the public <see cref="ActionManager.ExecuteAsync"/>
/// seam, plus owner-side InviteUserAsync/RevokeInviteAsync and the fork IDOR
/// path. Real InMemory stores so the load-mutate-save round trips persist between
/// manager calls; the access-request store is Mocked (no InMemory impl exists).
///
/// Gate assertion strategy: a REJECTED start surfaces the actionable invitation
/// message AND writes no run row; a PERMITTED start writes a run row (proving the
/// gate let it through) — robust regardless of downstream node-execution outcome.
/// </summary>
public class ActionInvitationsTests
{
    private static readonly Guid Owner    = Guid.NewGuid();
    private static readonly Guid Outsider = Guid.NewGuid();
    private static readonly Guid Invited  = Guid.NewGuid();

    /// <summary>The invitation-required rejection is worded around "invitation".</summary>
    private const string InviteRejectionFragment = "invitation";

    // ─── Scaffolding ───

    private static (ActionManager manager, InMemoryActionStore actionStore, InMemoryActionRunStore runStore)
        Build(Mock<IActionAccessRequestStore>? accessStore = null)
    {
        var actionStore = new InMemoryActionStore();
        var runStore = new InMemoryActionRunStore();
        var execStore = new InMemoryActionNodeExecutionStore();

        var manager = new ActionManager(
            actionStore,
            runStore,
            execStore,
            new ActionDagValidator(),
            new ActionDagExecutabilityValidator(),
            // Register the Condition handler so the economic-consent gate classifies
            // the test's Condition node as NON-economic (fail-closed IsEconomicNode
            // treats an UNregistered type as economic — see ActionEconomicManifestBuilder).
            new ActionNodeHandlerRegistry(new IActionNodeHandler[] { new ConditionNodeHandler() }),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough(),
            configuration: null,
            accessRequestStore: (accessStore ?? new Mock<IActionAccessRequestStore>()).Object);

        return (manager, actionStore, runStore);
    }

    /// <summary>
    /// A published (public + Active) single-node action owned by <see cref="Owner"/>.
    /// One Condition node that is both entry and terminal — the minimal DAG that
    /// passes structural validation and never trips the Tier-2 capability gate.
    /// </summary>
    private static ActionEntity PublishedAction(ActionRunAccess runAccess, params Guid[] invited)
    {
        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            Name = "Gated",
            UserId = Owner,
            IsPublic = true,
            Status = ActionStatus.Active,
            Version = 1,
            PublishedVersionHash = "hash-v1",
            RunAccess = runAccess,
            InvitedUserIds = invited.ToList(),
            Nodes = new List<ActionNode>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    Name = "only",
                    IsEntry = true,
                    IsTerminal = true,
                    NodeType = ActionNodeType.Condition,
                    Config = "{}"
                }
            }
        };
        return action;
    }

    private static async Task<int> RunCountAsync(InMemoryActionRunStore runStore, Guid actionId) =>
        (await runStore.GetByActionIdAsync(actionId)).Result!.Count();

    // ═══════════════════════════════════════════════════════════════════
    // AC1 — public + Open action: no regression, any user may start.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_PublicOpenAction_NonOwner_IsNotInviteGated()
    {
        var (manager, actionStore, runStore) = Build();
        var action = PublishedAction(ActionRunAccess.Open);
        await actionStore.UpsertActionAsync(action);

        var result = await manager.ExecuteAsync(action.Id, Outsider);

        // Open + public ⇒ the invite gate must NOT fire. A run row proves the
        // start was permitted through the LoadStartableAction chokepoint.
        result.Message.Should().NotContainEquivalentOf(InviteRejectionFragment);
        (await RunCountAsync(runStore, action.Id)).Should().Be(1, "an Open public action starts for any user");
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC2 — public + InviteOnly action: gated by the invite set.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Execute_InviteOnlyAction_NonOwnerNotInvited_IsRejectedWithInviteMessage()
    {
        var (manager, actionStore, runStore) = Build();
        var action = PublishedAction(ActionRunAccess.InviteOnly); // empty invite set
        await actionStore.UpsertActionAsync(action);

        var result = await manager.ExecuteAsync(action.Id, Outsider);

        result.IsError.Should().BeTrue();
        result.Message.Should().ContainEquivalentOf(InviteRejectionFragment,
            "a non-invited runner must be told to request access");
        (await RunCountAsync(runStore, action.Id)).Should().Be(0,
            "the gate rejects before any run row is written");
    }

    [Fact]
    public async Task Execute_InviteOnlyAction_NonOwnerWhoIsInvited_Succeeds()
    {
        var (manager, actionStore, runStore) = Build();
        var action = PublishedAction(ActionRunAccess.InviteOnly, Invited);
        await actionStore.UpsertActionAsync(action);

        var result = await manager.ExecuteAsync(action.Id, Invited);

        result.Message.Should().NotContainEquivalentOf(InviteRejectionFragment);
        (await RunCountAsync(runStore, action.Id)).Should().Be(1,
            "an invited user clears the gate and starts a run");
    }

    [Fact]
    public async Task Execute_InviteOnlyAction_Owner_AlwaysSucceeds_RegardlessOfRunAccess()
    {
        var (manager, actionStore, runStore) = Build();
        var action = PublishedAction(ActionRunAccess.InviteOnly); // owner NOT in invite list
        await actionStore.UpsertActionAsync(action);

        var result = await manager.ExecuteAsync(action.Id, Owner);

        result.Message.Should().NotContainEquivalentOf(InviteRejectionFragment);
        (await RunCountAsync(runStore, action.Id)).Should().Be(1,
            "the owner is implicitly invited and never gated");
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC4 — owner directly invites/revokes; revoke blocks future starts.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task InviteUser_AppendsToInvitedSet_AndIsIdempotent()
    {
        var (manager, actionStore, _) = Build();
        var action = PublishedAction(ActionRunAccess.InviteOnly);
        await actionStore.UpsertActionAsync(action);

        var first = await manager.InviteUserAsync(action.Id, Owner, Invited);
        first.IsError.Should().BeFalse();

        var afterFirst = (await actionStore.GetActionAsync(action.Id)).Result!;
        afterFirst.InvitedUserIds.Should().ContainSingle().Which.Should().Be(Invited);

        // Idempotent: re-inviting the same user does not duplicate.
        var second = await manager.InviteUserAsync(action.Id, Owner, Invited);
        second.IsError.Should().BeFalse();

        var afterSecond = (await actionStore.GetActionAsync(action.Id)).Result!;
        afterSecond.InvitedUserIds.Should().ContainSingle("re-invite must not duplicate");
    }

    [Fact]
    public async Task RevokeInvite_RemovesUser_AndBlocksFutureStarts()
    {
        var (manager, actionStore, runStore) = Build();
        var action = PublishedAction(ActionRunAccess.InviteOnly, Invited);
        await actionStore.UpsertActionAsync(action);

        var revoke = await manager.RevokeInviteAsync(action.Id, Owner, Invited);
        revoke.IsError.Should().BeFalse();

        var reloaded = (await actionStore.GetActionAsync(action.Id)).Result!;
        reloaded.InvitedUserIds.Should().NotContain(Invited);

        // The now-uninvited user is gated on a fresh start.
        var start = await manager.ExecuteAsync(action.Id, Invited);
        start.IsError.Should().BeTrue();
        start.Message.Should().ContainEquivalentOf(InviteRejectionFragment);
        (await RunCountAsync(runStore, action.Id)).Should().Be(0);
    }

    [Fact]
    public async Task InviteUser_ByNonOwner_IsRejected()
    {
        var (manager, actionStore, _) = Build();
        var action = PublishedAction(ActionRunAccess.InviteOnly);
        await actionStore.UpsertActionAsync(action);

        // AC5: owner-only op — a non-owner cannot mint invites on another's action.
        var result = await manager.InviteUserAsync(action.Id, Outsider, Invited);

        result.IsError.Should().BeTrue();
        var reloaded = (await actionStore.GetActionAsync(action.Id)).Result!;
        reloaded.InvitedUserIds.Should().BeEmpty("a non-owner invite must not mutate the set");
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC5 — fork IDOR: a non-invited user cannot fork an InviteOnly action.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Fork_InviteOnlyAction_ByNonInvitedUser_IsRejected()
    {
        var (manager, actionStore, runStore) = Build();
        var action = PublishedAction(ActionRunAccess.InviteOnly);
        await actionStore.UpsertActionAsync(action);

        // Seed a Running run owned by the outsider on this InviteOnly action (as if a
        // prior invitation had been revoked, or the run predates the gate). A fork
        // re-runs the action nodes, so the invite check applies to the originating
        // action for a non-owner forker.
        var run = new ActionRun
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            UserId = Outsider,
            Status = ActionRunStatus.Running,
            StartedAt = DateTime.UtcNow
        };
        await runStore.CreateAsync(run);

        var fork = await manager.ForkAsync(run.Id, action.Nodes[0].Id, "branch", Outsider);

        fork.IsError.Should().BeTrue(
            "a non-invited user cannot fork an InviteOnly action (fork re-runs its nodes)");
    }
}
