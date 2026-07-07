using FluentAssertions;
using AZOA.WebAPI.Interfaces.QuestExecution;
using AZOA.WebAPI.Interfaces.Stores;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Quest;
using AZOA.WebAPI.Models.Responses;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Quest;
using AZOA.WebAPI.Tests.Fakes;
using Moq;
using Xunit;
using QuestEntity = AZOA.WebAPI.Models.Quest.Quest;

namespace AZOA.WebAPI.Tests.Quest;

/// <summary>
/// quest-invitations-approval AC3 (request/approval state machine) + AC5
/// (IDOR scoping on decide/list/withdraw). The access-request store is Mocked so
/// the ≤1-Pending idempotency invariant and terminal-immutability can be pinned by
/// controlling exactly what the store returns (existing Pending vs missing vs
/// terminal). The quest store is a real InMemory store so the approve→append→persist
/// round trip is observable on Quest.InvitedAvatarIds.
/// </summary>
public class QuestAccessRequestStateMachineTests
{
    private static readonly Guid Owner     = Guid.NewGuid();
    private static readonly Guid Requester = Guid.NewGuid();
    private static readonly Guid Stranger  = Guid.NewGuid();

    // ─── Scaffolding ───

    private static (QuestManager manager, InMemoryQuestStore questStore, Mock<IQuestAccessRequestStore> access)
        Build()
    {
        var questStore = new InMemoryQuestStore();
        var access = new Mock<IQuestAccessRequestStore>();

        // CreateAsync / UpdateAsync echo the row back by default (success).
        access.Setup(s => s.CreateAsync(It.IsAny<QuestAccessRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((QuestAccessRequest r, CancellationToken _) =>
                  new AZOAResult<QuestAccessRequest> { Result = r, Message = "Created." });
        access.Setup(s => s.UpdateAsync(It.IsAny<QuestAccessRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync((QuestAccessRequest r, CancellationToken _) =>
                  new AZOAResult<QuestAccessRequest> { Result = r, Message = "Updated." });

        var manager = new QuestManager(
            questStore,
            new InMemoryQuestRunStore(),
            new InMemoryQuestNodeExecutionStore(),
            new QuestDagValidator(),
            new QuestDagExecutabilityValidator(),
            new QuestNodeHandlerRegistry(Array.Empty<IQuestNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough(),
            configuration: null,
            accessRequestStore: access.Object);

        return (manager, questStore, access);
    }

    private static QuestEntity InviteOnlyQuest(params Guid[] invited)
    {
        var quest = new QuestEntity
        {
            Id = Guid.NewGuid(),
            Name = "Gated",
            AvatarId = Owner,
            IsPublic = true,
            Status = QuestStatus.Active,
            Version = 1,
            RunAccess = QuestRunAccess.InviteOnly,
            InvitedAvatarIds = invited.ToList()
        };
        return quest;
    }

    /// <summary>A "no pending exists" result: NON-error null (store's not-found convention),
    /// distinct from a store fault so the manager opens a fresh request rather than failing closed.</summary>
    private static AZOAResult<QuestAccessRequest> NotFound() =>
        new() { IsError = false, Result = null, Message = "No pending request." };

    /// <summary>A store FAULT on the pending lookup (IsError, no result) — the manager must fail closed.</summary>
    private static AZOAResult<QuestAccessRequest> LookupFault() =>
        new() { IsError = true, Result = null, Message = "store unavailable" };

    private static void SetupNoPending(Mock<IQuestAccessRequestStore> access) =>
        access.Setup(s => s.GetPendingForQuestAndRequesterAsync(
                  It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(NotFound());

    private static void SetupPendingLookupFault(Mock<IQuestAccessRequestStore> access) =>
        access.Setup(s => s.GetPendingForQuestAndRequesterAsync(
                  It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(LookupFault());

    private static void SetupExistingPending(Mock<IQuestAccessRequestStore> access, QuestAccessRequest existing) =>
        access.Setup(s => s.GetPendingForQuestAndRequesterAsync(
                  existing.QuestId, existing.RequesterAvatarId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<QuestAccessRequest> { Result = existing, Message = "Found." });

    private static void SetupGetById(Mock<IQuestAccessRequestStore> access, QuestAccessRequest req) =>
        access.Setup(s => s.GetByIdAsync(req.Id, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AZOAResult<QuestAccessRequest> { Result = req, Message = "Found." });

    // ═══════════════════════════════════════════════════════════════════
    // AC3 — RequestAccessAsync idempotency (≤1 Pending per quest,requester).
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task RequestAccess_NoOpenPending_OpensExactlyOne()
    {
        var (manager, questStore, access) = Build();
        var quest = InviteOnlyQuest();
        await questStore.UpsertQuestAsync(quest);
        SetupNoPending(access);

        var result = await manager.RequestAccessAsync(quest.Id, Requester, "please");

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(QuestAccessRequestStatus.Pending);
        result.Result.QuestId.Should().Be(quest.Id);
        result.Result.RequesterAvatarId.Should().Be(Requester);
        access.Verify(s => s.CreateAsync(It.IsAny<QuestAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RequestAccess_WhilePendingExists_ReturnsExisting_DoesNotDuplicate()
    {
        var (manager, questStore, access) = Build();
        var quest = InviteOnlyQuest();
        await questStore.UpsertQuestAsync(quest);

        var existing = new QuestAccessRequest
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            RequesterAvatarId = Requester,
            Status = QuestAccessRequestStatus.Pending
        };
        SetupExistingPending(access, existing);

        var result = await manager.RequestAccessAsync(quest.Id, Requester, "again");

        result.IsError.Should().BeFalse();
        result.Result!.Id.Should().Be(existing.Id, "the live Pending request is returned, not a fresh one");
        // The load-bearing idempotency assertion: no second row is written.
        access.Verify(s => s.CreateAsync(It.IsAny<QuestAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestAccess_ByOwner_IsRejected_NoRequestNeeded()
    {
        var (manager, questStore, access) = Build();
        var quest = InviteOnlyQuest();
        await questStore.UpsertQuestAsync(quest);
        SetupNoPending(access);

        var result = await manager.RequestAccessAsync(quest.Id, Owner);

        result.IsError.Should().BeTrue("the owner already has run access — no request needed");
        access.Verify(s => s.CreateAsync(It.IsAny<QuestAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestAccess_ByAlreadyInvited_IsRejected_NoRequestNeeded()
    {
        var (manager, questStore, access) = Build();
        var quest = InviteOnlyQuest(Requester); // already invited
        await questStore.UpsertQuestAsync(quest);
        SetupNoPending(access);

        var result = await manager.RequestAccessAsync(quest.Id, Requester);

        result.IsError.Should().BeTrue("an already-invited avatar needs no request");
        access.Verify(s => s.CreateAsync(It.IsAny<QuestAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestAccess_OnOpenQuest_IsRejected_NoRequestNeeded()
    {
        var (manager, questStore, access) = Build();
        var quest = InviteOnlyQuest();
        quest.RunAccess = QuestRunAccess.Open; // public + Open: anyone may run, no gate
        await questStore.UpsertQuestAsync(quest);
        SetupNoPending(access);

        var result = await manager.RequestAccessAsync(quest.Id, Requester);

        result.IsError.Should().BeTrue("an Open quest needs no access request — anyone may run it");
        access.Verify(s => s.CreateAsync(It.IsAny<QuestAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestAccess_WhenPendingLookupFaults_FailsClosed_NoDuplicate()
    {
        var (manager, questStore, access) = Build();
        var quest = InviteOnlyQuest();
        await questStore.UpsertQuestAsync(quest);
        SetupPendingLookupFault(access);

        var result = await manager.RequestAccessAsync(quest.Id, Requester);

        result.IsError.Should().BeTrue("a store fault on the idempotency lookup must fail closed");
        // The load-bearing guard: a fault must NOT fall through to create a duplicate Pending.
        access.Verify(s => s.CreateAsync(It.IsAny<QuestAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequestAccess_AfterTerminalState_OpensFreshPending()
    {
        var (manager, questStore, access) = Build();
        var quest = InviteOnlyQuest();
        await questStore.UpsertQuestAsync(quest);
        // A prior Rejected/Withdrawn request leaves NO open Pending, so GetPending
        // reports missing and a fresh request may be opened.
        SetupNoPending(access);

        var result = await manager.RequestAccessAsync(quest.Id, Requester);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(QuestAccessRequestStatus.Pending);
        access.Verify(s => s.CreateAsync(It.IsAny<QuestAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Once, "a terminal prior request does not block a fresh Pending");
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC3 — DecideAccessRequestAsync approve / reject / terminal immutability.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Decide_Approve_SetsApproved_AppendsInvite_AndPersistsQuest()
    {
        var (manager, questStore, access) = Build();
        var quest = InviteOnlyQuest();
        await questStore.UpsertQuestAsync(quest);

        var pending = new QuestAccessRequest
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            RequesterAvatarId = Requester,
            Status = QuestAccessRequestStatus.Pending
        };
        SetupGetById(access, pending);

        var result = await manager.DecideAccessRequestAsync(pending.Id, Owner, approve: true, reason: "ok");

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(QuestAccessRequestStatus.Approved);

        // Requester is appended to the quest's invite set and the quest is persisted.
        var reloaded = (await questStore.GetQuestAsync(quest.Id)).Result!;
        reloaded.InvitedAvatarIds.Should().Contain(Requester, "approval mints the invitation");

        access.Verify(s => s.UpdateAsync(
            It.Is<QuestAccessRequest>(r => r.Status == QuestAccessRequestStatus.Approved),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Decide_Reject_SetsRejected_AndDoesNotInvite()
    {
        var (manager, questStore, access) = Build();
        var quest = InviteOnlyQuest();
        await questStore.UpsertQuestAsync(quest);

        var pending = new QuestAccessRequest
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            RequesterAvatarId = Requester,
            Status = QuestAccessRequestStatus.Pending
        };
        SetupGetById(access, pending);

        var result = await manager.DecideAccessRequestAsync(pending.Id, Owner, approve: false, reason: "no");

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(QuestAccessRequestStatus.Rejected);

        var reloaded = (await questStore.GetQuestAsync(quest.Id)).Result!;
        reloaded.InvitedAvatarIds.Should().NotContain(Requester, "a rejection mints no invitation");
    }

    [Fact]
    public async Task Decide_OnTerminalRequest_IsRejected_TerminalImmutability()
    {
        var (manager, questStore, access) = Build();
        var quest = InviteOnlyQuest();
        await questStore.UpsertQuestAsync(quest);

        var alreadyApproved = new QuestAccessRequest
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            RequesterAvatarId = Requester,
            Status = QuestAccessRequestStatus.Approved, // terminal
            DecidedAt = DateTime.UtcNow,
            DecidedByAvatarId = Owner
        };
        SetupGetById(access, alreadyApproved);

        var result = await manager.DecideAccessRequestAsync(alreadyApproved.Id, Owner, approve: false);

        result.IsError.Should().BeTrue("terminal requests are immutable — no re-decision");
        access.Verify(s => s.UpdateAsync(It.IsAny<QuestAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC3 — WithdrawAccessRequestAsync.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Withdraw_PendingRequest_ByRequester_SetsWithdrawn()
    {
        var (manager, questStore, access) = Build();
        var quest = InviteOnlyQuest();
        await questStore.UpsertQuestAsync(quest);

        var pending = new QuestAccessRequest
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            RequesterAvatarId = Requester,
            Status = QuestAccessRequestStatus.Pending
        };
        SetupGetById(access, pending);

        var result = await manager.WithdrawAccessRequestAsync(pending.Id, Requester);

        result.IsError.Should().BeFalse();
        result.Result!.Status.Should().Be(QuestAccessRequestStatus.Withdrawn);
        access.Verify(s => s.UpdateAsync(
            It.Is<QuestAccessRequest>(r => r.Status == QuestAccessRequestStatus.Withdrawn),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ═══════════════════════════════════════════════════════════════════
    // AC5 — IDOR scoping.
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Decide_ByNonOwnerOfQuest_IsRejected()
    {
        var (manager, questStore, access) = Build();
        var quest = InviteOnlyQuest();
        await questStore.UpsertQuestAsync(quest);

        var pending = new QuestAccessRequest
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            RequesterAvatarId = Requester,
            Status = QuestAccessRequestStatus.Pending
        };
        SetupGetById(access, pending);

        // Stranger is not the owner of the request's quest.
        var result = await manager.DecideAccessRequestAsync(pending.Id, Stranger, approve: true);

        result.IsError.Should().BeTrue("only the request's quest owner may decide it");
        access.Verify(s => s.UpdateAsync(It.IsAny<QuestAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);

        var reloaded = (await questStore.GetQuestAsync(quest.Id)).Result!;
        reloaded.InvitedAvatarIds.Should().NotContain(Requester);
    }

    [Fact]
    public async Task ListAccessRequests_ByNonOwner_IsRejected()
    {
        var (manager, questStore, _) = Build();
        var quest = InviteOnlyQuest();
        await questStore.UpsertQuestAsync(quest);

        var result = await manager.ListAccessRequestsAsync(quest.Id, Stranger);

        result.IsError.Should().BeTrue("the approval queue is owner-only");
    }

    [Fact]
    public async Task Withdraw_ByNonRequester_IsRejected()
    {
        var (manager, questStore, access) = Build();
        var quest = InviteOnlyQuest();
        await questStore.UpsertQuestAsync(quest);

        var pending = new QuestAccessRequest
        {
            Id = Guid.NewGuid(),
            QuestId = quest.Id,
            RequesterAvatarId = Requester,
            Status = QuestAccessRequestStatus.Pending
        };
        SetupGetById(access, pending);

        // Stranger is not the requester who owns this request.
        var result = await manager.WithdrawAccessRequestAsync(pending.Id, Stranger);

        result.IsError.Should().BeTrue("only the requester may withdraw their own request");
        access.Verify(s => s.UpdateAsync(It.IsAny<QuestAccessRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
