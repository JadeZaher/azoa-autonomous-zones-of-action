using FluentAssertions;
using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Tests.Fakes;
using Moq;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions;

/// <summary>
/// action-invitations-approval AC3 (request/approval state machine) + AC5
/// (IDOR scoping on decide/list/withdraw). The access-request store is Mocked so
/// the ≤1-Pending idempotency invariant and terminal-immutability can be pinned by
/// controlling exactly what the store returns (existing Pending vs missing vs
/// terminal). The action store is a real InMemory store so the approve→append→persist
/// round trip is observable on ActionDefinition.InvitedUserIds.
/// </summary>
public class ActionAccessRequestStateMachineTests
{
    private static readonly Guid Owner     = Guid.NewGuid();
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Stranger  = Guid.NewGuid();

    // ─── Scaffolding ───

    private static (ActionManager manager, InMemoryActionStore actionStore, Mock<IActionAccessRequestStore> access)
        Build()
    {
        var actionStore = new InMemoryActionStore();
        var access = new Mock<IActionAccessRequestStore>();

        // CreateAsync / UpdateAsync echo the row back by default (success).
        access.Setup(s => s.CreateAsync(It.IsAny<ActionAccessRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((ActionAccessRequest r, CancellationToken _) =>
                  new AZOAResult<ActionAccessRequest> { Result = r, Message = "Created." });
        access.Setup(s => s.UpdateAsync(It.IsAny<ActionAccessRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((ActionAccessRequest r, CancellationToken _) =>
                  new AZOAResult<ActionAccessRequest> { Result = r, Message = "Updated." });

        var manager = new ActionManager(
            actionStore,
            new InMemoryActionRunStore(),
            new InMemoryActionNodeExecutionStore(),
            new ActionDagValidator(),
            new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(Array.Empty<IActionNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough(),
            configuration: null,
            accessRequestStore: access.Object);

        return (manager, actionStore, access);
    }

    private static ActionEntity InviteOnlyAction(params Guid[] invited)
    {
        var action = new ActionEntity
        {
            Id = Guid.NewGuid(),
            Name = "Gated",
            UserId = Owner,
            IsPublic = true,
            Status = ActionStatus.Active,
            Version = 1,
            RunAccess = ActionRunAccess.InviteOnly,
            InvitedUserIds = invited.ToList()
        };
        return action;
    }

    /// <summary>A "no pending exists" result: NON-error null (store's not-found convention),
    /// distinct from a store fault so the manager opens a fresh request rather than failing closed.</summary>
    private static AZOAResult<ActionAccessRequest> NotFound() =>
        new() { IsError = false, Result = null, Message = "No pending request." };

    /// <summary>A store FAULT on the pending lookup (IsError, no result) — the manager must fail closed.</summary>
    private static AZOAResult<ActionAccessRequest> LookupFault() =>
        new() { IsError = true, Result = null, Message = "store unavailable" };

    private static void SetupNoPending(Mock<IActionAccessRequestStore> access) =>
        access.Setup(s => s.GetPendingForActionAndRequesterAsync(
                  It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(NotFound());

    private static void SetupPendingLookupFault(Mock<IActionAccessRequestStore> access) =>
        access.Setup(s => s.GetPendingForActionAndRequesterAsync(
                  It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(LookupFault());

    private static void SetupExistingPending(Mock<IActionAccessRequestStore> access, ActionAccessRequest existing) =>
        access.Setup(s => s.GetPendingForActionAndRequesterAsync(
                  existing.ActionId, existing.RequesterUserId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<ActionAccessRequest> { Result = existing, Message = "Found." });

    private static void SetupGetById(Mock<IActionAccessRequestStore> access, ActionAccessRequest req) =>
        access.Setup(s => s.GetByIdAsync(req.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<ActionAccessRequest> { Result = req, Message = "Found." });

    // ═══════════════════════════════════════════════════════════════════
    // AC3 — RequestAccessAsync idempotency (≤1 Pending per action,requester).
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RequestAccess_NoOpenPending_OpensExactlyOne()
    {
        var (manager, actionStore, access) = Build();
        var action = InviteOnlyAction();
        await actionStore.UpsertActionAsync(action);
        SetupNoPending(access);

        var result = await manager.RequestAccessAsync(action.Id, Requester, "please");

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(ActionAccessRequestStatus.Pending);
        result.Result.ActionId.Should().Be(action.Id);
        result.Result.RequesterUserId.Should().Be(Requester);
        access.Verify(s => s.CreateAsync(It.IsAny<ActionAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestAccess_WhilePendingExists_ReturnsExisting_DoesNotDuplicate()
    {
        var (manager, actionStore, access) = Build();
        var action = InviteOnlyAction();
        await actionStore.UpsertActionAsync(action);

        var existing = new ActionAccessRequest
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            RequesterUserId = Requester,
            Status = ActionAccessRequestStatus.Pending
        };
        SetupExistingPending(access, existing);

        var result = await manager.RequestAccessAsync(action.Id, Requester, "again");

        result.IsError.Should().BeFalse();
        result.Result!.Id.Should().Be(existing.Id, "the live Pending request is returned, not a fresh one");
        // The load-bearing idempotency assertion: no second row is written.
        access.Verify(s => s.CreateAsync(It.IsAny<ActionAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestAccess_ByOwner_IsRejected_NoRequestNeeded()
    {
        var (manager, actionStore, access) = Build();
        var action = InviteOnlyAction();
        await actionStore.UpsertActionAsync(action);
        SetupNoPending(access);

        var result = await manager.RequestAccessAsync(action.Id, Owner);

        result.IsError.Should().BeTrue("the owner already has run access — no request needed");
        access.Verify(s => s.CreateAsync(It.IsAny<ActionAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestAccess_ByAlreadyInvited_IsRejected_NoRequestNeeded()
    {
        var (manager, actionStore, access) = Build();
        var action = InviteOnlyAction(Requester); // already invited
        await actionStore.UpsertActionAsync(action);
        SetupNoPending(access);

        var result = await manager.RequestAccessAsync(action.Id, Requester);

        result.IsError.Should().BeTrue("an already-invited user needs no request");
        access.Verify(s => s.CreateAsync(It.IsAny<ActionAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestAccess_OnOpenAction_IsRejected_NoRequestNeeded()
    {
        var (manager, actionStore, access) = Build();
        var action = InviteOnlyAction();
        action.RunAccess = ActionRunAccess.Open; // public + Open: anyone may run, no gate
        await actionStore.UpsertActionAsync(action);
        SetupNoPending(access);

        var result = await manager.RequestAccessAsync(action.Id, Requester);

        result.IsError.Should().BeTrue("an Open action needs no access request — anyone may run it");
        access.Verify(s => s.CreateAsync(It.IsAny<ActionAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestAccess_WhenPendingLookupFaults_FailsClosed_NoDuplicate()
    {
        var (manager, actionStore, access) = Build();
        var action = InviteOnlyAction();
        await actionStore.UpsertActionAsync(action);
        SetupPendingLookupFault(access);

        var result = await manager.RequestAccessAsync(action.Id, Requester);

        result.IsError.Should().BeTrue("a store fault on the idempotency lookup must fail closed");
        // The load-bearing guard: a fault must NOT fall through to create a duplicate Pending.
        access.Verify(s => s.CreateAsync(It.IsAny<ActionAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestAccess_AfterTerminalState_OpensFreshPending()
    {
        var (manager, actionStore, access) = Build();
        var action = InviteOnlyAction();
        await actionStore.UpsertActionAsync(action);
        // A prior Rejected/Withdrawn request leaves NO open Pending, so GetPending
        // reports missing and a fresh request may be opened.
        SetupNoPending(access);

        var result = await manager.RequestAccessAsync(action.Id, Requester);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(ActionAccessRequestStatus.Pending);
        access.Verify(s => s.CreateAsync(It.IsAny<ActionAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Once, "a terminal prior request does not block a fresh Pending");
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC3 — DecideAccessRequestAsync approve / reject / terminal immutability.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Decide_Approve_SetsApproved_AppendsInvite_AndPersistsAction()
    {
        var (manager, actionStore, access) = Build();
        var action = InviteOnlyAction();
        await actionStore.UpsertActionAsync(action);

        var pending = new ActionAccessRequest
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            RequesterUserId = Requester,
            Status = ActionAccessRequestStatus.Pending
        };
        SetupGetById(access, pending);

        var result = await manager.DecideAccessRequestAsync(pending.Id, Owner, approve: true, reason: "ok");

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(ActionAccessRequestStatus.Approved);

        // Requester is appended to the action's invite set and the action is persisted.
        var reloaded = (await actionStore.GetActionAsync(action.Id)).Result!;
        reloaded.InvitedUserIds.Should().Contain(Requester, "approval mints the invitation");

        access.Verify(s => s.UpdateAsync(
            It.Is<ActionAccessRequest>(r => r.Status == ActionAccessRequestStatus.Approved),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Decide_Reject_SetsRejected_AndDoesNotInvite()
    {
        var (manager, actionStore, access) = Build();
        var action = InviteOnlyAction();
        await actionStore.UpsertActionAsync(action);

        var pending = new ActionAccessRequest
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            RequesterUserId = Requester,
            Status = ActionAccessRequestStatus.Pending
        };
        SetupGetById(access, pending);

        var result = await manager.DecideAccessRequestAsync(pending.Id, Owner, approve: false, reason: "no");

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(ActionAccessRequestStatus.Rejected);

        var reloaded = (await actionStore.GetActionAsync(action.Id)).Result!;
        reloaded.InvitedUserIds.Should().NotContain(Requester, "a rejection mints no invitation");
    }

    [Fact]
    public async Task Decide_OnTerminalRequest_IsRejected_TerminalImmutability()
    {
        var (manager, actionStore, access) = Build();
        var action = InviteOnlyAction();
        await actionStore.UpsertActionAsync(action);

        var alreadyApproved = new ActionAccessRequest
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            RequesterUserId = Requester,
            Status = ActionAccessRequestStatus.Approved, // terminal
            DecidedAt = DateTime.UtcNow,
            DecidedByUserId = Owner
        };
        SetupGetById(access, alreadyApproved);

        var result = await manager.DecideAccessRequestAsync(alreadyApproved.Id, Owner, approve: false);

        result.IsError.Should().BeTrue("terminal requests are immutable — no re-decision");
        access.Verify(s => s.UpdateAsync(It.IsAny<ActionAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC3 — WithdrawAccessRequestAsync.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Withdraw_PendingRequest_ByRequester_SetsWithdrawn()
    {
        var (manager, actionStore, access) = Build();
        var action = InviteOnlyAction();
        await actionStore.UpsertActionAsync(action);

        var pending = new ActionAccessRequest
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            RequesterUserId = Requester,
            Status = ActionAccessRequestStatus.Pending
        };
        SetupGetById(access, pending);

        var result = await manager.WithdrawAccessRequestAsync(pending.Id, Requester);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(ActionAccessRequestStatus.Withdrawn);
        access.Verify(s => s.UpdateAsync(
            It.Is<ActionAccessRequest>(r => r.Status == ActionAccessRequestStatus.Withdrawn),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC5 — IDOR scoping.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Decide_ByNonOwnerOfAction_IsRejected()
    {
        var (manager, actionStore, access) = Build();
        var action = InviteOnlyAction();
        await actionStore.UpsertActionAsync(action);

        var pending = new ActionAccessRequest
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            RequesterUserId = Requester,
            Status = ActionAccessRequestStatus.Pending
        };
        SetupGetById(access, pending);

        // Stranger is not the owner of the request's action.
        var result = await manager.DecideAccessRequestAsync(pending.Id, Stranger, approve: true);

        result.IsError.Should().BeTrue("only the request's action owner may decide it");
        access.Verify(s => s.UpdateAsync(It.IsAny<ActionAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var reloaded = (await actionStore.GetActionAsync(action.Id)).Result!;
        reloaded.InvitedUserIds.Should().NotContain(Requester);
    }

    [Fact]
    public async Task ListAccessRequests_ByNonOwner_IsRejected()
    {
        var (manager, actionStore, _) = Build();
        var action = InviteOnlyAction();
        await actionStore.UpsertActionAsync(action);

        var result = await manager.ListAccessRequestsAsync(action.Id, Stranger);

        result.IsError.Should().BeTrue("the approval queue is owner-only");
    }

    [Fact]
    public async Task Withdraw_ByNonRequester_IsRejected()
    {
        var (manager, actionStore, access) = Build();
        var action = InviteOnlyAction();
        await actionStore.UpsertActionAsync(action);

        var pending = new ActionAccessRequest
        {
            Id = Guid.NewGuid(),
            ActionId = action.Id,
            RequesterUserId = Requester,
            Status = ActionAccessRequestStatus.Pending
        };
        SetupGetById(access, pending);

        // Stranger is not the requester who owns this request.
        var result = await manager.WithdrawAccessRequestAsync(pending.Id, Stranger);

        result.IsError.Should().BeTrue("only the requester may withdraw their own request");
        access.Verify(s => s.UpdateAsync(It.IsAny<ActionAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
