using AZOA.WebAPI.Interfaces.ActionExecution;
using AZOA.WebAPI.Managers;
using AZOA.WebAPI.Models.Actions;
using AZOA.WebAPI.Models.Requests;
using AZOA.WebAPI.Providers.Stores;
using AZOA.WebAPI.Services;
using AZOA.WebAPI.Services.Actions;
using AZOA.WebAPI.Tests.Fakes;
using AZOA.WebAPI.Tests.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;
using ActionEntity = AZOA.WebAPI.Models.Actions.ActionDefinition;

namespace AZOA.WebAPI.Tests.Actions;

public sealed class ActionPublishApprovalTests
{
    private static readonly Guid OwnerUserId = Guid.NewGuid();
    private static readonly Guid OperatorUserId = Guid.NewGuid();

    private static (ActionManager manager, InMemoryActionStore store, ActionPublishApprovalPolicy policy)
        BuildManager(bool requireApproval = true, ActionEntity? seed = null)
    {
        var store = new InMemoryActionStore();
        var runStore = new InMemoryActionRunStore();
        var executions = new InMemoryActionNodeExecutionStore();
        if (seed != null)
            store.UpsertActionAsync(seed).GetAwaiter().GetResult();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ActionDefinition:RequireOperatorApprovalForPublish"] = requireApproval ? "true" : "false"
            })
            .Build();
        var policy = new ActionPublishApprovalPolicy(config);

        var manager = new ActionManager(
            store,
            runStore,
            executions,
            new ActionDagValidator(),
            new ActionDagExecutabilityValidator(),
            new ActionNodeHandlerRegistry(Array.Empty<IActionNodeHandler>()),
            new InMemorySagaStore(),
            WalletManagerMocks.Empty(),
            BlockchainProviderFactoryFakes.Returning(),
            BindingResolverFakes.PassThrough(),
            configuration: config,
            workflowAdmission: new InMemoryActionWorkflowAdmissionStore(runStore, executions, new InMemorySagaStore()),
            publishApprovalPolicy: policy);

        return (manager, store, policy);
    }

    private static ActionEntity ValidDraftAction()
    {
        var actionId = Guid.NewGuid();
        var entryId = Guid.NewGuid();
        var terminalId = Guid.NewGuid();
        return new ActionEntity
        {
            Id = actionId,
            Name = "Approval-gated workflow",
            Description = "Validates operator publish approval gate.",
            UserId = OwnerUserId,
            IsPublic = true,
            Status = ActionStatus.Draft,
            Nodes =
            [
                new ActionNode { Id = entryId, ActionId = actionId, Name = "Entry", NodeType = ActionNodeType.Condition, IsEntry = true, Config = "{}" },
                new ActionNode { Id = terminalId, ActionId = actionId, Name = "Terminal", NodeType = ActionNodeType.Condition, IsTerminal = true, Config = "{}" },
            ],
            Edges =
            [
                new ActionEdge { Id = Guid.NewGuid(), ActionId = actionId, SourceNodeId = entryId, TargetNodeId = terminalId, EdgeType = ActionEdgeType.Control },
            ],
        };
    }

    [Fact]
    public async Task Publish_WhenOperatorApprovalEnabled_StagesPendingApprovalAndBlocksExecution()
    {
        var action = ValidDraftAction();
        var (manager, _, _) = BuildManager(requireApproval: true, seed: action);

        var publish = await manager.PublishAsync(action.Id, OwnerUserId);

        publish.IsError.Should().BeFalse();
        publish.Message.Should().Contain("operator publish approval");
        publish.Result!.Status.Should().Be(ActionStatus.Draft);
        publish.Result.Metadata[PublishApprovalMetadataKeys.Status].Should().Be(ActionPublishApprovalStatuses.PendingApproval);
        publish.Result.Metadata[PublishApprovalMetadataKeys.VersionHash].Should().NotBeNullOrWhiteSpace();

        var run = await manager.StartWorkflowRunAsync(action.Id, OwnerUserId, new AZOARequest { IdempotencyKey = "pending-run-1" });
        run.IsError.Should().BeTrue();
        run.Message.Should().Contain("published (Active)");
    }

    [Fact]
    public async Task OperatorApprove_TransitionsPendingWorkflowToActive_AndRejectsPostSubmissionTampering()
    {
        var action = ValidDraftAction();
        var (manager, _, _) = BuildManager(requireApproval: true, seed: action);
        await manager.PublishAsync(action.Id, OwnerUserId);

        var pendingList = await manager.ListPublishApprovalsAsync(ActionPublishApprovalStatuses.PendingApproval);
        pendingList.IsError.Should().BeFalse();
        pendingList.Result.Should().ContainSingle(item => item.ActionId == action.Id && item.ApprovalStatus == ActionPublishApprovalStatuses.PendingApproval);

        // Mutating a node after requesting approval breaks hash match (bait-and-switch guard)
        action.Nodes[0].Config = "{\"tampered\":true}";
        var tamperedDecision = await manager.DecidePublishApprovalAsync(action.Id, approve: true, reason: "Looks good", OperatorUserId, "admin:ops");
        tamperedDecision.IsError.Should().BeTrue();
        tamperedDecision.Message.Should().Contain("changed after publish approval was requested");

        // Restore original config and resubmit cleanly
        action.Nodes[0].Config = "{}";
        await manager.PublishAsync(action.Id, OwnerUserId);

        var approved = await manager.DecidePublishApprovalAsync(action.Id, approve: true, reason: "Approved by node operator", OperatorUserId, "admin:ops");
        approved.IsError.Should().BeFalse();
        approved.Result!.Status.Should().Be(nameof(ActionStatus.Active));
        approved.Result.ApprovalStatus.Should().Be(ActionPublishApprovalStatuses.Approved);
        approved.Result.DecidedBy.Should().Be("admin:ops");

        var loaded = await manager.GetAsync(action.Id, OwnerUserId);
        loaded.Result!.Status.Should().Be(ActionStatus.Active);
        loaded.Result.PublishedVersionHash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task OperatorReject_RequiresReason_AndKeepsWorkflowInDraft()
    {
        var action = ValidDraftAction();
        var (manager, _, _) = BuildManager(requireApproval: true, seed: action);
        await manager.PublishAsync(action.Id, OwnerUserId);

        var missingReason = await manager.DecidePublishApprovalAsync(action.Id, approve: false, reason: "  ", OperatorUserId, "legacy-operator");
        missingReason.IsError.Should().BeTrue();
        missingReason.Message.Should().Contain("reason is required");

        var rejected = await manager.DecidePublishApprovalAsync(action.Id, approve: false, reason: "Add a Critic failure handler first.", OperatorUserId, "legacy-operator");
        rejected.IsError.Should().BeFalse();
        rejected.Result!.Status.Should().Be(nameof(ActionStatus.Draft));
        rejected.Result.ApprovalStatus.Should().Be(ActionPublishApprovalStatuses.Rejected);
        rejected.Result.Reason.Should().Be("Add a Critic failure handler first.");
    }

    [Fact]
    public async Task Unpublish_ClearsApprovalMetadata_AndPolicyToggleTakesEffectImmediately()
    {
        var action = ValidDraftAction();
        var (manager, _, policy) = BuildManager(requireApproval: false, seed: action);

        var policyView = manager.SetPublishApprovalPolicy(true, "admin:ops");
        policyView.RequireOperatorApprovalForPublish.Should().BeTrue();
        policy.RequireOperatorApprovalForPublish.Should().BeTrue();

        await manager.PublishAsync(action.Id, OwnerUserId);
        await manager.DecidePublishApprovalAsync(action.Id, approve: true, reason: "Verified", OperatorUserId, "admin:ops");

        var unpublished = await manager.UnpublishAsync(action.Id, OwnerUserId);
        unpublished.IsError.Should().BeFalse();
        unpublished.Result!.Status.Should().Be(ActionStatus.Draft);
        unpublished.Result.Metadata.Should().NotContainKey(PublishApprovalMetadataKeys.Status);
        unpublished.Result.Metadata.Should().NotContainKey(PublishApprovalMetadataKeys.VersionHash);

        // Next publish requires fresh approval
        var republished = await manager.PublishAsync(action.Id, OwnerUserId);
        republished.IsError.Should().BeFalse();
        republished.Result!.Status.Should().Be(ActionStatus.Draft);
        republished.Result.Metadata[PublishApprovalMetadataKeys.Status].Should().Be(ActionPublishApprovalStatuses.PendingApproval);
    }
}
